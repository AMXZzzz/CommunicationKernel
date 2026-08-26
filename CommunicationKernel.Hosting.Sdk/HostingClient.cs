#nullable disable

// -----------------------------------------------------------------------------
// 文件: HostingClient.cs
// 层级: 客户端层 — 所有 UI 共用
// 作用: 封装所有 gRPC 调用，为 ViewModel 提供强类型异步方法。
//       所有网络 I/O 均在此类完成，ViewModel 无需感知 Protobuf 细节。
// 调用链:
//   UI（WPF / Blazor / 其他）→ HostingClient → gRPC Channel → Hosting.App
// -----------------------------------------------------------------------------

using CommunicationKernel.Hosting.Grpc.V1;   // 由 Grpc.Tools 从 .proto 生成
using Grpc.Core;                                  // RpcException, Metadata
using Grpc.Net.Client;                            // GrpcChannel
using Microsoft.Extensions.Logging;              // ILogger<T>

namespace CommunicationKernel.Hosting.Sdk;

// ============================================================================
// 数据传输对象 — 用于隔离 ViewModel 与 Protobuf 消息类型
// ============================================================================

/// <summary>路由条目 DTO，供 ViewModel 绑定用，不持有 Protobuf 类型。</summary>
public sealed record RouteDto(
    string RouteId,
    string ProtocolId,
    string TransportKind,
    string Address,
    int    Port,
    string Station,
    string SerialPort,
    int    BaudRate);

/// <summary>路由状态事件 DTO，来自 WatchRouteStatus 流。</summary>
public sealed record RouteStatusDto(
    string   RouteId,
    bool     Online,
    string   ErrorCode,
    string   ErrorMessage,
    DateTime Timestamp);

/// <summary>
/// 协议描述符 DTO。
/// UI 依据此对象渲染协议下拉框与设备表单，不内置任何协议知识：
/// 下拉框显示 <see cref="DisplayName"/>，注册时回传 <see cref="ProtocolId"/>；
/// <see cref="SupportedTransports"/> 决定可选的连接方式，并据此展示
/// 「IP+端口」还是「串口+波特率」；<see cref="RequiresStation"/> 决定是否展示站号输入框。
/// </summary>
public sealed record ProtocolDescriptorDto(
    string ProtocolId,
    string DisplayName,
    IReadOnlyList<string> SupportedTransports,
    bool   RequiresStation,
    string StationHint) {

    /// <summary>该协议是否支持指定介质（不区分大小写）。</summary>
    public bool Supports(string transportKind) {
        // 空清单或空介质名无法匹配
        if (SupportedTransports is null || string.IsNullOrWhiteSpace(transportKind))
            return false;

        // 不区分大小写：Protobuf 枚举 ToString 与 UI 传入值可能大小写不同
        foreach (string t in SupportedTransports) {
            if (string.Equals(t, transportKind, StringComparison.OrdinalIgnoreCase))
                return true;
        }

        // 遍历完仍未命中，该协议不支持此介质
        return false;
    }

    /// <summary>默认介质：取列表首项；列表为空时回落到 Tcp。</summary>
    public string DefaultTransport =>
        SupportedTransports is { Count: > 0 } ? SupportedTransports[0] : "Tcp";
}

/// <summary>
/// 宿主机器上的一个可用串口（UI SDK DTO）。
/// </summary>
/// <remarks>
/// 三层命名必须分开：引擎 <c>SerialPortInfo</c>、
/// gRPC <c>SerialPortDescriptor</c>、本类型 <see cref="SerialPortDto"/>。
/// 禁止把本类型命名为 SerialPortDescriptor / SerialPortInfo。
/// </remarks>
/// <param name="PortName">
/// 直接回填到注册请求的设备名。Windows 形如 "COM3"，Linux 形如 "/dev/ttyUSB0"。
/// </param>
/// <param name="Description">
/// 面向操作员的补充说明，可为空。Linux 上通常是 by-id 稳定路径——
/// 多个 USB 串口同时插着时 ttyUSB 编号会随枚举顺序在重启后对调。
/// </param>
public sealed record SerialPortDto(string PortName, string Description) {

    /// <summary>下拉框里显示的文本：有说明时一并给出，便于分辨同类设备。</summary>
    public string Display =>
        string.IsNullOrWhiteSpace(Description) ? PortName : $"{PortName}  ({Description})";
}

