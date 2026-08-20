using System;
using System.Linq;
using System.Threading.Channels;
using System.Threading.Tasks;
using CommunicationKernel.Core.Abstractions.Errors;
using CommunicationKernel.Core.Abstractions.Results;
using CommunicationKernel.EngineHost.Grpc.V1;
using CommunicationKernel.EngineHost.Host;
using Google.Protobuf;
using Grpc.Core;
using Microsoft.Extensions.Logging;

namespace CommunicationKernel.EngineHost.Services;

/// <summary>
/// -----------------------------------------------------------------------------
/// 文件: EngineHostGrpcService.cs
/// 层级: EngineHost / Services / Grpc
/// 作用: EngineHost gRPC 对外服务实现（协议无关请求模型）。
/// 说明:
/// 1) RegisterRoute 负责路由注册入口；底层组装由 HostRuntime 完成。
/// 2) Read/Write 仅使用 route_id，避免 UI 端依赖协议细节字段。
/// 3) WatchRouteStatus 使用有界 Channel（容量 256，溢出丢旧）防止慢客户端
///    导致内存无限增长；状态采用最终一致模型，丢失旧快照不影响正确性。
/// -----------------------------------------------------------------------------
/// </summary>
public sealed class EngineHostGrpcService : EngineHostApi.EngineHostApiBase {
    // 服务版本常量集中定义，避免多处硬编码。
    private const string HostVersion = "1.0.0-grpc-v3";

    // 状态推流 Channel 容量：超出时丢弃最旧快照（最终一致，不影响准确性）。
    private const int StatusChannelCapacity = 256;

    private readonly HostRuntime _hostRuntime;
    private readonly ILogger<EngineHostGrpcService> _logger;

    public EngineHostGrpcService(HostRuntime hostRuntime, ILogger<EngineHostGrpcService> logger) {
        ArgumentNullException.ThrowIfNull(hostRuntime);
        ArgumentNullException.ThrowIfNull(logger);
        _hostRuntime = hostRuntime;
        _logger      = logger;
    }

    public override Task<HealthResponse> Health(HealthRequest request, ServerCallContext context) {
        _ = request;
        _ = context;
        return Task.FromResult(new HealthResponse {
            Ok           = true,
            HostVersion  = HostVersion,
            RouteCount   = _hostRuntime.RouteCount
        });
    }

    public override Task<DiagnosticsResponse> GetDiagnostics(DiagnosticsRequest request, ServerCallContext context) {
        _ = context;
        _ = request;
        return Task.FromResult(new DiagnosticsResponse {
            RequestId         = Guid.NewGuid().ToString("N"),
            RouteCount        = _hostRuntime.RouteCount,
            SubscriptionCount = _hostRuntime.SubscriptionCount,
            WriteQueueCount   = 0,
            HostVersion       = HostVersion
        });
    }

    public override async Task<RegisterRouteResponse> RegisterRoute(RegisterRouteRequest request, ServerCallContext context) {
        var command = new HostRuntime.RegisterRouteCommand {
            RouteId       = request.RouteId,
            ProtocolId    = request.ProtocolId,
            TransportId   = request.TransportId,
            TransportKind = request.TransportKind,
            Address       = request.Address,
            Port          = request.Port,
            Station       = request.Station,
            SerialPort    = request.SerialPort,
            BaudRate      = request.BaudRate,
            MinIoIntervalMs = request.MinIoIntervalMs
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

    public override Task<QueryRoutesResponse> QueryRoutes(QueryRoutesRequest request, ServerCallContext context) {
        _ = context;

        var routes = _hostRuntime.SnapshotRoutes();
        var filtered = routes.Where(r =>
            (string.IsNullOrWhiteSpace(request.RouteId)      || string.Equals(r.RouteId,                    request.RouteId,      StringComparison.OrdinalIgnoreCase))
            && (string.IsNullOrWhiteSpace(request.ProtocolId) || string.Equals(r.RouteKey.ProtocolId,        request.ProtocolId,   StringComparison.OrdinalIgnoreCase))
            && (string.IsNullOrWhiteSpace(request.TransportKind) || string.Equals(r.RouteKey.TransportKind.ToString(), request.TransportKind, StringComparison.OrdinalIgnoreCase))
            && (string.IsNullOrWhiteSpace(request.Address)    || string.Equals(r.RouteKey.Address,           request.Address,      StringComparison.OrdinalIgnoreCase)));

        var response = new QueryRoutesResponse();
        foreach (HostRuntime.RouteRuntimeInfo r in filtered) {
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

    public override async Task WatchRouteStatus(
        WatchRouteStatusRequest request,
        IServerStreamWriter<RouteStatusEvent> responseStream,
        ServerCallContext context) {

        // 先下发当前快照，避免客户端首连时无状态可用。
        foreach (HostRuntime.RouteStatusSnapshot snapshot in _hostRuntime.SnapshotStatuses(request.RouteId)) {
            await responseStream.WriteAsync(ToStatusEvent(snapshot)).ConfigureAwait(false);
        }

        // 有界 Channel（容量 256，溢出丢最旧）：防止慢客户端导致内存无限增长。
        var channel = Channel.CreateBounded<HostRuntime.RouteStatusSnapshot>(
            new BoundedChannelOptions(StatusChannelCapacity) {
                FullMode = BoundedChannelFullMode.DropOldest
            });

        void OnStatus(HostRuntime.RouteStatusSnapshot snapshot) {
            if (!string.IsNullOrWhiteSpace(request.RouteId)
                && !string.Equals(request.RouteId, snapshot.RouteId, StringComparison.OrdinalIgnoreCase))
                return;

            if (!channel.Writer.TryWrite(snapshot))
                _logger.LogWarning("WatchRouteStatus: status channel full, dropped event for route '{RouteId}'.", snapshot.RouteId);
        }

        _hostRuntime.RouteStatusChanged += OnStatus;
        try {
            while (!context.CancellationToken.IsCancellationRequested) {
                HostRuntime.RouteStatusSnapshot snapshot =
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

    private static RouteStatusEvent ToStatusEvent(HostRuntime.RouteStatusSnapshot snapshot) =>
        new RouteStatusEvent {
            RouteId          = snapshot.RouteId,
            Online           = snapshot.Online,
            ErrorCode        = snapshot.ErrorCode.ToString(),
            ErrorMessage     = snapshot.ErrorMessage,
            TimestampUnixMs  = snapshot.TimestampUtc.ToUnixTimeMilliseconds()
        };
}
