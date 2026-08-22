using System;
using System.Linq;
using System.Threading.Channels;
using System.Threading.Tasks;
using CommunicationKernel.Communication.Protocol.Abstractions;
using CommunicationKernel.Communication.Transport.Abstractions;
using CommunicationKernel.Core.Abstractions.Errors;
using CommunicationKernel.Core.Abstractions.Results;
using CommunicationKernel.EngineHost.Grpc.V1;
using CommunicationKernel.Engine.Runtime;
using CommunicationKernel.Engine.Runtime.Models;
using Google.Protobuf;
using Grpc.Core;
using Microsoft.Extensions.Logging;

// -----------------------------------------------------------------------------
// 文件: HostGrpcService.cs
// 层级: Host.App / Services / Grpc
// 作用: Host.App gRPC 对外服务实现（协议无关请求模型）。
// 说明:
// 1) RegisterRoute 负责路由注册入口；底层组装由 EngineRuntime 完成。
// 2) Read/Write 仅使用 route_id，避免 UI 端依赖协议细节字段。
// 3) WatchRouteStatus 使用有界 Channel（容量 256，溢出丢旧）防止慢客户端
//    导致内存无限增长；状态采用最终一致模型，丢失旧快照不影响正确性。
// -----------------------------------------------------------------------------

namespace CommunicationKernel.Host.App.Services;

/// <summary>
/// -----------------------------------------------------------------------------
/// 文件: HostGrpcService.cs
/// 层级: Host.App / Services / Grpc
/// 作用: Host.App gRPC 对外服务实现（协议无关请求模型）。
/// 说明:
/// 1) RegisterRoute 负责路由注册入口；底层组装由 EngineRuntime 完成。
/// 2) Read/Write 仅使用 route_id，避免 UI 端依赖协议细节字段。
/// 3) WatchRouteStatus 使用有界 Channel（容量 256，溢出丢旧）防止慢客户端
///    导致内存无限增长；状态采用最终一致模型，丢失旧快照不影响正确性。
/// -----------------------------------------------------------------------------
/// </summary>
public sealed class HostGrpcService : EngineHostApi.EngineHostApiBase {
    // 服务版本常量集中定义，避免多处硬编码。
    private const string HostVersion = "1.0.0-grpc-v3";

    // 状态推流 Channel 容量：超出时丢弃最旧快照（最终一致，不影响准确性）。
    private const int StatusChannelCapacity = 256;

    // 通讯内核：路由注册、读写、状态订阅均经此入口
    private readonly EngineRuntime _hostRuntime;

    // 插件装配：提供协议清单与宿主机器串口枚举，与是否已注册路由无关
    private readonly IRouteAssemblyService _routeAssemblyService;

    // 结构化日志：慢客户端丢事件、注册失败等诊断都走这里
    private readonly ILogger<HostGrpcService> _logger;

    // 注入内核、插件装配与日志；三者均为单例，与宿主同寿
    public HostGrpcService (
        // 路由注册 / 读写 / 状态订阅的唯一入口
        EngineRuntime hostRuntime,
        // 协议插件清单与宿主机器串口枚举
        IRouteAssemblyService routeAssemblyService,
        // 推流丢事件、注册失败等诊断日志
        ILogger<HostGrpcService> logger) {

        // 缺一不可：任一为空则 gRPC 端点无法工作
        ArgumentNullException.ThrowIfNull(hostRuntime);
        ArgumentNullException.ThrowIfNull(routeAssemblyService);
        ArgumentNullException.ThrowIfNull(logger);

        // 保存依赖，供后续 RPC 方法使用
        _hostRuntime            = hostRuntime;
        _routeAssemblyService   = routeAssemblyService;
        _logger                 = logger;
    }

    // ============================================================================
    // Health / Diagnostics
    // ============================================================================

