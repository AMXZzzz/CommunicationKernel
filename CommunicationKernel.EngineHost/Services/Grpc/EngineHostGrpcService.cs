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

namespace CommunicationKernel.EngineHost.Services;

/// <summary>
/// -----------------------------------------------------------------------------
/// 文件: EngineHostGrpcService.cs
/// 层级: EngineHost / Services / Grpc
/// 作用: EngineHost gRPC 对外服务实现（协议无关请求模型）。
/// 说明:
/// 1) RegisterRoute 负责路由注册入口；底层组装由 HostRuntime 完成。
/// 2) Read/Write 仅使用 route_id，避免 UI 端依赖协议细节字段。
/// 3) WatchRouteStatus 提供实时状态流，支持多 UI 并发监控。
/// -----------------------------------------------------------------------------
/// </summary>
public sealed class EngineHostGrpcService : EngineHostApi.EngineHostApiBase {
    private readonly HostRuntime _hostRuntime;

    public EngineHostGrpcService(HostRuntime hostRuntime) {
        ArgumentNullException.ThrowIfNull(hostRuntime);
        _hostRuntime = hostRuntime;
    }

    public override Task<HealthResponse> Health(HealthRequest request, ServerCallContext context) {
        _ = request;
        _ = context;

        return Task.FromResult(new HealthResponse {
            Ok = true,
            HostVersion = "1.0.0-grpc-v3",
            RouteCount = _hostRuntime.Facade.Orchestrator.ConnectionRouter.Count
        });
    }

    public override Task<DiagnosticsResponse> GetDiagnostics(DiagnosticsRequest request, ServerCallContext context) {
        _ = context;
        _ = request;

        return Task.FromResult(new DiagnosticsResponse {
            RequestId = Guid.NewGuid().ToString("N"),
            RouteCount = _hostRuntime.Facade.Orchestrator.ConnectionRouter.Count,
            SubscriptionCount = _hostRuntime.Facade.Orchestrator.SubscriptionHub.Count,
            WriteQueueCount = 0,
            HostVersion = "1.0.0-grpc-v3"
        });
    }

    public override async Task<RegisterRouteResponse> RegisterRoute(RegisterRouteRequest request, ServerCallContext context) {
        // 分支1：请求体为空字符串字段会在运行时层统一校验并返回规范错误。
        var command = new HostRuntime.RegisterRouteCommand {
            RouteId = request.RouteId,
            ProtocolId = request.ProtocolId,
            TransportId = request.TransportId,
            TransportKind = request.TransportKind,
            Address = request.Address,
            Port = request.Port,
            Station = request.Station,
            SerialPort = request.SerialPort,
            BaudRate = request.BaudRate,
            MinIoIntervalMs = request.MinIoIntervalMs
        };

        OperationResult<string> register = await _hostRuntime.RegisterRouteAsync(command, context.CancellationToken).ConfigureAwait(false);
        return new RegisterRouteResponse {
            Success = register.Success,
            ErrorCode = register.ErrorCode.ToString(),
            ErrorMessage = register.Success ? string.Empty : register.ErrorMessage,
            RouteId = register.Success ? register.Value ?? string.Empty : string.Empty
        };
    }

    public override Task<QueryRoutesResponse> QueryRoutes(QueryRoutesRequest request, ServerCallContext context) {
        _ = context;

        var routes = _hostRuntime.SnapshotRoutes();
        var filtered = routes.Where(route =>
            (string.IsNullOrWhiteSpace(request.RouteId) || string.Equals(route.RouteId, request.RouteId, StringComparison.OrdinalIgnoreCase))
            && (string.IsNullOrWhiteSpace(request.ProtocolId) || string.Equals(route.RouteKey.ProtocolId, request.ProtocolId, StringComparison.OrdinalIgnoreCase))
            && (string.IsNullOrWhiteSpace(request.TransportKind) || string.Equals(route.RouteKey.TransportKind.ToString(), request.TransportKind, StringComparison.OrdinalIgnoreCase))
            && (string.IsNullOrWhiteSpace(request.Address) || string.Equals(route.RouteKey.Address, request.Address, StringComparison.OrdinalIgnoreCase)));

        var response = new QueryRoutesResponse();
        foreach (HostRuntime.RouteRuntimeInfo route in filtered) {
            response.Routes.Add(new RouteItem {
                RouteId = route.RouteId,
                ProtocolId = route.RouteKey.ProtocolId,
                TransportId = route.TransportId,
                TransportKind = route.RouteKey.TransportKind.ToString(),
                Address = route.RouteKey.Address,
                Port = route.RouteKey.Port,
                Station = route.RouteKey.Station ?? string.Empty,
                SerialPort = route.Endpoint.SerialPort ?? string.Empty,
                BaudRate = route.Endpoint.BaudRate ?? 0
            });
        }

        return Task.FromResult(response);
    }

