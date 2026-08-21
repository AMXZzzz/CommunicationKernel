// -----------------------------------------------------------------------------
// 文件: Services/EngineHostGrpcClient.cs
// 层级: UI 层 — WPF 客户端服务
// 作用: 封装所有 gRPC 调用，为 ViewModel 提供强类型异步方法。
//       所有网络 I/O 均在此类完成，ViewModel 无需感知 Protobuf 细节。
// 调用链:
//   ViewModel → EngineHostGrpcClient → gRPC Channel → EngineHost gRPC Server
// -----------------------------------------------------------------------------

using CommunicationKernel.EngineHost.Grpc.V1;   // 由 Grpc.Tools 从 .proto 生成
using Grpc.Core;                                  // RpcException, Metadata
using Grpc.Net.Client;                            // GrpcChannel
using Microsoft.Extensions.Logging;              // ILogger<T>

namespace CommunicationKernel.UI.Wpf.Services;

// =============================================================================
// 数据传输对象 — 用于隔离 ViewModel 与 Protobuf 消息类型
// =============================================================================

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
/// <see cref="TransportKind"/> 决定展示「IP+端口」还是「串口+波特率」；
/// <see cref="RequiresStation"/> 决定是否展示站号输入框。
/// </summary>
public sealed record ProtocolDescriptorDto(
    string ProtocolId,
    string DisplayName,
    string TransportKind,
    bool   RequiresStation,
    string StationHint);

/// <summary>读取结果 DTO。</summary>
public sealed record ReadResultDto(bool Success, string ErrorCode, string ErrorMessage, byte[] Data);

/// <summary>写入结果 DTO。</summary>
public sealed record WriteResultDto(bool Success, string ErrorCode, string ErrorMessage);

// =============================================================================
// gRPC 客户端封装
// =============================================================================

/// <summary>
/// 封装所有 EngineHostApi gRPC 调用。
/// 单例生命周期：应用启动时由 DI 容器创建，应用退出时 Dispose。
/// </summary>
public sealed class EngineHostGrpcClient : IAsyncDisposable {

    // -------------------------------------------------------------------------
    // 常量
    // -------------------------------------------------------------------------

    /// <summary>
    /// 单次读写 RPC 的截止时间（秒）。
    /// 没有截止时间时，PLC 假死会让调用方（尤其是变量轮询循环）无限期挂起，
    /// 使退避重试机制形同虚设。
    /// </summary>
    private const int IoDeadlineSeconds = 10;

    /// <summary>健康检查 RPC 的截止时间（秒）。</summary>
    private const int HealthDeadlineSeconds = 5;

    // -------------------------------------------------------------------------
    // 私有字段
    // -------------------------------------------------------------------------

    /// <summary>gRPC 通道，持有底层 HTTP/2 连接池。</summary>
    private readonly GrpcChannel _channel;

    /// <summary>由 Grpc.Tools 生成的强类型客户端 stub。</summary>
    private readonly EngineHostApi.EngineHostApiClient _stub;

    /// <summary>日志记录器（可为 NullLogger，保持可测试性）。</summary>
    private readonly ILogger<EngineHostGrpcClient> _logger;

    // -------------------------------------------------------------------------
    // 构造函数
    // -------------------------------------------------------------------------

    /// <param name="serverAddress">gRPC 服务地址，例如 "http://localhost:5000"。</param>
    /// <param name="logger">可选日志记录器，为 null 时使用 NullLogger。</param>
    public EngineHostGrpcClient(string serverAddress, ILogger<EngineHostGrpcClient> logger = null) {
        // 记录日志器，null 时退化为 NullLogger 保证无条件安全调用
        _logger = logger ?? Microsoft.Extensions.Logging.Abstractions.NullLogger<EngineHostGrpcClient>.Instance;

        // 创建 gRPC 通道：每个 GrpcChannel 对应一个 HTTP/2 连接池，应复用
        _channel = GrpcChannel.ForAddress(serverAddress);

        // 创建 stub：轻量级，不持有连接，可频繁 new 但无需这样做
        _stub = new EngineHostApi.EngineHostApiClient(_channel);

        _logger.LogInformation("gRPC 客户端已初始化，目标地址: {Address}", serverAddress);
    }

