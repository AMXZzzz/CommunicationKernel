using System;
using System.Linq;
using System.Threading.Tasks;
using CommunicationKernel.Communication.Transport.Abstractions;
using CommunicationKernel.Contracts.Models;
using CommunicationKernel.Core.Abstractions.Errors;
using CommunicationKernel.Core.Abstractions.Results;
using CommunicationKernel.Engine.Router.Abstractions;
using CommunicationKernel.Engine.Router.Models;
using CommunicationKernel.EngineHost.Grpc.V1;
using CommunicationKernel.EngineHost.Host;
using Google.Protobuf;
using Grpc.Core;

namespace CommunicationKernel.EngineHost.Services;

/// <summary>
/// -----------------------------------------------------------------------------
/// 文件: EngineHostGrpcService.cs
/// 层级: EngineHost / Services
/// 作用: EngineHost 高性能 gRPC 服务实现。
/// 说明:
/// 1) 该服务只负责请求/响应适配，不实现底层并发调度细节。
/// 2) 读写并发策略由 Router 层统一控制（同路由串行写、同键读合并）。
/// 3) 本版提供 Health/Diagnostics/RouteQuery/Read/Write。
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
            HostVersion = "1.0.0-grpc-v2",
            RouteCount = _hostRuntime.Facade.Orchestrator.ConnectionRouter.Count
        });
    }

    public override Task<DiagnosticsResponse> GetDiagnostics(DiagnosticsRequest request, ServerCallContext context) {
        _ = context;

        var diagnostics = new DiagnosticsDto {
            RouteCount = _hostRuntime.Facade.Orchestrator.ConnectionRouter.Count,
            SubscriptionCount = _hostRuntime.Facade.Orchestrator.SubscriptionHub.Count,
            WriteQueueCount = 0,
            HostVersion = "1.0.0-grpc-v2"
        };

        _ = request.IncludeQueues;
        _ = request.IncludeRoutes;
        _ = request.IncludeSubscriptions;

        return Task.FromResult(new DiagnosticsResponse {
            RequestId = Guid.NewGuid().ToString("N"),
            RouteCount = diagnostics.RouteCount,
            SubscriptionCount = diagnostics.SubscriptionCount,
            WriteQueueCount = diagnostics.WriteQueueCount,
            HostVersion = diagnostics.HostVersion ?? string.Empty
        });
    }

    public override Task<RegisterRouteResponse> RegisterRoute(RegisterRouteRequest request, ServerCallContext context) {
        _ = context;

        // 分支1：本阶段不在 gRPC 层创建真实 RouteEntry（需要插件工厂参与组装）。
        // 含义：严格避免“伪路由”进入系统，防止后续读写落空。
        return Task.FromResult(new RegisterRouteResponse {
            Success = false,
            ErrorCode = KernelErrorCode.InvalidArgument.ToString(),
            ErrorMessage = "RegisterRoute requires plugin-based route assembly and is not enabled in this phase.",
            RouteId = string.Empty
        });
    }

    public override Task<QueryRoutesResponse> QueryRoutes(QueryRoutesRequest request, ServerCallContext context) {
        _ = context;

        var allRoutes = _hostRuntime.Facade.Orchestrator.ConnectionRouter.Snapshot();

        // 分支2：按可选条件过滤（空条件代表不过滤）。
        var filtered = allRoutes.Where(route =>
            (string.IsNullOrWhiteSpace(request.ProtocolId) || string.Equals(route.Key.ProtocolId, request.ProtocolId, StringComparison.OrdinalIgnoreCase))
            && (string.IsNullOrWhiteSpace(request.TransportKind) || string.Equals(route.Key.TransportKind.ToString(), request.TransportKind, StringComparison.OrdinalIgnoreCase))
            && (string.IsNullOrWhiteSpace(request.Address) || string.Equals(route.Key.Address, request.Address, StringComparison.OrdinalIgnoreCase)));

        var response = new QueryRoutesResponse();
        foreach (RouteEntry route in filtered) {
            response.Routes.Add(new RouteItem {
                RouteId = route.Key.ToString(),
                ProtocolId = route.Key.ProtocolId,
                TransportKind = route.Key.TransportKind.ToString(),
                Address = route.Key.Address,
                Port = route.Key.Port,
                Station = route.Key.Station ?? string.Empty
            });
        }

        return Task.FromResult(response);
    }

    public override async Task<ReadResponse> Read(ReadRequest request, ServerCallContext context) {
        // 分支3：构建路由键失败时，返回参数错误。
        if (!TryCreateRouteKey(request.ProtocolId, request.TransportKind, request.Address, request.Port, request.Station, out RouteKey routeKey, out string validationError)) {
            return new ReadResponse {
                Success = false,
                ErrorCode = KernelErrorCode.InvalidArgument.ToString(),
                ErrorMessage = validationError,
                Data = ByteString.Empty
            };
        }

        // 分支4：读长度非法时直接拒绝。
        if (request.Length <= 0) {
            return new ReadResponse {
                Success = false,
                ErrorCode = KernelErrorCode.InvalidArgument.ToString(),
                ErrorMessage = "length must be greater than 0",
                Data = ByteString.Empty
            };
        }

        // 分支5：路由不存在时返回明确错误。
        if (!_hostRuntime.Facade.TryGet(routeKey, out RouteEntry? routeEntry) || routeEntry is null) {
            return new ReadResponse {
                Success = false,
                ErrorCode = KernelErrorCode.RouteNotFound.ToString(),
                ErrorMessage = "route not found",
                Data = ByteString.Empty
            };
        }

        OperationResult<byte[]> result = await _hostRuntime.Facade.ExecuteReadAsync(
            new ReadRequestKey(routeKey, request.DataAddress, request.Length),
            cancellationToken => routeEntry.ProtocolDriver.ReadAsync(routeEntry.TransportClient, request.DataAddress, request.Length, cancellationToken),
            context.CancellationToken);

        return new ReadResponse {
            Success = result.Success,
            ErrorCode = result.ErrorCode.ToString(),
            ErrorMessage = result.Success ? string.Empty : result.ErrorMessage,
            Data = result.Success ? ByteString.CopyFrom(result.Value ?? Array.Empty<byte>()) : ByteString.Empty
        };
    }

    public override async Task<WriteResponse> Write(WriteRequest request, ServerCallContext context) {
        if (!TryCreateRouteKey(request.ProtocolId, request.TransportKind, request.Address, request.Port, request.Station, out RouteKey routeKey, out string validationError)) {
            return new WriteResponse {
                Success = false,
                ErrorCode = KernelErrorCode.InvalidArgument.ToString(),
                ErrorMessage = validationError
            };
        }

        if (!_hostRuntime.Facade.TryGet(routeKey, out RouteEntry? routeEntry) || routeEntry is null) {
            return new WriteResponse {
                Success = false,
                ErrorCode = KernelErrorCode.RouteNotFound.ToString(),
                ErrorMessage = "route not found"
            };
        }

        byte[] payload = request.Data?.ToByteArray() ?? Array.Empty<byte>();
        OperationResult result = await _hostRuntime.Facade.ExecuteWriteAsync(
            routeKey,
            cancellationToken => routeEntry.ProtocolDriver.WriteAsync(routeEntry.TransportClient, request.DataAddress, payload, cancellationToken),
            context.CancellationToken);

        return new WriteResponse {
            Success = result.Success,
            ErrorCode = result.ErrorCode.ToString(),
            ErrorMessage = result.Success ? string.Empty : result.ErrorMessage
        };
    }

    private static bool TryCreateRouteKey(
        string protocolId,
        string transportKind,
        string address,
        int port,
        string? station,
        out RouteKey routeKey,
        out string error) {

        routeKey = default;
        error = string.Empty;

        // 分支6：协议标识为空，无法构建路由键。
        if (string.IsNullOrWhiteSpace(protocolId)) {
            error = "protocol_id is required";
            return false;
        }

        // 分支7：介质解析失败，返回参数错误。
        if (!Enum.TryParse(transportKind, ignoreCase: true, out TransportKind parsedTransportKind)) {
            error = "transport_kind is invalid";
            return false;
        }

        // 分支8：地址为空会降低路由唯一性，严格模式下拒绝。
        if (string.IsNullOrWhiteSpace(address)) {
            error = "address is required";
            return false;
        }

        routeKey = new RouteKey(protocolId.Trim(), parsedTransportKind, address.Trim(), port, string.IsNullOrWhiteSpace(station) ? null : station.Trim());
        return true;
    }
}
