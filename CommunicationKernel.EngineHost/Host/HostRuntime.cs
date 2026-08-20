using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using CommunicationKernel.Communication.Transport.Abstractions;
using CommunicationKernel.Core.Abstractions.Errors;
using CommunicationKernel.Core.Abstractions.Results;
using CommunicationKernel.Engine.Router;
using CommunicationKernel.Engine.Router.Abstractions;
using CommunicationKernel.Engine.Router.Models;

namespace CommunicationKernel.EngineHost.Host;

/// <summary>
/// -----------------------------------------------------------------------------
/// 文件: HostRuntime.cs
/// 层级: EngineHost / Host
/// 作用: Host 运行时主控入口，负责路由注册与运行时执行策略。
/// 说明:
/// 1) 对外暴露 Facade 统一入口；对内管理路由运行时上下文。
/// 2) RegisterRoute 通过装配抽象服务完成 RouteEntry 获取并落表。
/// 3) 统一处理串口节流、单次重连、状态发布等企业级运行策略。
/// -----------------------------------------------------------------------------
/// </summary>
public sealed class HostRuntime {
    private readonly ConcurrentDictionary<string, RouteRuntimeRegistration> _registrationsByRouteId =
        new(StringComparer.OrdinalIgnoreCase);

    private readonly ConcurrentDictionary<RouteKey, string> _routeIdByKey = new();
    private readonly ConcurrentDictionary<string, RouteStatusSnapshot> _routeStatuses =
        new(StringComparer.OrdinalIgnoreCase);

    private readonly IRouteAssemblyService _routeAssemblyService;

    /// <summary>
    /// 当路由状态变化时触发，用于 gRPC 实时推流。
    /// </summary>
    public event Action<RouteStatusSnapshot>? RouteStatusChanged;

    /// <summary>
    /// 创建 Host 运行时。
    /// </summary>
    /// <param name="orchestrator">可选编排器；为空时创建默认实现。</param>
    /// <param name="routeAssemblyService">路由装配服务（负责协议/传输插件组装）。</param>
    public HostRuntime(
        IRouteAssemblyService routeAssemblyService,
        IRouterOrchestrator? orchestrator = null) {

        ArgumentNullException.ThrowIfNull(routeAssemblyService);

        // 组合根职责：统一在 HostRuntime 装配编排器实现。
        IRouterOrchestrator resolvedOrchestrator = orchestrator ?? new RouterOrchestrator();
        Facade = new EngineHostFacade(resolvedOrchestrator);

        _routeAssemblyService = routeAssemblyService;
    }

    /// <summary>
    /// Host 对外统一门面。
    /// </summary>
    public EngineHostFacade Facade { get; }

    /// <summary>
    /// 获取路由快照（用于查询接口）。
    /// </summary>
    public IReadOnlyList<RouteRuntimeInfo> SnapshotRoutes() {
        return _registrationsByRouteId.Values
            .Select(registration => new RouteRuntimeInfo {
                RouteId = registration.RouteId,
                RouteKey = registration.RouteKey,
                Endpoint = registration.Endpoint,
                TransportId = registration.TransportId
            })
            .ToList();
    }

    /// <summary>
    /// 获取状态快照（用于流式订阅初始化）。
    /// </summary>
    public IReadOnlyList<RouteStatusSnapshot> SnapshotStatuses(string? routeId = null) {
        if (string.IsNullOrWhiteSpace(routeId)) {
            return _routeStatuses.Values.ToList();
        }

        return _routeStatuses.TryGetValue(routeId, out RouteStatusSnapshot? snapshot)
            ? new[] { snapshot }
            : Array.Empty<RouteStatusSnapshot>();
    }