    /// <summary>
    /// 健康检查：返回服务端版本、路由数量等基本信息，供客户端（UI 层）监控。
    /// </summary>
    /// <param name="request"></param>
    /// <param name="context"></param>
    /// <returns></returns>
    public override Task<HealthResponse> Health(HealthRequest request, ServerCallContext context) {
        // 本 RPC 不使用请求体与调用上下文，显式丢弃以免未使用告警
        _ = request;
        _ = context;

        // 立即返回：版本常量 + 当前已注册路由数，供 UI 心跳与连接指示
        return Task.FromResult(new HealthResponse {
            Ok           = true,
            HostVersion  = HostVersion,
            RouteCount   = _hostRuntime.RouteCount
        });
    }

    /// <summary>
    /// 诊断信息：返回服务端版本、路由数量、订阅数量等详细信息，供客户端（UI 层）调试。
    /// </summary>
    /// <param name="request"></param>
    /// <param name="context"></param>
    /// <returns></returns>
    public override Task<DiagnosticsResponse> GetDiagnostics(DiagnosticsRequest request, ServerCallContext context) {
        // 诊断同样不依赖请求字段；RequestId 每次新生成便于日志对账
        _ = context;
        _ = request;

        // 快照当前路由数与挂起数，不触及 PLC I/O
        return Task.FromResult(new DiagnosticsResponse {
            RequestId         = Guid.NewGuid().ToString("N"),
            RouteCount        = _hostRuntime.RouteCount,
            PendingRouteCount = _hostRuntime.PendingRouteCount,
            HostVersion       = HostVersion
        });
    }

    // ============================================================================
    // 路由注册 / 查询 / 注销
    // ============================================================================

    /// <summary>
    /// 注册路由：用于一次性注册路由并完成插件组装。
    /// </summary>
    /// <param name="request"></param>
    /// <param name="context"></param>
    /// <returns></returns>
    public override async Task<RegisterRouteResponse> RegisterRoute(RegisterRouteRequest request, ServerCallContext context) {
        // 把 Protobuf 请求投影为内核命令；协议细节由插件工厂消化，这里只做字段搬运
        var command = new RegisterRouteCommand {
            RouteId       = request.RouteId,
            ProtocolId    = request.ProtocolId,
            TransportId   = request.TransportId,
            TransportKind = request.TransportKind,
            Address       = request.Address,
            Port          = request.Port,
            Station       = request.Station,
            SerialPort    = request.SerialPort,
            BaudRate      = request.BaudRate,
            MinIoIntervalMs = request.MinIoIntervalMs,
            Parity        = request.Parity,
            DataBits      = request.DataBits,
            StopBits      = request.StopBits
        };

        // 交给 EngineRuntime 组装传输+协议并挂入路由表；取消令牌来自 gRPC 调用上下文
        OperationResult<string> register = await _hostRuntime
            .RegisterRouteAsync(command, context.CancellationToken)
            .ConfigureAwait(false);

        // 业务失败也以响应字段返回，不抛 RpcException，便于 UI 展示 ErrorCode
        return new RegisterRouteResponse {
            Success      = register.Success,
            ErrorCode    = register.ErrorCode.ToString(),
            ErrorMessage = register.Success ? string.Empty : register.ErrorMessage,
            RouteId      = register.Success ? register.Value ?? string.Empty : string.Empty
        };
    }
    