// 操作结果类型（ReadResultDto / WriteResultDto / RegisterRouteResultDto /
// RemoveRouteResultDto / HealthResultDto 及其公共基类 HostingOperationResult）
// 定义在 HostResults.cs——它们是对外契约的一部分，与本文件的 gRPC 封装职责分开。

// ============================================================================
// gRPC 客户端封装
// ============================================================================

/// <summary>
/// 封装所有 HostingApi gRPC 调用。
/// 单例生命周期：应用启动时由 DI 容器创建，应用退出时 Dispose。
/// </summary>
public sealed class HostingClient : IHostingClient {

    // ============================================================================
    // 常量
    // ============================================================================

    /// <summary>
    /// 单次读写 RPC 的截止时间（秒）。
    /// 没有截止时间时，PLC 假死会让调用方（尤其是变量轮询循环）无限期挂起，
    /// 使退避重试机制形同虚设。
    /// </summary>
    private const int IoDeadlineSeconds = 10;

    /// <summary>健康检查 RPC 的截止时间（秒）。</summary>
    private const int HealthDeadlineSeconds = 5;

    /// <summary>
    /// 涉及建立/断开 PLC 连接的 RPC 截止时间（秒）。
    /// 比普通读写宽松，因为服务端要完成 TCP 握手或打开串口。
    /// </summary>
    private const int ConnectDeadlineSeconds = 30;

    /// <summary>查询类 RPC 的截止时间（秒）。</summary>
    private const int QueryDeadlineSeconds = 10;

    // ============================================================================
    // 私有字段
    // ============================================================================

    /// <summary>gRPC 通道，持有底层 HTTP/2 连接池。</summary>
    private readonly GrpcChannel _channel;

    /// <summary>由 Grpc.Tools 生成的强类型客户端 stub。</summary>
    private readonly HostingApi.HostingApiClient _stub;

    /// <summary>日志记录器（可为 NullLogger，保持可测试性）。</summary>
    private readonly ILogger<HostingClient> _logger;

    // ============================================================================
    // 构造函数
    // ============================================================================

    /// <param name="serverAddress">gRPC 服务地址，例如 "http://localhost:5000"。</param>
    /// <param name="logger">可选日志记录器，为 null 时使用 NullLogger。</param>
    public HostingClient(string serverAddress, ILogger<HostingClient> logger = null) {
        // 记录日志器，null 时退化为 NullLogger 保证无条件安全调用
        _logger = logger ?? Microsoft.Extensions.Logging.Abstractions.NullLogger<HostingClient>.Instance;

        // 创建 gRPC 通道：每个 GrpcChannel 对应一个 HTTP/2 连接池，应复用
        _channel = GrpcChannel.ForAddress(serverAddress);

        // 创建 stub：轻量级，不持有连接，可频繁 new 但无需这样做
        _stub = new HostingApi.HostingApiClient(_channel);

        // 记下目标地址，便于对照 appsettings 与实际连接
        _logger.LogInformation("gRPC 客户端已初始化，目标地址: {Address}", serverAddress);
    }

    // ============================================================================
    // Health — 健康检查
    // ============================================================================

    /// <summary>
    /// 调用 HostingApi.Health，检测服务是否在线。
    /// 网络异常时返回 <see cref="HealthResultDto.Offline"/> 而非抛出。
    /// </summary>
    public async Task<HealthResultDto> HealthAsync(
        CancellationToken ct = default) {
        try {
            // 发起 Health RPC，设置 5 秒超时防止界面卡死
            HealthResponse resp = await _stub.HealthAsync(new HealthRequest(),
                deadline: DateTime.UtcNow.AddSeconds(HealthDeadlineSeconds),
                cancellationToken: ct).ConfigureAwait(false);

            // 透传服务端字段；业务失败也走这条路径（Ok=false）
            return new HealthResultDto(resp.Ok, resp.HostVersion, resp.RouteCount);
        }
        catch (RpcException ex) {
            // gRPC 协议级错误（连接拒绝、超时等）
            _logger.LogWarning("Health 调用失败: {Status} {Detail}", ex.Status.StatusCode, ex.Status.Detail);
            return HealthResultDto.Offline();
        }
        catch (ObjectDisposedException) {
            // 应用退出时通道已释放，健康轮询可能仍在途：视为离线，不抛出
            return HealthResultDto.Offline();
        }
    }

