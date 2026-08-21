// -----------------------------------------------------------------------------
// 文件: Services/EngineHostWebClient.cs
// 层级: UI 层 — Blazor Web 客户端服务
// 作用: 与 WPF 端的 EngineHostGrpcClient 对等，封装所有 gRPC 调用。
//       Blazor Server 端使用，在服务器端建立 gRPC 连接，通过 SignalR 同步到浏览器。
//       注意：Blazor Server 可直接用标准 gRPC（服务端到服务端），
//             无需 gRPC-Web（gRPC-Web 仅在浏览器端 WASM 中必须）。
// 调用链:
//   Blazor 组件 → EngineHostWebClient → gRPC Channel → EngineHost gRPC Server
// -----------------------------------------------------------------------------

using CommunicationKernel.EngineHost.Grpc.V1;   // 由 Grpc.Tools 生成
using Grpc.Core;
using Grpc.Net.Client;
using Microsoft.Extensions.Logging;

namespace CommunicationKernel.UI.Web.Services;

// =============================================================================
// DTO（与 WPF 端共享相同设计，两个项目独立编译，不共享程序集）
// =============================================================================

/// <summary>路由信息 DTO。</summary>
public sealed record RouteDto(
    string RouteId,
    string ProtocolId,
    string TransportKind,
    string Address,
    int    Port,
    string Station);

/// <summary>路由状态事件 DTO（来自 WatchRouteStatus 流）。</summary>
public sealed record RouteStatusDto(
    string   RouteId,
    bool     Online,
    string   ErrorCode,
    string   ErrorMessage,
    DateTime Timestamp);

/// <summary>读取结果 DTO。</summary>
public sealed record ReadResultDto(bool Success, string ErrorCode, string ErrorMessage, byte[] Data);

/// <summary>写入结果 DTO。</summary>
public sealed record WriteResultDto(bool Success, string ErrorCode, string ErrorMessage);

// =============================================================================
// gRPC 客户端封装（Blazor Server 版）
// =============================================================================

/// <summary>
/// Blazor Server 端的 EngineHostApi gRPC 客户端封装。
/// 生命周期：Scoped（每个 SignalR 连接/用户会话一个实例），
///           由 DI 容器在会话结束时 DisposeAsync。
/// </summary>
public sealed class EngineHostWebClient : IAsyncDisposable {

    // -------------------------------------------------------------------------
    // 私有字段
    // -------------------------------------------------------------------------

    /// <summary>gRPC 通道（HTTP/2 连接池）。</summary>
    private readonly GrpcChannel _channel;

    /// <summary>由 proto 生成的强类型 stub。</summary>
    private readonly EngineHostApi.EngineHostApiClient _stub;

    private readonly ILogger<EngineHostWebClient> _logger;

    // -------------------------------------------------------------------------
    // 构造函数
    // -------------------------------------------------------------------------

    /// <param name="serverAddress">EngineHost gRPC 地址，例如 "http://localhost:5000"。</param>
    public EngineHostWebClient(string serverAddress, ILogger<EngineHostWebClient>? logger = null) {
        _logger  = logger ?? Microsoft.Extensions.Logging.Abstractions.NullLogger<EngineHostWebClient>.Instance;
        _channel = GrpcChannel.ForAddress(serverAddress);
        _stub    = new EngineHostApi.EngineHostApiClient(_channel);
        _logger.LogInformation("Web gRPC 客户端初始化: {Address}", serverAddress);
    }

    // -------------------------------------------------------------------------
    // Health
    // -------------------------------------------------------------------------

    /// <summary>健康检查，5 秒超时，网络失败时返回 (false, "", 0)。</summary>
    public async Task<(bool Ok, string HostVersion, int RouteCount)> HealthAsync(
        CancellationToken ct = default) {
        try {
            HealthResponse r = await _stub.HealthAsync(new HealthRequest(),
                deadline: DateTime.UtcNow.AddSeconds(5),
                cancellationToken: ct).ConfigureAwait(false);
            return (r.Ok, r.HostVersion, r.RouteCount);
        }
        catch (RpcException ex) {
            _logger.LogWarning("Health 失败: {Status}", ex.Status.StatusCode);
            return (false, string.Empty, 0);
        }
    }

    // -------------------------------------------------------------------------
    // RegisterRoute
    // -------------------------------------------------------------------------