    /// <summary>
    /// 注册路由并完成插件工厂组装。
    /// </summary>
    public async Task<OperationResult<string>> RegisterRouteAsync(RegisterRouteCommand command, CancellationToken cancellationToken) {
        ArgumentNullException.ThrowIfNull(command);

        // 分支1：route_id 必填。缺失时拒绝注册。
        if (string.IsNullOrWhiteSpace(command.RouteId)) {
            return OperationResult<string>.Fail("route_id is required", KernelErrorCode.InvalidArgument);
        }

        // 分支2：委托装配服务构建 RouteEntry 与连接状态。
        OperationResult<RouteAssemblyResult> assemble = await _routeAssemblyService
            .AssembleAsync(command, cancellationToken)
            .ConfigureAwait(false);

        if (!assemble.Success) {
            return OperationResult<string>.Fail(assemble.ErrorMessage, assemble.ErrorCode);
        }

        RouteAssemblyResult assembled = assemble.Value;

        // 分支3：路由键已存在，直接拒绝。
        if (_routeIdByKey.ContainsKey(assembled.RouteKey)) {
            await assembled.RollbackAsync(cancellationToken).ConfigureAwait(false);
            return OperationResult<string>.Fail($"route already exists: {assembled.RouteKey}", KernelErrorCode.RouteBusy);
        }

        // 分支4：注册到 Router；失败时回滚连接资源。
        if (!Facade.TryRegister(assembled.RouteEntry)) {
            await assembled.RollbackAsync(cancellationToken).ConfigureAwait(false);
            return OperationResult<string>.Fail("router rejected route registration", KernelErrorCode.RouteBusy);
        }

        string resolvedRouteId = command.RouteId.Trim();
        var registration = new RouteRuntimeRegistration(
            resolvedRouteId,
            assembled.RouteKey,
            assembled.Endpoint,
            assembled.TransportId,
            assembled.RouteEntry,
            assembled.IsSerialRoute,
            assembled.MinIoIntervalMs);

        _registrationsByRouteId[resolvedRouteId] = registration;
        _routeIdByKey[assembled.RouteKey] = resolvedRouteId;

        PublishStatus(resolvedRouteId, online: true, KernelErrorCode.None, string.Empty);
        return OperationResult<string>.Ok(resolvedRouteId);
    }