    // ============================================================================
    // RegisterRoute — 注册路由
    // ============================================================================

    /// <summary>
    /// 注册一条路由。
    /// </summary>
    /// <returns>(success, errorCode, errorMessage, assignedRouteId)</returns>
    /// <param name="routeId">路由 ID，由调用方指定并在后续读写中原样使用。</param>
    /// <param name="protocolId">
    /// 协议标识，必须取自 <see cref="QueryProtocolsAsync"/> 返回的
    /// <see cref="ProtocolDescriptorDto.ProtocolId"/>；
    /// 传展示名会导致服务端匹配不到协议工厂。
    /// </param>
    /// <param name="transportKind">传输介质，"Tcp" 或 "Serial"。</param>
    /// <param name="address">TCP 路由的 IP 地址；串口路由传空字符串。</param>
    /// <param name="port">TCP 端口；串口路由传 0。</param>
    /// <param name="station">
    /// 站号 / 从站地址。设备级配置，因此变量地址可保持干净（DT100 而非 01:DT100）。
    /// 协议不需要站号时传空字符串。
    /// </param>
    /// <param name="serialPort">
    /// 串口名；TCP 路由传空字符串。
    /// 取值应来自 <see cref="QuerySerialPortsAsync"/>——那是宿主机器上的串口，
    /// 不是本机的（Windows 形如 "COM3"，树莓派形如 "/dev/ttyUSB0"）。
    /// </param>
    /// <param name="baudRate">波特率；TCP 路由传 0。</param>
    /// <param name="minIoIntervalMs">
    /// 同一路由两次 I/O 之间的最小间隔（毫秒）。
    /// 串口共享总线时需要它来满足从站的帧间静默要求。
    /// </param>
    /// <param name="ct">取消令牌。</param>
    public async Task<RegisterRouteResultDto>
        RegisterRouteAsync(
            string routeId,
            string protocolId,
            string transportKind,
            string address,
            int    port,
            string station,
            string serialPort      = "",
            int    baudRate        = 0,
            int    minIoIntervalMs = 100,
            CancellationToken ct   = default) {
        try {
            // 构造 Protobuf 请求消息。
            // Protobuf 的 string 字段不接受 null，统一归一为空字符串。
            RegisterRouteRequest req = new() {
                RouteId         = routeId       ?? string.Empty,
                ProtocolId      = protocolId    ?? string.Empty,
                TransportKind   = transportKind ?? string.Empty,
                Address         = address       ?? string.Empty,
                Port            = port,
                Station         = station       ?? string.Empty,
                SerialPort      = serialPort    ?? string.Empty,
                BaudRate        = baudRate,
                MinIoIntervalMs = minIoIntervalMs,
            };

            // 注册路由要在服务端建立真实 PLC 连接，是最容易长时间挂起的调用之一，
            // 必须设置截止时间，否则 UI 的"保存"按钮会无限期无响应
            RegisterRouteResponse resp = await _stub.RegisterRouteAsync(req,
                deadline: DateTime.UtcNow.AddSeconds(ConnectDeadlineSeconds),
                cancellationToken: ct).ConfigureAwait(false);

            if (!resp.Success)
                // 服务端返回业务失败，记录警告
                _logger.LogWarning("RegisterRoute 失败: {Code} {Msg}", resp.ErrorCode, resp.ErrorMessage);
            else
                _logger.LogInformation("路由已注册: {RouteId}", resp.RouteId);

            // 把服务端结果原样交给调用方，不抛异常
            return new RegisterRouteResultDto(resp.Success, resp.ErrorCode, resp.ErrorMessage, resp.RouteId);
        }
        catch (RpcException ex) {
            // 传输层失败（宿主未起、截止超时、HTTP/2 被拒）：归为 RPC_ERROR
            _logger.LogError(ex, "RegisterRoute RPC 异常: {RouteId}", routeId);
            return new RegisterRouteResultDto(false, "RPC_ERROR", ex.Status.Detail, routeId);
        }
    }

    // ============================================================================
    // QueryRoutes — 查询路由列表
    // ============================================================================