    /// <summary>
    /// 查询路由：根据请求参数过滤路由信息，供客户端（UI 层）查看。
    /// </summary>
    /// <param name="request"></param>
    /// <param name="context"></param>
    /// <returns></returns>
    public override Task<QueryRoutesResponse> QueryRoutes(QueryRoutesRequest request, ServerCallContext context) {
        // 查询是内存快照，不需要取消令牌
        _ = context;

        // 取出当前全部路由运行时信息
        var routes = _hostRuntime.SnapshotRoutes();

        // 空字符串表示该维不过滤；比较忽略大小写，兼容 UI 传入的枚举 ToString
        var filtered = routes.Where(r =>
            (string.IsNullOrWhiteSpace(request.RouteId)      || string.Equals(r.RouteId,                    request.RouteId,      StringComparison.OrdinalIgnoreCase))
            && (string.IsNullOrWhiteSpace(request.ProtocolId) || string.Equals(r.RouteKey.ProtocolId,        request.ProtocolId,   StringComparison.OrdinalIgnoreCase))
            && (string.IsNullOrWhiteSpace(request.TransportKind) || string.Equals(r.RouteKey.TransportKind.ToString(), request.TransportKind, StringComparison.OrdinalIgnoreCase))
            && (string.IsNullOrWhiteSpace(request.Address)    || string.Equals(r.RouteKey.Address,           request.Address,      StringComparison.OrdinalIgnoreCase)));

        // 投影为线上契约，切断 UI 对内核模型的依赖
        var response = new QueryRoutesResponse();
        foreach (RouteRuntimeInfo r in filtered) {
            response.Routes.Add(new RouteItem {
                RouteId       = r.RouteId,
                ProtocolId    = r.RouteKey.ProtocolId,
                TransportId   = r.TransportId,
                TransportKind = r.RouteKey.TransportKind.ToString(),
                Address       = r.RouteKey.Address,
                Port          = r.RouteKey.Port,
                Station       = r.RouteKey.Station ?? string.Empty,
                SerialPort    = r.Endpoint.SerialPort ?? string.Empty,
                BaudRate      = r.Endpoint.BaudRate ?? 0
            });
        }

        // 过滤后的快照交给客户端，不抛「未找到」——空列表本身就是结果
        return Task.FromResult(response);
    }

    // ============================================================================
    // 读写
    // ============================================================================
    
    /// <summary>
    /// 读取数据：根据路由 ID 和数据地址读取数据，供客户端（UI 层）使用。
    /// </summary>
    /// <param name="request"></param>
    /// <param name="context"></param>
    /// <returns></returns> 
    public override async Task<ReadResponse> Read(ReadRequest request, ServerCallContext context) {
        // 没有 route_id 无法定位传输连接，直接参数错误，避免打到内核
        if (string.IsNullOrWhiteSpace(request.RouteId)) {
            return new ReadResponse {
                Success      = false,
                ErrorCode    = KernelErrorCode.InvalidArgument.ToString(),
                ErrorMessage = "route_id is required",
                Data         = ByteString.Empty
            };
        }

        // 按路由读：内核负责同路由串行、相同地址合并；length 单位为字节
        OperationResult<byte[]> result = await _hostRuntime
            .ReadByRouteIdAsync(request.RouteId, request.DataAddress, request.Length, context.CancellationToken)
            .ConfigureAwait(false);

        // 成功才带数据；失败返回空字节，错误码供 UI 决定是否重试
        return new ReadResponse {
            Success      = result.Success,
            ErrorCode    = result.ErrorCode.ToString(),
            ErrorMessage = result.Success ? string.Empty : result.ErrorMessage,
            Data         = result.Success ? ByteString.CopyFrom(result.Value ?? Array.Empty<byte>()) : ByteString.Empty
        };
    }

    /// <summary>
    /// 写入数据：根据路由 ID、数据地址和数据内容写入数据，供客户端（UI 层）使用。
    /// </summary>
    /// <param name="request"></param>
    /// <param name="context"></param>
    /// <returns></returns>
    public override async Task<WriteResponse> Write(WriteRequest request, ServerCallContext context) {
        // 与 Read 相同：缺 route_id 直接拒绝
        if (string.IsNullOrWhiteSpace(request.RouteId)) {
            return new WriteResponse {
                Success      = false,
                ErrorCode    = KernelErrorCode.InvalidArgument.ToString(),
                ErrorMessage = "route_id is required"
            };
        }

        // Protobuf bytes 可能为 null，归一为空数组再交给内核
        byte[] payload = request.Data?.ToByteArray() ?? Array.Empty<byte>();

        // 按路由写：同路由与读共用串行门，避免总线交错
        OperationResult result = await _hostRuntime
            .WriteByRouteIdAsync(request.RouteId, request.DataAddress, payload, context.CancellationToken)
            .ConfigureAwait(false);

        // 只回报成败与错误码，不回传写入内容
        return new WriteResponse {
            Success      = result.Success,
            ErrorCode    = result.ErrorCode.ToString(),
            ErrorMessage = result.Success ? string.Empty : result.ErrorMessage
        };
    }

    // ============================================================================
    // 状态推流
    // ============================================================================