    public override async Task<ReadResponse> Read(ReadRequest request, ServerCallContext context) {
        // 分支2：route_id 为空时直接拒绝，确保协议无关模型入口严谨。
        if (string.IsNullOrWhiteSpace(request.RouteId)) {
            return new ReadResponse {
                Success = false,
                ErrorCode = KernelErrorCode.InvalidArgument.ToString(),
                ErrorMessage = "route_id is required",
                Data = ByteString.Empty
            };
        }

        OperationResult<byte[]> result = await _hostRuntime
            .ReadByRouteIdAsync(request.RouteId, request.DataAddress, request.Length, context.CancellationToken)
            .ConfigureAwait(false);

        return new ReadResponse {
            Success = result.Success,
            ErrorCode = result.ErrorCode.ToString(),
            ErrorMessage = result.Success ? string.Empty : result.ErrorMessage,
            Data = result.Success ? ByteString.CopyFrom(result.Value ?? Array.Empty<byte>()) : ByteString.Empty
        };
    }

    public override async Task<WriteResponse> Write(WriteRequest request, ServerCallContext context) {
        if (string.IsNullOrWhiteSpace(request.RouteId)) {
            return new WriteResponse {
                Success = false,
                ErrorCode = KernelErrorCode.InvalidArgument.ToString(),
                ErrorMessage = "route_id is required"
            };
        }

        byte[] payload = request.Data?.ToByteArray() ?? Array.Empty<byte>();
        OperationResult result = await _hostRuntime
            .WriteByRouteIdAsync(request.RouteId, request.DataAddress, payload, context.CancellationToken)
            .ConfigureAwait(false);

        return new WriteResponse {
            Success = result.Success,
            ErrorCode = result.ErrorCode.ToString(),
            ErrorMessage = result.Success ? string.Empty : result.ErrorMessage
        };
    }

    public override async Task WatchRouteStatus(
        WatchRouteStatusRequest request,
        IServerStreamWriter<RouteStatusEvent> responseStream,
        ServerCallContext context) {

        // 先下发当前状态快照，避免客户端刚连上时无状态可用。
        foreach (HostRuntime.RouteStatusSnapshot snapshot in _hostRuntime.SnapshotStatuses(request.RouteId)) {
            await responseStream.WriteAsync(ToStatusEvent(snapshot)).ConfigureAwait(false);
        }

        var channel = Channel.CreateUnbounded<HostRuntime.RouteStatusSnapshot>();

        void OnStatus(HostRuntime.RouteStatusSnapshot snapshot) {
            // 分支3：请求指定 route_id 时，只透传目标路由事件。
            if (!string.IsNullOrWhiteSpace(request.RouteId)
                && !string.Equals(request.RouteId, snapshot.RouteId, StringComparison.OrdinalIgnoreCase)) {
                return;
            }

            channel.Writer.TryWrite(snapshot);
        }

        _hostRuntime.RouteStatusChanged += OnStatus;
        try {
            while (!context.CancellationToken.IsCancellationRequested) {
                HostRuntime.RouteStatusSnapshot snapshot = await channel.Reader.ReadAsync(context.CancellationToken).ConfigureAwait(false);
                await responseStream.WriteAsync(ToStatusEvent(snapshot)).ConfigureAwait(false);
            }
        } catch (OperationCanceledException) {
            // 客户端取消订阅属于正常退出路径。
        } finally {
            _hostRuntime.RouteStatusChanged -= OnStatus;
            channel.Writer.TryComplete();
        }
    }

    private static RouteStatusEvent ToStatusEvent(HostRuntime.RouteStatusSnapshot snapshot) {
        return new RouteStatusEvent {
            RouteId = snapshot.RouteId,
            Online = snapshot.Online,
            ErrorCode = snapshot.ErrorCode.ToString(),
            ErrorMessage = snapshot.ErrorMessage,
            TimestampUnixMs = snapshot.TimestampUtc.ToUnixTimeMilliseconds()
        };
    }
}