    /// <summary>查询路由，所有参数均可为空字符串表示不过滤。</summary>
    public async Task<IReadOnlyList<RouteDto>> QueryRoutesAsync(
        string routeId      = "",
        string protocolId   = "",
        string transportKind = "",
        string address      = "",
        CancellationToken ct = default) {
        try {
            // 带截止时间的查询：路由表快照，空过滤条件表示全量；不涉及 PLC I/O
            QueryRoutesResponse resp = await _stub.QueryRoutesAsync(
                new QueryRoutesRequest {
                    RouteId       = routeId,
                    ProtocolId    = protocolId,
                    TransportKind = transportKind,
                    Address       = address,
                },
                deadline: DateTime.UtcNow.AddSeconds(QueryDeadlineSeconds),
                cancellationToken: ct).ConfigureAwait(false);

            // 将 Protobuf RouteItem 投影为本地 DTO，避免 ViewModel 依赖 Protobuf 类型
            return resp.Routes
                .Select(r => new RouteDto(r.RouteId, r.ProtocolId, r.TransportKind,
                                          r.Address, r.Port, r.Station,
                                          r.SerialPort, r.BaudRate))
                .ToList();
        }
        catch (RpcException ex) {
            // 查询失败返回空列表：UI 可继续展示本地缓存，不因一次 RPC 失败清空界面
            _logger.LogError(ex, "QueryRoutes RPC 异常");
            return Array.Empty<RouteDto>();
        }
    }

    // ============================================================================
    // RemoveRoute — 删除路由
    // ============================================================================

    /// <summary>
    /// 向 Hosting.App 注销路由，停止对应的 PLC 连接。
    /// 结果以 <see cref="RemoveRouteResultDto"/> 返回；
    /// 服务端尚未实现时（Unimplemented）视为成功，由调用方在本地删除。
    /// </summary>
    public async Task<RemoveRouteResultDto>
        RemoveRouteAsync(string routeId, CancellationToken ct = default) {
        try {
            // 注销要等服务端释放 TCP/串口，使用连接类截止时间
            RemoveRouteResponse resp = await _stub.RemoveRouteAsync(
                new RemoveRouteRequest { RouteId = routeId },
                deadline: DateTime.UtcNow.AddSeconds(ConnectDeadlineSeconds),
                cancellationToken: ct).ConfigureAwait(false);

            if (!resp.Success)
                _logger.LogWarning("RemoveRoute 失败: {Code} {Msg}", resp.ErrorCode, resp.ErrorMessage);
            else
                _logger.LogInformation("路由已删除: {RouteId}", routeId);

            // 业务成败原样返回，由 UI 决定是否从本地列表移除
            return new RemoveRouteResultDto(resp.Success, resp.ErrorCode, resp.ErrorMessage);
        }
        catch (RpcException ex) when (ex.StatusCode == StatusCode.Unimplemented) {
            // 服务端不认识该接口 ≠ 删除成功。
            // 此前把它当成功，会让 UI 移除条目而服务端仍持有该路由与 PLC 连接，
            // 界面与实际状态从此不一致，且该 RouteId 再也无法重新注册。
            _logger.LogError("RemoveRoute 未被服务端实现: {RouteId}", routeId);
            return new RemoveRouteResultDto(false, "UNIMPLEMENTED",
                "服务端不支持删除路由，请升级 Hosting.App 后重试");
        }
        catch (RpcException ex) {
            // 传输层失败：返回 RPC_ERROR，由 UI 决定是否重试
            _logger.LogError(ex, "RemoveRoute RPC 异常: {RouteId}", routeId);
            return new RemoveRouteResultDto(false, "RPC_ERROR", ex.Status.Detail);
        }
    }

    // ============================================================================
    // QueryProtocols — 查询服务端协议插件列表
    // ============================================================================