    // -------------------------------------------------------------------------
    // Health — 健康检查
    // -------------------------------------------------------------------------

    /// <summary>
    /// 调用 EngineHostApi.Health，检测服务是否在线。
    /// 返回 (ok, hostVersion, routeCount)；网络异常时返回 (false, "", 0)。
    /// </summary>
    public async Task<(bool Ok, string HostVersion, int RouteCount)> HealthAsync(
        CancellationToken ct = default) {
        try {
            // 发起 Health RPC，设置 5 秒超时防止界面卡死
            HealthResponse resp = await _stub.HealthAsync(new HealthRequest(),
                deadline: DateTime.UtcNow.AddSeconds(HealthDeadlineSeconds),
                cancellationToken: ct).ConfigureAwait(false);

            return (resp.Ok, resp.HostVersion, resp.RouteCount);
        }
        catch (RpcException ex) {
            // gRPC 协议级错误（连接拒绝、超时等）
            _logger.LogWarning("Health 调用失败: {Status} {Detail}", ex.Status.StatusCode, ex.Status.Detail);
            return (false, string.Empty, 0);
        }
        catch (ObjectDisposedException) {
            // 应用退出时通道已释放，健康轮询可能仍在途：视为离线，不抛出
            return (false, string.Empty, 0);
        }
    }

    // -------------------------------------------------------------------------
    // RegisterRoute — 注册路由
    // -------------------------------------------------------------------------

    /// <summary>
    /// 注册一条路由。
    /// </summary>
    /// <returns>(success, errorCode, errorMessage, assignedRouteId)</returns>
    /// <param name="serialPort">串口名（如 "COM3"）；TCP 路由传空字符串。</param>
    /// <param name="baudRate">波特率；TCP 路由传 0。</param>
    public async Task<(bool Success, string ErrorCode, string ErrorMessage, string RouteId)>
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

            RegisterRouteResponse resp = await _stub.RegisterRouteAsync(req,
                cancellationToken: ct).ConfigureAwait(false);

            if (!resp.Success)
                // 服务端返回业务失败，记录警告
                _logger.LogWarning("RegisterRoute 失败: {Code} {Msg}", resp.ErrorCode, resp.ErrorMessage);
            else
                _logger.LogInformation("路由已注册: {RouteId}", resp.RouteId);

