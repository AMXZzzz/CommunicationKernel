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

    //! 依赖注入的 EngineRuntime 与路由装配服务
    private readonly EngineRuntime _hostRuntime;

    //! 依赖注入的路由装配服务（提供协议插件清单）
    private readonly IRouteAssemblyService _routeAssemblyService;

    //! 依赖注入的日志记录器
    private readonly ILogger<HostGrpcService> _logger;

    //! 构造函数：注入 EngineRuntime、路由装配服务和日志记录器
    public HostGrpcService (
        //! hostRuntime 提供路由注册、读写、状态订阅等核心功能
        EngineRuntime hostRuntime,
        //! routeAssemblyService 提供协议插件清单，用于 QueryProtocols
        IRouteAssemblyService routeAssemblyService,
        //! logger 提供日志记录功能
        ILogger<HostGrpcService> logger) {

        //! 校验依赖注入参数，确保非空
        ArgumentNullException.ThrowIfNull(hostRuntime);
        ArgumentNullException.ThrowIfNull(routeAssemblyService);
        ArgumentNullException.ThrowIfNull(logger);

        //! 赋值依赖注入参数到私有字段
        _hostRuntime            = hostRuntime;
        _routeAssemblyService   = routeAssemblyService;
        _logger                 = logger;
    }

    /// <summary>
    /// 健康检查：返回服务端版本、路由数量等基本信息，供客户端（UI 层）监控。
    /// </summary>
    /// <param name="request"></param>
    /// <param name="context"></param>
    /// <returns></returns>
    public override Task<HealthResponse> Health(HealthRequest request, ServerCallContext context) {
        _ = request;
        _ = context;
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
        _ = context;
        _ = request;
        return Task.FromResult(new DiagnosticsResponse {
            RequestId         = Guid.NewGuid().ToString("N"),
            RouteCount        = _hostRuntime.RouteCount,
            PendingRouteCount = _hostRuntime.PendingRouteCount,
            HostVersion       = HostVersion
        });
    }

    /// <summary>
    /// 注册路由：用于一次性注册路由并完成插件组装。
    /// </summary>
    /// <param name="request"></param>
    /// <param name="context"></param>
    /// <returns></returns>
    public override async Task<RegisterRouteResponse> RegisterRoute(RegisterRouteRequest request, ServerCallContext context) {
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

        OperationResult<string> register = await _hostRuntime
            .RegisterRouteAsync(command, context.CancellationToken)
            .ConfigureAwait(false);

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
        _ = context;

        var routes = _hostRuntime.SnapshotRoutes();
        var filtered = routes.Where(r =>
            (string.IsNullOrWhiteSpace(request.RouteId)      || string.Equals(r.RouteId,                    request.RouteId,      StringComparison.OrdinalIgnoreCase))
            && (string.IsNullOrWhiteSpace(request.ProtocolId) || string.Equals(r.RouteKey.ProtocolId,        request.ProtocolId,   StringComparison.OrdinalIgnoreCase))
            && (string.IsNullOrWhiteSpace(request.TransportKind) || string.Equals(r.RouteKey.TransportKind.ToString(), request.TransportKind, StringComparison.OrdinalIgnoreCase))
            && (string.IsNullOrWhiteSpace(request.Address)    || string.Equals(r.RouteKey.Address,           request.Address,      StringComparison.OrdinalIgnoreCase)));

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

        return Task.FromResult(response);
    }
    
    /// <summary>
    /// 读取数据：根据路由 ID 和数据地址读取数据，供客户端（UI 层）使用。
    /// </summary>
    /// <param name="request"></param>
    /// <param name="context"></param>
    /// <returns></returns> 
    public override async Task<ReadResponse> Read(ReadRequest request, ServerCallContext context) {
        if (string.IsNullOrWhiteSpace(request.RouteId)) {
            return new ReadResponse {
                Success      = false,
                ErrorCode    = KernelErrorCode.InvalidArgument.ToString(),
                ErrorMessage = "route_id is required",
                Data         = ByteString.Empty
            };
        }

        OperationResult<byte[]> result = await _hostRuntime
            .ReadByRouteIdAsync(request.RouteId, request.DataAddress, request.Length, context.CancellationToken)
            .ConfigureAwait(false);

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
        if (string.IsNullOrWhiteSpace(request.RouteId)) {
            return new WriteResponse {
                Success      = false,
                ErrorCode    = KernelErrorCode.InvalidArgument.ToString(),
                ErrorMessage = "route_id is required"
            };
        }

        byte[] payload = request.Data?.ToByteArray() ?? Array.Empty<byte>();
        OperationResult result = await _hostRuntime
            .WriteByRouteIdAsync(request.RouteId, request.DataAddress, payload, context.CancellationToken)
            .ConfigureAwait(false);

        return new WriteResponse {
            Success      = result.Success,
            ErrorCode    = result.ErrorCode.ToString(),
            ErrorMessage = result.Success ? string.Empty : result.ErrorMessage
        };
    }

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
            if (!string.IsNullOrWhiteSpace(request.RouteId)
                && !string.Equals(request.RouteId, snapshot.RouteId, StringComparison.OrdinalIgnoreCase))
                return;

            if (!channel.Writer.TryWrite(snapshot))
                _logger.LogWarning("WatchRouteStatus: status channel full, dropped event for route '{RouteId}'.", snapshot.RouteId);
        }

        // 必须「先订阅、再发快照」。
        // 反过来会留下一个窗口：快照已取、订阅尚未挂上，该窗口内发生的状态变化
        // 既不在快照里、也不会被推送，客户端将永久停留在过期状态。
        // 先订阅可能导致快照与首批事件重复，但状态是最终一致模型，重复无害。
        _hostRuntime.RouteStatusChanged += OnStatus;
        try {
            foreach (RouteStatusSnapshot snapshot in _hostRuntime.SnapshotStatuses(request.RouteId)) {
                await responseStream.WriteAsync(ToStatusEvent(snapshot)).ConfigureAwait(false);
            }

            while (!context.CancellationToken.IsCancellationRequested) {
                RouteStatusSnapshot snapshot =
                    await channel.Reader.ReadAsync(context.CancellationToken).ConfigureAwait(false);
                await responseStream.WriteAsync(ToStatusEvent(snapshot)).ConfigureAwait(false);
            }
        } catch (OperationCanceledException) {
            // 客户端取消订阅：正常退出。
        } finally {
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

        OperationResult result = await _hostRuntime
            .UnregisterRouteAsync(request.RouteId, context.CancellationToken)
            .ConfigureAwait(false);

        return new RemoveRouteResponse {
            Success      = result.Success,
            ErrorCode    = result.ErrorCode.ToString(),
            ErrorMessage = result.Success ? string.Empty : result.ErrorMessage
        };
    }

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

        _ = request;
        _ = context;

        var response = new QueryProtocolsResponse();
        foreach (ProtocolMetadata meta in _routeAssemblyService.GetAvailableProtocols()) {
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

            response.Protocols.Add(descriptor);
        }

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

        _ = request;
        _ = context;

        var response = new QuerySerialPortsResponse();

        // 没有串口是正常状态（纯以太网现场未装串口插件），返回空列表即可，
        // 不是错误，UI 据此提示"未发现串口"并保留手工输入。
        // 传输层的 SerialPortDescriptor 与 Protobuf 生成的同名类型重名，
        // 用别名区分：左边是引擎侧模型，右边是线上契约。
        foreach (CommunicationKernel.Communication.Transport.Abstractions.SerialPortDescriptor port
                 in _routeAssemblyService.GetAvailableSerialPorts()) {
            response.Ports.Add(new Grpc.V1.SerialPortDescriptor {
                PortName    = port.PortName ?? string.Empty,
                Description = port.Description ?? string.Empty
            });
        }

        return Task.FromResult(response);
    }

    /// <summary>
    /// 将 RouteStatusSnapshot 转换为 gRPC RouteStatusEvent。
    /// </summary>
    /// <param name="snapshot"></param>
    /// <returns></returns>
    private static RouteStatusEvent ToStatusEvent(RouteStatusSnapshot snapshot) =>
        new RouteStatusEvent {
            RouteId          = snapshot.RouteId,
            Online           = snapshot.Online,
            ErrorCode        = snapshot.ErrorCode.ToString(),
            ErrorMessage     = snapshot.ErrorMessage,
            TimestampUnixMs  = snapshot.TimestampUtc.ToUnixTimeMilliseconds()
        };
}