    /// <summary>
    /// 查询 Hosting.App 已加载的协议插件描述符列表。
    /// 服务端未实现（Unimplemented）或不可达时返回空列表，
    /// 调用方应回退到本地兜底列表以保证离线状态下界面仍可操作。
    /// </summary>
    public async Task<IReadOnlyList<ProtocolDescriptorDto>> QueryProtocolsAsync(
        CancellationToken ct = default) {
        try {
            // 协议清单来自宿主插件工厂，与是否已注册路由无关；清单很小，5 秒截止即可
            QueryProtocolsResponse resp = await _stub.QueryProtocolsAsync(
                new QueryProtocolsRequest(),
                deadline: DateTime.UtcNow.AddSeconds(5),
                cancellationToken: ct).ConfigureAwait(false);

            // 投影为 UI 可用的 DTO，切断对 Protobuf 的依赖
            return resp.Protocols
                .Select(p => new ProtocolDescriptorDto(
                    p.ProtocolId, p.DisplayName, p.SupportedTransports.ToList(),
                    p.RequiresStation, p.StationHint))
                .ToList();
        }
        catch (RpcException ex) when (ex.StatusCode == StatusCode.Unimplemented) {
            // 服务端暂未实现该接口，返回空列表，调用方使用本地兜底
            _logger.LogDebug("QueryProtocols 服务端未实现，使用本地兜底列表");
            return Array.Empty<ProtocolDescriptorDto>();
        }
        catch (RpcException ex) {
            // 宿主不可达：返回空，UI 应使用本地兜底列表，避免下拉框空白且无提示
            _logger.LogWarning("QueryProtocols RPC 异常: {Status}", ex.Status.StatusCode);
            return Array.Empty<ProtocolDescriptorDto>();
        }
    }

    // ============================================================================
    // QuerySerialPorts — 查询宿主机器上的串口
    // ============================================================================

    /// <summary>
    /// 查询<b>宿主所在机器</b>上可用的串口。
    /// </summary>
    /// <remarks>
    /// 不是本机串口：宿主跑在树莓派时，操作员要选的是树莓派上的
    /// /dev/ttyUSB0，而不是自己 PC 上的 COM1。
    /// 返回空列表在多种情况下都属正常——现场是纯以太网、宿主未装串口插件、
    /// 或服务端版本较旧尚无此接口——UI 一律保留手工输入即可。
    /// </remarks>
    public async Task<IReadOnlyList<SerialPortDto>> QuerySerialPortsAsync(
        CancellationToken ct = default) {
        try {
            // 枚举发生在宿主侧；deadline 覆盖一次串口扫描即可
            QuerySerialPortsResponse resp = await _stub.QuerySerialPortsAsync(
                new QuerySerialPortsRequest(),
                deadline: DateTime.UtcNow.AddSeconds(QueryDeadlineSeconds),
                cancellationToken: ct).ConfigureAwait(false);

            // 投影为本地 DTO；空列表是正常情况，UI 应保留手工输入
            return resp.Ports
                .Select(p => new SerialPortDto(p.PortName, p.Description))
                .ToList();
        }
        catch (RpcException ex) when (ex.StatusCode == StatusCode.Unimplemented) {
            // 旧版宿主无此接口：串口改由操作员手工填写
            _logger.LogDebug("QuerySerialPorts 服务端未实现，串口需手工输入");
            return Array.Empty<SerialPortDto>();
        }
        catch (RpcException ex) {
            // 宿主不可达同样返回空，不阻断添加设备流程
            _logger.LogWarning("QuerySerialPorts RPC 异常: {Status}", ex.Status.StatusCode);
            return Array.Empty<SerialPortDto>();
        }
    }

    // ============================================================================
    // Read — 读取 PLC 数据
    // ============================================================================

    /// <summary>
    /// 向指定路由发起读取请求。
    /// </summary>
    /// <param name="routeId">路由标识。</param>
    /// <param name="dataAddress">协议地址字符串（例如 "DB10.DBW0"）。</param>
    /// <param name="length">
    /// 读取的<b>字节</b>数。协议插件自行换算到本协议的计数单位——
    /// Modbus 寄存器区按 (length+1)/2 个寄存器，位区按 length*8 位。
    /// </param>
    /// <param name="ct">取消令牌。</param>
    public async Task<ReadResultDto> ReadAsync(
        string routeId,
        string dataAddress,
        int    length,
        CancellationToken ct = default) {
        try {
            // 按路由读 PLC；deadline 防止假死挂起轮询循环
            ReadResponse resp = await _stub.ReadAsync(
                new ReadRequest { RouteId = routeId, DataAddress = dataAddress, Length = length },
                deadline: DateTime.UtcNow.AddSeconds(IoDeadlineSeconds),
                cancellationToken: ct).ConfigureAwait(false);

            // 无论成败都把结果装箱返回，调用方无需 catch
            return new ReadResultDto(resp.Success, resp.ErrorCode, resp.ErrorMessage,
                                     resp.Data.ToByteArray());
        }
        catch (RpcException ex) when (ex.StatusCode == StatusCode.DeadlineExceeded) {
            // 超时归类为可重试错误，交由轮询循环按退避策略处理
            _logger.LogWarning("Read 超时: route={RouteId} addr={Addr}", routeId, dataAddress);
            return new ReadResultDto(false, "TIMEOUT", "读取超时", Array.Empty<byte>());
        }
        catch (RpcException ex) {
            // 非超时的协议级错误（不可用、取消等），归为 RPC_ERROR
            _logger.LogError(ex, "Read RPC 异常: route={RouteId} addr={Addr}", routeId, dataAddress);
            return new ReadResultDto(false, "RPC_ERROR", ex.Status.Detail, Array.Empty<byte>());
        }
    }