    /// <summary>
    /// 通过 route_id 执行读取。
    /// </summary>
    public async Task<OperationResult<byte[]>> ReadByRouteIdAsync(
        string routeId,
        string dataAddress,
        int length,
        CancellationToken cancellationToken) {

        if (!_registrationsByRouteId.TryGetValue(routeId, out RouteRuntimeRegistration? registration)) {
            return OperationResult<byte[]>.Fail("route not found", KernelErrorCode.RouteNotFound);
        }

        if (length <= 0) {
            return OperationResult<byte[]>.Fail("length must be greater than 0", KernelErrorCode.InvalidArgument);
        }

        return await Facade.ExecuteReadAsync(
            new ReadRequestKey(registration.RouteKey, dataAddress, length),
            token => ExecuteWithRoutePolicyAsync(
                registration,
                ct => registration.RouteEntry.ProtocolDriver.ReadAsync(
                    registration.RouteEntry.TransportClient,
                    dataAddress,
                    length,
                    ct),
                token),
            cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// 通过 route_id 执行写入。
    /// </summary>
    public async Task<OperationResult> WriteByRouteIdAsync(
        string routeId,
        string dataAddress,
        byte[] payload,
        CancellationToken cancellationToken) {

        if (!_registrationsByRouteId.TryGetValue(routeId, out RouteRuntimeRegistration? registration)) {
            return OperationResult.Fail("route not found", KernelErrorCode.RouteNotFound);
        }

        byte[] effectivePayload = payload ?? Array.Empty<byte>();

        return await Facade.ExecuteWriteAsync(
            registration.RouteKey,
            token => ExecuteWithRoutePolicyAsync(
                registration,
                ct => registration.RouteEntry.ProtocolDriver.WriteAsync(
                    registration.RouteEntry.TransportClient,
                    dataAddress,
                    effectivePayload,
                    ct),
                token),
            cancellationToken).ConfigureAwait(false);
    }

    private async Task<OperationResult<byte[]>> ExecuteWithRoutePolicyAsync(
        RouteRuntimeRegistration registration,
        Func<CancellationToken, Task<OperationResult<byte[]>>> ioAction,
        CancellationToken cancellationToken) {

        if (registration.IsSerialRoute) {
            await WaitSerialWindowAsync(registration, cancellationToken).ConfigureAwait(false);
        }

        try {
            OperationResult<byte[]> result = await ioAction(cancellationToken).ConfigureAwait(false);
            if (result.Success) {
                PublishStatus(registration.RouteId, online: true, KernelErrorCode.None, string.Empty);
                return result;
            }

            // 分支9：失败后按错误类型决定是否尝试一次快速重连。
            if (ShouldAttemptReconnect(result.ErrorCode)) {
                bool reconnected = await TryReconnectAsync(registration, cancellationToken).ConfigureAwait(false);
                if (reconnected) {
                    OperationResult<byte[]> retry = await ioAction(cancellationToken).ConfigureAwait(false);
                    PublishStatus(registration.RouteId, retry.Success, retry.ErrorCode, retry.Success ? string.Empty : retry.ErrorMessage);
                    return retry;
                }
            }

            PublishStatus(registration.RouteId, online: false, result.ErrorCode, result.ErrorMessage);
            return result;
        } catch (Exception ex) {
            PublishStatus(registration.RouteId, online: false, KernelErrorCode.TransportIoError, ex.Message);
            return OperationResult<byte[]>.Fail(ex.Message, KernelErrorCode.TransportIoError);
        } finally {
            if (registration.IsSerialRoute) {
                registration.MarkSerialIoCompleted();
                registration.SerialIoGate.Release();
            }
        }
    }

    private async Task<OperationResult> ExecuteWithRoutePolicyAsync(
        RouteRuntimeRegistration registration,
        Func<CancellationToken, Task<OperationResult>> ioAction,
        CancellationToken cancellationToken) {

        if (registration.IsSerialRoute) {
            await WaitSerialWindowAsync(registration, cancellationToken).ConfigureAwait(false);
        }

        try {
            OperationResult result = await ioAction(cancellationToken).ConfigureAwait(false);
            if (result.Success) {
                PublishStatus(registration.RouteId, online: true, KernelErrorCode.None, string.Empty);
                return result;
            }

            if (ShouldAttemptReconnect(result.ErrorCode)) {
                bool reconnected = await TryReconnectAsync(registration, cancellationToken).ConfigureAwait(false);
                if (reconnected) {
                    OperationResult retry = await ioAction(cancellationToken).ConfigureAwait(false);
                    PublishStatus(registration.RouteId, retry.Success, retry.ErrorCode, retry.Success ? string.Empty : retry.ErrorMessage);
                    return retry;
                }
            }

            PublishStatus(registration.RouteId, online: false, result.ErrorCode, result.ErrorMessage);
            return result;
        } catch (Exception ex) {
            PublishStatus(registration.RouteId, online: false, KernelErrorCode.TransportIoError, ex.Message);
            return OperationResult.Fail(ex.Message, KernelErrorCode.TransportIoError);
        } finally {
            if (registration.IsSerialRoute) {
                registration.MarkSerialIoCompleted();
                registration.SerialIoGate.Release();
            }
        }
    }

    private async Task WaitSerialWindowAsync(RouteRuntimeRegistration registration, CancellationToken cancellationToken) {
        // 串口保护策略：同一路由 I/O 互斥，防止串口读写并发抢占。
        await registration.SerialIoGate.WaitAsync(cancellationToken).ConfigureAwait(false);

        // 串口保护策略：控制最小发送间隔，避免速率过高导致缓冲阻塞。
        int elapsedMs = registration.GetElapsedSinceLastIoMs();
        int delayMs = registration.MinIoIntervalMs - elapsedMs;
        if (delayMs > 0) {
            await Task.Delay(delayMs, cancellationToken).ConfigureAwait(false);
        }
    }

    private static bool ShouldAttemptReconnect(KernelErrorCode errorCode) {
        return errorCode is KernelErrorCode.TransportIoError
            or KernelErrorCode.TransportUnavailable
            or KernelErrorCode.Timeout;
    }

    private async Task<bool> TryReconnectAsync(RouteRuntimeRegistration registration, CancellationToken cancellationToken) {
        try {
            await registration.RouteEntry.TransportClient.DisconnectAsync(cancellationToken).ConfigureAwait(false);
            OperationResult reconnect = await registration.RouteEntry.TransportClient
                .ConnectAsync(registration.Endpoint, cancellationToken)
                .ConfigureAwait(false);

            if (reconnect.Success) {
                PublishStatus(registration.RouteId, online: true, KernelErrorCode.None, string.Empty);
                return true;
            }

            PublishStatus(registration.RouteId, online: false, reconnect.ErrorCode, reconnect.ErrorMessage);
            return false;
        } catch (Exception ex) {
            PublishStatus(registration.RouteId, online: false, KernelErrorCode.TransportIoError, ex.Message);
            return false;
        }
    }

    private void PublishStatus(string routeId, bool online, KernelErrorCode errorCode, string errorMessage) {
        var snapshot = new RouteStatusSnapshot {
            RouteId = routeId,
            Online = online,
            ErrorCode = errorCode,
            ErrorMessage = errorMessage ?? string.Empty,
            TimestampUtc = DateTimeOffset.UtcNow
        };

        _routeStatuses[routeId] = snapshot;
        RouteStatusChanged?.Invoke(snapshot);
    }

    /// <summary>
    /// RegisterRoute 入参模型（服务层与运行时层之间的协议无关命令对象）。
    /// </summary>
    public sealed class RegisterRouteCommand {
        public required string RouteId { get; init; }
        public required string ProtocolId { get; init; }
        public string? TransportId { get; init; }
        public required string TransportKind { get; init; }
        public string? Address { get; init; }
        public int Port { get; init; }
        public string? Station { get; init; }
        public string? SerialPort { get; init; }
        public int BaudRate { get; init; }
        public int MinIoIntervalMs { get; init; }
    }

    /// <summary>
    /// 路由快照模型。
    /// </summary>
    public sealed class RouteRuntimeInfo {
        public required string RouteId { get; init; }
        public required string TransportId { get; init; }
        public required RouteKey RouteKey { get; init; }
        public required TransportEndpoint Endpoint { get; init; }
    }

    /// <summary>
    /// 路由状态快照模型。
    /// </summary>
    public sealed class RouteStatusSnapshot {
        public required string RouteId { get; init; }
        public bool Online { get; init; }
        public KernelErrorCode ErrorCode { get; init; }
        public string ErrorMessage { get; init; } = string.Empty;
        public DateTimeOffset TimestampUtc { get; init; }
    }

    private sealed class RouteRuntimeRegistration {
        private long _lastSerialIoCompletedUtcTicks;

        public RouteRuntimeRegistration(
            string routeId,
            RouteKey routeKey,
            TransportEndpoint endpoint,
            string transportId,
            RouteEntry routeEntry,
            bool isSerialRoute,
            int minIoIntervalMs) {

            RouteId = routeId;
            RouteKey = routeKey;
            Endpoint = endpoint;
            TransportId = transportId;
            RouteEntry = routeEntry;
            IsSerialRoute = isSerialRoute;
            MinIoIntervalMs = Math.Max(0, minIoIntervalMs);
            _lastSerialIoCompletedUtcTicks = DateTimeOffset.MinValue.UtcTicks;
        }

        public string RouteId { get; }
        public RouteKey RouteKey { get; }
        public TransportEndpoint Endpoint { get; }
        public string TransportId { get; }
        public RouteEntry RouteEntry { get; }
        public bool IsSerialRoute { get; }
        public int MinIoIntervalMs { get; }
        public SemaphoreSlim SerialIoGate { get; } = new(1, 1);

        public int GetElapsedSinceLastIoMs() {
            long ticks = Interlocked.Read(ref _lastSerialIoCompletedUtcTicks);
            if (ticks == DateTimeOffset.MinValue.UtcTicks) {
                return int.MaxValue;
            }

            var elapsed = DateTimeOffset.UtcNow - new DateTimeOffset(ticks, TimeSpan.Zero);
            return (int)Math.Max(0, elapsed.TotalMilliseconds);
        }

        public void MarkSerialIoCompleted() {
            Interlocked.Exchange(ref _lastSerialIoCompletedUtcTicks, DateTimeOffset.UtcNow.UtcTicks);
        }
    }
}