    public async Task<(bool Success, string ErrorCode, string ErrorMessage, string RouteId)>
        RegisterRouteAsync(
            string routeId, string protocolId, string transportKind,
            string address, int port, string station, int minIoMs = 100,
            CancellationToken ct = default) {
        try {
            RegisterRouteResponse r = await _stub.RegisterRouteAsync(
                new RegisterRouteRequest {
                    RouteId = routeId, ProtocolId = protocolId,
                    TransportKind = transportKind, Address = address,
                    Port = port, Station = station, MinIoIntervalMs = minIoMs,
                }, cancellationToken: ct).ConfigureAwait(false);

            if (!r.Success)
                _logger.LogWarning("RegisterRoute 失败: {Code} {Msg}", r.ErrorCode, r.ErrorMessage);
            return (r.Success, r.ErrorCode, r.ErrorMessage, r.RouteId);
        }
        catch (RpcException ex) {
            _logger.LogError(ex, "RegisterRoute RPC 异常");
            return (false, "RPC_ERROR", ex.Status.Detail, routeId);
        }
    }

    // -------------------------------------------------------------------------
    // QueryRoutes
    // -------------------------------------------------------------------------

    public async Task<IReadOnlyList<RouteDto>> QueryRoutesAsync(
        string routeId = "", string protocolId = "",
        string transportKind = "", string address = "",
        CancellationToken ct = default) {
        try {
            QueryRoutesResponse r = await _stub.QueryRoutesAsync(
                new QueryRoutesRequest {
                    RouteId = routeId, ProtocolId = protocolId,
                    TransportKind = transportKind, Address = address,
                }, cancellationToken: ct).ConfigureAwait(false);

            return r.Routes
                .Select(x => new RouteDto(x.RouteId, x.ProtocolId, x.TransportKind,
                                          x.Address, x.Port, x.Station))
                .ToList();
        }
        catch (RpcException ex) {
            _logger.LogError(ex, "QueryRoutes RPC 异常");
            return Array.Empty<RouteDto>();
        }
    }

    // -------------------------------------------------------------------------
    // Read
    // -------------------------------------------------------------------------

    public async Task<ReadResultDto> ReadAsync(
        string routeId, string dataAddress, int length,
        CancellationToken ct = default) {
        try {
            ReadResponse r = await _stub.ReadAsync(
                new ReadRequest { RouteId = routeId, DataAddress = dataAddress, Length = length },
                cancellationToken: ct).ConfigureAwait(false);
            return new ReadResultDto(r.Success, r.ErrorCode, r.ErrorMessage, r.Data.ToByteArray());
        }
        catch (RpcException ex) {
            _logger.LogError(ex, "Read RPC 异常: {RouteId}/{Addr}", routeId, dataAddress);
            return new ReadResultDto(false, "RPC_ERROR", ex.Status.Detail, Array.Empty<byte>());
        }
    }

    // -------------------------------------------------------------------------
    // Write
    // -------------------------------------------------------------------------

    public async Task<WriteResultDto> WriteAsync(
        string routeId, string dataAddress, byte[] data,
        CancellationToken ct = default) {
        try {
            WriteResponse r = await _stub.WriteAsync(
                new WriteRequest {
                    RouteId = routeId, DataAddress = dataAddress,
                    Data = Google.Protobuf.ByteString.CopyFrom(data),
                }, cancellationToken: ct).ConfigureAwait(false);
            return new WriteResultDto(r.Success, r.ErrorCode, r.ErrorMessage);
        }
        catch (RpcException ex) {
            _logger.LogError(ex, "Write RPC 异常: {RouteId}/{Addr}", routeId, dataAddress);
            return new WriteResultDto(false, "RPC_ERROR", ex.Status.Detail);
        }
    }

    // -------------------------------------------------------------------------
    // WatchRouteStatus
    // -------------------------------------------------------------------------

    /// <summary>
    /// 订阅路由状态流。异步枚举器版本，适合 Blazor 组件在 OnInitializedAsync 中使用。
    /// </summary>
    public async IAsyncEnumerable<RouteStatusDto> WatchRouteStatusAsync(
        string routeId,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct = default) {

        AsyncServerStreamingCall<RouteStatusEvent>? call = null;
        try {
            // 建立服务端流
            call = _stub.WatchRouteStatus(
                new WatchRouteStatusRequest { RouteId = routeId },
                cancellationToken: ct);

            // 逐条 yield 给调用方
            await foreach (RouteStatusEvent evt in call.ResponseStream.ReadAllAsync(ct)) {
                DateTime ts = DateTimeOffset
                    .FromUnixTimeMilliseconds(evt.TimestampUnixMs)
                    .LocalDateTime;

                yield return new RouteStatusDto(evt.RouteId, evt.Online,
                                                evt.ErrorCode, evt.ErrorMessage, ts);
            }
        }
        finally {
            // 确保调用结束时释放 call 对象
            call?.Dispose();
        }
    }

    // -------------------------------------------------------------------------
    // IAsyncDisposable
    // -------------------------------------------------------------------------

    public async ValueTask DisposeAsync() {
        await _channel.ShutdownAsync().ConfigureAwait(false);
        _channel.Dispose();
    }
}