    /// <summary>
    /// 订阅路由状态：客户端可通过此接口实时接收指定路由的状态变化事件，供 UI 层显示。
    /// </summary>
    /// <param name="request"></param>
    /// <param name="responseStream"></param>
    /// <param name="context"></param>
    /// <returns></returns>
    public override async Task WatchRouteStatus(
        WatchRouteStatusRequest request,
        IServerStreamWriter<RouteStatusEvent> responseStream,
        ServerCallContext context) {

        // 有界 Channel（容量 256，溢出丢最旧）：防止慢客户端导致内存无限增长。
        var channel = Channel.CreateBounded<RouteStatusSnapshot>(
            new BoundedChannelOptions(StatusChannelCapacity) {
                FullMode = BoundedChannelFullMode.DropOldest
            });

        void OnStatus(RouteStatusSnapshot snapshot) {
            // 指定了 route_id 则只转发该路由，空字符串表示订阅全部
            if (!string.IsNullOrWhiteSpace(request.RouteId)
                && !string.Equals(request.RouteId, snapshot.RouteId, StringComparison.OrdinalIgnoreCase))
                return;

            // 写不进去说明客户端太慢，丢旧快照并记警告；状态是最终一致，丢旧无害
            if (!channel.Writer.TryWrite(snapshot))
                _logger.LogWarning("WatchRouteStatus: status channel full, dropped event for route '{RouteId}'.", snapshot.RouteId);
        }

        // 必须「先订阅、再发快照」。
        // 反过来会留下一个窗口：快照已取、订阅尚未挂上，该窗口内发生的状态变化
        // 既不在快照里、也不会被推送，客户端将永久停留在过期状态。
        // 先订阅可能导致快照与首批事件重复，但状态是最终一致模型，重复无害。
        _hostRuntime.RouteStatusChanged += OnStatus;
        try {
            // 先把当前快照推给客户端，避免订阅后长时间看不到初始状态
            foreach (RouteStatusSnapshot snapshot in _hostRuntime.SnapshotStatuses(request.RouteId)) {
                await responseStream.WriteAsync(ToStatusEvent(snapshot)).ConfigureAwait(false);
            }

            // 随后阻塞读 Channel，直到客户端取消
            while (!context.CancellationToken.IsCancellationRequested) {
                // 等待下一帧状态；取消令牌来自 gRPC 流，客户端断开即结束
                RouteStatusSnapshot snapshot =
                    await channel.Reader.ReadAsync(context.CancellationToken).ConfigureAwait(false);

                // 转为线上事件写出；慢客户端由 Channel DropOldest 保护
                await responseStream.WriteAsync(ToStatusEvent(snapshot)).ConfigureAwait(false);
            }
        } catch (OperationCanceledException) {
            // 客户端取消订阅：正常退出。
        } finally {
            // 摘掉事件并关闭 Channel，避免泄漏与对已完成流继续写入
            _hostRuntime.RouteStatusChanged -= OnStatus;
            channel.Writer.TryComplete();
        }
    }

    /// <summary>
    /// 注销路由：调用 EngineRuntime.UnregisterRouteAsync 释放传输连接和协议驱动，
    /// 然后向客户端回报操作结果。
    /// </summary>
    public override async Task<RemoveRouteResponse> RemoveRoute(
        RemoveRouteRequest request, ServerCallContext context) {

        // 释放 TCP/串口与协议驱动，并从路由表摘除
        OperationResult result = await _hostRuntime
            .UnregisterRouteAsync(request.RouteId, context.CancellationToken)
            .ConfigureAwait(false);

        // 同样以字段表达成败，不抛异常
        return new RemoveRouteResponse {
            Success      = result.Success,
            ErrorCode    = result.ErrorCode.ToString(),
            ErrorMessage = result.Success ? string.Empty : result.ErrorMessage
        };
    }

    // ============================================================================
    // 协议 / 串口发现
    // ============================================================================