            return (resp.Success, resp.ErrorCode, resp.ErrorMessage, resp.RouteId);
        }
        catch (RpcException ex) {
            _logger.LogError(ex, "RegisterRoute RPC 异常: {RouteId}", routeId);
            return (false, "RPC_ERROR", ex.Status.Detail, routeId);
        }
    }

    // -------------------------------------------------------------------------
    // QueryRoutes — 查询路由列表
    // -------------------------------------------------------------------------

    /// <summary>查询路由，所有参数均可为空字符串表示不过滤。</summary>
    public async Task<IReadOnlyList<RouteDto>> QueryRoutesAsync(
        string routeId      = "",
        string protocolId   = "",
        string transportKind = "",
        string address      = "",
        CancellationToken ct = default) {
        try {
            QueryRoutesResponse resp = await _stub.QueryRoutesAsync(
                new QueryRoutesRequest {
                    RouteId       = routeId,
                    ProtocolId    = protocolId,
                    TransportKind = transportKind,
                    Address       = address,
                },
                cancellationToken: ct).ConfigureAwait(false);

            // 将 Protobuf RouteItem 投影为本地 DTO，避免 ViewModel 依赖 Protobuf 类型
            return resp.Routes
                .Select(r => new RouteDto(r.RouteId, r.ProtocolId, r.TransportKind,
                                          r.Address, r.Port, r.Station,
                                          r.SerialPort, r.BaudRate))
                .ToList();
        }
        catch (RpcException ex) {
            _logger.LogError(ex, "QueryRoutes RPC 异常");
            return Array.Empty<RouteDto>();
        }
    }

    // -------------------------------------------------------------------------
    // RemoveRoute — 删除路由
    // -------------------------------------------------------------------------

    /// <summary>
    /// 向 EngineHost 注销路由，停止对应的 PLC 连接。
    /// 返回 (success, errorCode, errorMessage)；
    /// 服务端尚未实现时（Unimplemented）视为成功，由调用方在本地删除。
    /// </summary>
    public async Task<(bool Success, string ErrorCode, string ErrorMessage)>
        RemoveRouteAsync(string routeId, CancellationToken ct = default) {
        try {
            RemoveRouteResponse resp = await _stub.RemoveRouteAsync(
                new RemoveRouteRequest { RouteId = routeId },
                cancellationToken: ct).ConfigureAwait(false);

            if (!resp.Success)
                _logger.LogWarning("RemoveRoute 失败: {Code} {Msg}", resp.ErrorCode, resp.ErrorMessage);
            else
                _logger.LogInformation("路由已删除: {RouteId}", routeId);

            return (resp.Success, resp.ErrorCode, resp.ErrorMessage);
        }
        catch (RpcException ex) when (ex.StatusCode == StatusCode.Unimplemented) {
            // 服务端暂未实现该接口：视为成功，调用方继续本地删除
            _logger.LogDebug("RemoveRoute 服务端未实现，仅本地删除: {RouteId}", routeId);
            return (true, string.Empty, string.Empty);
        }
        catch (RpcException ex) {
            _logger.LogError(ex, "RemoveRoute RPC 异常: {RouteId}", routeId);
            return (false, "RPC_ERROR", ex.Status.Detail);
        }
    }

    // -------------------------------------------------------------------------
    // QueryProtocols — 查询服务端协议插件列表
    // -------------------------------------------------------------------------

    /// <summary>
    /// 查询 EngineHost 已加载的协议插件描述符列表。
    /// 服务端未实现（Unimplemented）或不可达时返回空列表，
    /// 调用方应回退到本地兜底列表以保证离线状态下界面仍可操作。
    /// </summary>
    public async Task<IReadOnlyList<ProtocolDescriptorDto>> QueryProtocolsAsync(
        CancellationToken ct = default) {
        try {
            QueryProtocolsResponse resp = await _stub.QueryProtocolsAsync(
                new QueryProtocolsRequest(),
                deadline: DateTime.UtcNow.AddSeconds(5),
                cancellationToken: ct).ConfigureAwait(false);

            return resp.Protocols
                .Select(p => new ProtocolDescriptorDto(
                    p.ProtocolId, p.DisplayName, p.TransportKind,
                    p.RequiresStation, p.StationHint))
                .ToList();
        }
        catch (RpcException ex) when (ex.StatusCode == StatusCode.Unimplemented) {
            // 服务端暂未实现该接口，返回空列表，调用方使用本地兜底
            _logger.LogDebug("QueryProtocols 服务端未实现，使用本地兜底列表");
            return Array.Empty<ProtocolDescriptorDto>();
        }
        catch (RpcException ex) {
            _logger.LogWarning("QueryProtocols RPC 异常: {Status}", ex.Status.StatusCode);
            return Array.Empty<ProtocolDescriptorDto>();
        }
    }

    // -------------------------------------------------------------------------
    // Read — 读取 PLC 数据
    // -------------------------------------------------------------------------

    /// <summary>
    /// 向指定路由发起读取请求。
    /// </summary>
    /// <param name="routeId">路由标识。</param>
    /// <param name="dataAddress">协议地址字符串（例如 "DB10.DBW0"）。</param>
    /// <param name="length">读取字节数。</param>
    public async Task<ReadResultDto> ReadAsync(
        string routeId,
        string dataAddress,
        int    length,
        CancellationToken ct = default) {
        try {
            ReadResponse resp = await _stub.ReadAsync(
                new ReadRequest { RouteId = routeId, DataAddress = dataAddress, Length = length },
                deadline: DateTime.UtcNow.AddSeconds(IoDeadlineSeconds),
                cancellationToken: ct).ConfigureAwait(false);

            return new ReadResultDto(resp.Success, resp.ErrorCode, resp.ErrorMessage,
                                     resp.Data.ToByteArray());
        }
        catch (RpcException ex) when (ex.StatusCode == StatusCode.DeadlineExceeded) {
            // 超时归类为可重试错误，交由轮询循环按退避策略处理
            _logger.LogWarning("Read 超时: route={RouteId} addr={Addr}", routeId, dataAddress);
            return new ReadResultDto(false, "TIMEOUT", "读取超时", Array.Empty<byte>());
        }
        catch (RpcException ex) {
            _logger.LogError(ex, "Read RPC 异常: route={RouteId} addr={Addr}", routeId, dataAddress);
            return new ReadResultDto(false, "RPC_ERROR", ex.Status.Detail, Array.Empty<byte>());
        }
    }

    // -------------------------------------------------------------------------
    // Write — 写入 PLC 数据
    // -------------------------------------------------------------------------

    /// <summary>
    /// 向指定路由发起写入请求。
    /// </summary>
    public async Task<WriteResultDto> WriteAsync(
        string routeId,
        string dataAddress,
        byte[] data,
        CancellationToken ct = default) {
        try {
            WriteResponse resp = await _stub.WriteAsync(
                new WriteRequest {
                    RouteId     = routeId,
                    DataAddress = dataAddress,
                    Data        = Google.Protobuf.ByteString.CopyFrom(data),
                },
                deadline: DateTime.UtcNow.AddSeconds(IoDeadlineSeconds),
                cancellationToken: ct).ConfigureAwait(false);

            return new WriteResultDto(resp.Success, resp.ErrorCode, resp.ErrorMessage);
        }
        catch (RpcException ex) when (ex.StatusCode == StatusCode.DeadlineExceeded) {
            // 写入超时：结果未知，明确告知用户而非静默失败
            _logger.LogWarning("Write 超时: route={RouteId} addr={Addr}", routeId, dataAddress);
            return new WriteResultDto(false, "TIMEOUT", "写入超时，结果未知，请复核后重试");
        }
        catch (RpcException ex) {
            _logger.LogError(ex, "Write RPC 异常: route={RouteId} addr={Addr}", routeId, dataAddress);
            return new WriteResultDto(false, "RPC_ERROR", ex.Status.Detail);
        }
    }

    // -------------------------------------------------------------------------
    // WatchRouteStatus — 订阅路由状态流
    // -------------------------------------------------------------------------

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

        while (!ct.IsCancellationRequested) {
            bool streamEstablished = false;

            try {
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

                    RouteStatusDto dto = new(evt.RouteId, evt.Online,
                                             evt.ErrorCode, evt.ErrorMessage, ts);

                    // 交给调用方处理（通常是更新 ViewModel 属性）
                    await onStatus(dto).ConfigureAwait(false);
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
                _logger.LogWarning("WatchRouteStatus 流中断（{Status}），{Delay}ms 后重连: {RouteId}",
                    ex.Status.StatusCode, backoffMs, routeId);
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
                return;
            }

            backoffMs = Math.Min(backoffMs * 2, maxBackoffMs);
        }
    }

    // -------------------------------------------------------------------------
    // IAsyncDisposable — 释放 gRPC 通道
    // -------------------------------------------------------------------------

    public async ValueTask DisposeAsync() {
        // 关闭 HTTP/2 连接池，等待现有请求完成
        await _channel.ShutdownAsync().ConfigureAwait(false);
        _channel.Dispose();
        _logger.LogInformation("gRPC 通道已关闭");
    }
}