    // ============================================================================
    // Write — 写入 PLC 数据
    // ============================================================================

    /// <summary>
    /// 向指定路由发起写入请求。
    /// </summary>
    public async Task<WriteResultDto> WriteAsync(
        string routeId,
        string dataAddress,
        byte[] data,
        CancellationToken ct = default) {
        try {
            // 按路由写 PLC；payload 从 byte[] 拷进 Protobuf ByteString
            WriteResponse resp = await _stub.WriteAsync(
                new WriteRequest {
                    RouteId     = routeId,
                    DataAddress = dataAddress,
                    Data        = Google.Protobuf.ByteString.CopyFrom(data),
                },
                deadline: DateTime.UtcNow.AddSeconds(IoDeadlineSeconds),
                cancellationToken: ct).ConfigureAwait(false);

            // 只回报成败；写入结果未知时由超时分支另行说明
            return new WriteResultDto(resp.Success, resp.ErrorCode, resp.ErrorMessage);
        }
        catch (RpcException ex) when (ex.StatusCode == StatusCode.DeadlineExceeded) {
            // 写入超时：结果未知，明确告知用户而非静默失败
            _logger.LogWarning("Write 超时: route={RouteId} addr={Addr}", routeId, dataAddress);
            return new WriteResultDto(false, "TIMEOUT", "写入超时，结果未知，请复核后重试");
        }
        catch (RpcException ex) {
            // 非超时传输失败，归为 RPC_ERROR
            _logger.LogError(ex, "Write RPC 异常: route={RouteId} addr={Addr}", routeId, dataAddress);
            return new WriteResultDto(false, "RPC_ERROR", ex.Status.Detail);
        }
    }

    // ============================================================================
    // WatchRouteStatus — 订阅路由状态流
    // ============================================================================