    /// <summary>
    /// 列出服务端已加载的全部协议插件描述符。
    /// 客户端（UI 层）据此渲染协议下拉框与设备表单，无需内置任何协议知识。
    /// </summary>
    /// <remarks>
    /// 数据源是插件工厂清单，与当前是否已注册路由无关——
    /// 空载 Host 同样返回完整可用协议列表，这正是 UI 首次添加设备时的场景。
    /// </remarks>
    public override Task<QueryProtocolsResponse> QueryProtocols(
        QueryProtocolsRequest request, ServerCallContext context) {

        // 本查询不依赖请求字段与调用上下文
        _ = request;
        _ = context;

        var response = new QueryProtocolsResponse();

        // 遍历已加载的协议工厂元数据，投影为 Protobuf 描述符
        foreach (ProtocolMetadata meta in _routeAssemblyService.GetAvailableProtocols()) {
            // 无展示名时回落到 ID，保证下拉框不出现空项
            var descriptor = new ProtocolDescriptor {
                ProtocolId      = meta.ProtocolId,
                DisplayName     = string.IsNullOrWhiteSpace(meta.DisplayName)
                    ? meta.ProtocolId          // 无展示名时回落到 ID，保证下拉框不出现空项
                    : meta.DisplayName,
                RequiresStation = meta.RequiresStation,
                StationHint     = meta.StationHint ?? string.Empty
            };

            // 支持的介质列表：为空时回落到 Tcp，保证 UI 至少有一个可选项
            if (meta.SupportedTransports is { Count: > 0 }) {
                foreach (TransportKind kind in meta.SupportedTransports)
                    descriptor.SupportedTransports.Add(kind.ToString());
            } else {
                descriptor.SupportedTransports.Add(TransportKind.Tcp.ToString());
            }

            // 加入响应清单，供 UI 渲染协议下拉与表单
            response.Protocols.Add(descriptor);
        }

        // 空载宿主同样返回完整协议列表，这正是 UI 首次添加设备的场景
        return Task.FromResult(response);
    }

    /// <summary>
    /// 列出<b>本机</b>（服务端所在机器）当前可用的串口。
    /// </summary>
    /// <remarks>
    /// 串口长在跑通讯的这台机器上。宿主在树莓派、上位机在办公室 PC 时，
    /// 上位机列出的 COM1/COM2 是它自己的，与 PLC 毫无关系——
    /// 选中后注册必然失败，而错误信息指向"打不开 COM1"，完全误导。
    /// 因此枚举必须发生在这里。
    ///
    /// 服务端不持有任何串口知识：具体怎么枚举由串口传输插件实现，
    /// 引擎只是在传输工厂里找有没有人实现了枚举接口。
    /// </remarks>
    public override Task<QuerySerialPortsResponse> QuerySerialPorts(
        QuerySerialPortsRequest request, ServerCallContext context) {

        // 本查询不依赖请求字段与调用上下文
        _ = request;
        _ = context;

        var response = new QuerySerialPortsResponse();

        // 引擎模型 SerialPortInfo → 线上契约 SerialPortDescriptor。
        // 没有串口是正常状态（纯以太网现场未装串口插件），返回空列表即可，
        // 不是错误，UI 据此提示"未发现串口"并保留手工输入。
        foreach (SerialPortInfo port in _routeAssemblyService.GetAvailableSerialPorts()) {
            response.Ports.Add(new SerialPortDescriptor {
                PortName    = port.PortName ?? string.Empty,
                Description = port.Description ?? string.Empty
            });
        }

        // 空列表是正常状态（纯以太网现场），不是错误
        return Task.FromResult(response);
    }

    // ============================================================================
    // 映射
    // ============================================================================

    /// <summary>
    /// 将 RouteStatusSnapshot 转换为 gRPC RouteStatusEvent。
    /// </summary>
    /// <param name="snapshot"></param>
    /// <returns></returns>
    private static RouteStatusEvent ToStatusEvent(RouteStatusSnapshot snapshot) =>
        // 时间戳转 Unix 毫秒，客户端再还原为本地 DateTime
        new RouteStatusEvent {
            RouteId          = snapshot.RouteId,
            Online           = snapshot.Online,
            ErrorCode        = snapshot.ErrorCode.ToString(),
            ErrorMessage     = snapshot.ErrorMessage,
            TimestampUnixMs  = snapshot.TimestampUtc.ToUnixTimeMilliseconds()
        };
}