    /// <summary>
    /// 订阅路由状态事件流（服务端流式 RPC），断线后自动重连直至取消。
    /// 调用方通过 <paramref name="onStatus"/> 回调接收每个状态事件；
    /// 流意外中断时先回调一次离线状态，再按退避策略重连。
    /// 取消 <paramref name="ct"/> 可安全停止。
    /// </summary>
    /// <remarks>
    /// 必须自动重连：服务端重启或网络抖动会使流静默结束，
    /// 若就此返回，界面上所有设备会永远停留在最后已知状态（通常是"已连接"绿灯），
    /// 而实际早已断开——这是比显示离线危险得多的错误状态。
    /// </remarks>
    /// <param name="routeId">目标路由 ID；空字符串表示订阅全部路由。</param>
    /// <param name="onStatus">状态事件回调。</param>
    /// <param name="onDisconnected">
    /// 流中断时的回调，用于把设备标记为离线。可为 null。
    /// </param>
    /// <param name="ct">取消令牌。</param>
    public async Task WatchRouteStatusAsync(
        string routeId,
        Func<RouteStatusDto, Task> onStatus,
        Func<Task> onDisconnected = null,
        CancellationToken ct = default) {

        // 重连退避：首次 1 秒，逐次翻倍，上限 30 秒
        const int initialBackoffMs = 1_000;
        const int maxBackoffMs     = 30_000;
        int backoffMs = initialBackoffMs;

        // 直到调用方取消：流结束或异常都只结束本轮，不终结循环
        while (!ct.IsCancellationRequested) {
            // 本轮流是否已收到过事件；用于决定是否重置退避
            bool streamEstablished = false;

            try {
                // 打开服务端流式 RPC；空 routeId 表示订阅全部路由
                using AsyncServerStreamingCall<RouteStatusEvent> call =
                    _stub.WatchRouteStatus(new WatchRouteStatusRequest { RouteId = routeId },
                        cancellationToken: ct);

                // 逐条读取状态事件，直到流结束或取消
                await foreach (RouteStatusEvent evt in call.ResponseStream.ReadAllAsync(ct)
                                   .ConfigureAwait(false)) {
                    // 收到首个事件即视为连接成功，重置退避
                    if (!streamEstablished) {
                        streamEstablished = true;
                        backoffMs = initialBackoffMs;
                    }

                    // 将 Protobuf 时间戳（Unix ms）转换为本地 DateTime
                    DateTime ts = DateTimeOffset
                        .FromUnixTimeMilliseconds(evt.TimestampUnixMs)
                        .LocalDateTime;

                    // 投影为本地 DTO，切断 ViewModel 对 Protobuf 的依赖
                    RouteStatusDto dto = new(evt.RouteId, evt.Online,
                                             evt.ErrorCode, evt.ErrorMessage, ts);

                    // 交给调用方处理（通常是更新 ViewModel 属性）。
                    // 回调异常必须隔离：它若冒泡出去会被下方的 catch 当成流故障，
                    // 严重时（非 RpcException）直接终结整个重连循环，
                    // 此后该设备的状态永不更新，且界面停留在最后已知状态。
                    try {
                        await onStatus(dto).ConfigureAwait(false);
                    } catch (Exception ex) {
                        _logger.LogError(ex, "WatchRouteStatus 状态回调抛出异常，已隔离: {RouteId}", routeId);
                    }
                }

                // 流正常结束（服务端主动关闭），仍需重连
                _logger.LogDebug("WatchRouteStatus 流结束，准备重连: {RouteId}", routeId);
            }
            catch (OperationCanceledException) {
                // 调用方主动取消：正常退出，不重连
                _logger.LogDebug("WatchRouteStatus 流已取消: {RouteId}", routeId);
                return;
            }
            catch (RpcException ex) when (ex.StatusCode == StatusCode.Cancelled) {
                // gRPC 层的取消，也属于正常结束
                _logger.LogDebug("WatchRouteStatus gRPC 取消: {RouteId}", routeId);
                return;
            }
            catch (ObjectDisposedException) {
                // 通道已释放（应用退出中）：停止重连
                return;
            }
            catch (RpcException ex) {
                // 流中断（宿主重启、网络抖动）：记录后进入退避重连
                _logger.LogWarning("WatchRouteStatus 流中断（{Status}），{Delay}ms 后重连: {RouteId}",
                    ex.Status.StatusCode, backoffMs, routeId);
            }
            catch (Exception ex) {
                // 兜底：任何非预期异常也只结束本轮，不终结重连循环
                _logger.LogError(ex, "WatchRouteStatus 非预期异常，{Delay}ms 后重连: {RouteId}", backoffMs, routeId);
            }

            // 走到这里说明流已断开：先通知调用方置为离线，避免残留虚假的"已连接"
            if (onDisconnected != null) {
                try {
                    await onDisconnected().ConfigureAwait(false);
                } catch (Exception) {
                    // 回调异常不应中断重连循环
                }
            }

            // 退避等待后重连
            try {
                await Task.Delay(backoffMs, ct).ConfigureAwait(false);
            } catch (OperationCanceledException) {
                // 等待期间被取消：结束订阅
                return;
            }

            // 指数退避，封顶 30 秒，避免宿主长时间宕机时打出重连风暴
            backoffMs = Math.Min(backoffMs * 2, maxBackoffMs);
        }
    }

    // ============================================================================
    // IAsyncDisposable — 释放 gRPC 通道
    // ============================================================================

    /// <summary>
    /// 关闭 gRPC 通道并释放连接池。
    /// </summary>
    /// <remarks>
    /// 应用退出时调用一次。ShutdownAsync 会等待在途请求完成，
    /// 直接 Dispose 会让正在进行的读写以连接中断告终。
    /// </remarks>
    public async ValueTask DisposeAsync() {
        // 关闭 HTTP/2 连接池，等待现有请求完成
        await _channel.ShutdownAsync().ConfigureAwait(false);

        // 释放通道底层资源
        _channel.Dispose();

        // 关闭完成，后续任何 RPC 都会 ObjectDisposedException，健康轮询会当成离线
        _logger.LogInformation("gRPC 通道已关闭");
    }
}
