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
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace CommunicationKernel.EngineHost.Host;

/// <summary>
/// -----------------------------------------------------------------------------
/// 文件: HostRuntime.cs
/// 层级: EngineHost / Host
/// 作用: Host 运行时主控入口，负责路由注册与运行时执行策略。
/// 说明:
/// 1) Facade 属性为 internal：外部（gRPC 服务）应通过 HostRuntime 公开方法
///    访问路由信息，禁止穿透至 Facade/Orchestrator 内部。
/// 2) RegisterRoute 通过装配抽象服务完成 RouteEntry 获取并落表；
///    重复 RouteId / RouteKey 均被拒绝，已建立的连接通过 RollbackAsync 回滚。
/// 3) UnregisterRouteAsync 统一注销入口，确保 WriteScheduler + TransportClient 均被释放。
/// 4) 统一处理串口节流、单次重连、状态发布等企业级运行策略。
/// -----------------------------------------------------------------------------
/// </summary>
public sealed class HostRuntime {
    private readonly ConcurrentDictionary<string, RouteRuntimeRegistration> _registrationsByRouteId =
        new(StringComparer.OrdinalIgnoreCase);

    private readonly ConcurrentDictionary<RouteKey, string> _routeIdByKey = new();
    private readonly ConcurrentDictionary<string, RouteStatusSnapshot> _routeStatuses =
        new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// RouteId 占位表：值无意义，仅用键表示「已注册或正在注册中」。
    /// </summary>
    /// <remarks>
    /// 装配（含建立真实 TCP/串口连接）是耗时的异步过程。若仅在装配前后各做一次
    /// ContainsKey 检查，两个同 RouteId 的并发注册会双双通过检查，
    /// 后者覆盖前者的登记项，导致前者的 TransportClient 成为无人释放的孤儿。
    /// 因此改为在装配开始前用 TryAdd 原子占位，失败路径在 finally 中释放。
    /// </remarks>
    private readonly ConcurrentDictionary<string, byte> _routeIdReservations =
        new(StringComparer.OrdinalIgnoreCase);

    private readonly IRouteAssemblyService _routeAssemblyService;
    private readonly ILogger<HostRuntime> _logger;

    /// <summary>保护「状态比较 + 写入」的原子性，见 <see cref="PublishStatus"/>。</summary>
    private readonly object _statusPublishLock = new();

    /// <summary>当路由状态变化时触发，用于 gRPC 实时推流。</summary>
    public event Action<RouteStatusSnapshot>? RouteStatusChanged;

    public HostRuntime(
        IRouteAssemblyService routeAssemblyService,
        IRouterOrchestrator? orchestrator = null,
        ILogger<HostRuntime>? logger = null,
        ILoggerFactory? loggerFactory = null) {

        ArgumentNullException.ThrowIfNull(routeAssemblyService);

        _logger = logger ?? loggerFactory?.CreateLogger<HostRuntime>() ?? NullLogger<HostRuntime>.Instance;

        IRouterOrchestrator resolvedOrchestrator = orchestrator ?? new RouterOrchestrator(loggerFactory);
        Facade = new EngineHostFacade(resolvedOrchestrator);

        _routeAssemblyService = routeAssemblyService;
        _logger.LogInformation("HostRuntime initialized.");
    }

    // internal: gRPC 服务禁止穿透 Facade 访问内部路由器，只能通过 HostRuntime 公开方法。
    internal EngineHostFacade Facade { get; }

    /// <summary>当前注册路由数量（供 gRPC Health / Diagnostics 端点使用）。</summary>
    public int RouteCount => Facade.Orchestrator.RouteCount;

    /// <summary>当前有效订阅数量（供 gRPC Diagnostics 端点使用）。</summary>
    public int SubscriptionCount => Facade.Orchestrator.SubscriptionCount;

    /// <summary>获取路由快照（用于查询接口）。</summary>
    public IReadOnlyList<RouteRuntimeInfo> SnapshotRoutes() {
        return _registrationsByRouteId.Values
            .Select(r => new RouteRuntimeInfo {
                RouteId     = r.RouteId,
                RouteKey    = r.RouteKey,
                Endpoint    = r.Endpoint,
                TransportId = r.TransportId
            })
            .ToList();
    }

    /// <summary>获取状态快照（用于流式订阅初始化）。</summary>
    public IReadOnlyList<RouteStatusSnapshot> SnapshotStatuses(string? routeId = null) {
        if (string.IsNullOrWhiteSpace(routeId))
            return _routeStatuses.Values.ToList();

        return _routeStatuses.TryGetValue(routeId, out RouteStatusSnapshot? snapshot)
            ? new[] { snapshot }
            : Array.Empty<RouteStatusSnapshot>();
    }

    /// <summary>注册路由并完成插件工厂组装。</summary>
    public async Task<OperationResult<string>> RegisterRouteAsync(
        RegisterRouteCommand command, CancellationToken cancellationToken) {

        ArgumentNullException.ThrowIfNull(command);

        if (string.IsNullOrWhiteSpace(command.RouteId))
            return OperationResult<string>.Fail("route_id is required", KernelErrorCode.InvalidArgument);

        // RouteKey 用 '|' 作分隔符，输入字段中不得包含该字符，否则会破坏键唯一性。
        if (command.ProtocolId?.Contains('|') == true)
            return OperationResult<string>.Fail("protocol_id must not contain '|'", KernelErrorCode.InvalidArgument);
        if (command.Address?.Contains('|') == true)
            return OperationResult<string>.Fail("address must not contain '|'", KernelErrorCode.InvalidArgument);
        if (command.Station?.Contains('|') == true)
            return OperationResult<string>.Fail("station must not contain '|'", KernelErrorCode.InvalidArgument);

        string resolvedRouteId = command.RouteId.Trim();

        // 分支1：原子占位 RouteId。TryAdd 失败即表示已注册或另一请求正在注册中。
        if (!_routeIdReservations.TryAdd(resolvedRouteId, 0)) {
            _logger.LogWarning("RegisterRoute rejected: route_id '{RouteId}' already registered or in progress.", resolvedRouteId);
            return OperationResult<string>.Fail($"route_id already registered: {resolvedRouteId}", KernelErrorCode.RouteBusy);
        }

        // 占位是否最终转为正式登记；未转正时必须在 finally 中释放，否则该 RouteId 被永久占死。
        bool reservationCommitted = false;

        try {
            // 分支2：委托装配服务构建 RouteEntry 与连接状态。
            OperationResult<RouteAssemblyResult> assemble = await _routeAssemblyService
                .AssembleAsync(command, cancellationToken)
                .ConfigureAwait(false);

            if (!assemble.Success) {
                _logger.LogError("RegisterRoute failed to assemble route '{RouteId}': {Error}", resolvedRouteId, assemble.ErrorMessage);
                return OperationResult<string>.Fail(assemble.ErrorMessage, assemble.ErrorCode);
            }

            RouteAssemblyResult assembled = assemble.Value;

            // 分支3：原子占位 RouteKey（协议+地址+站号），拒绝指向同一物理设备的重复路由。
            if (!_routeIdByKey.TryAdd(assembled.RouteKey, resolvedRouteId)) {
                await assembled.RollbackAsync(cancellationToken).ConfigureAwait(false);
                _logger.LogWarning("RegisterRoute rejected: RouteKey '{RouteKey}' already exists.", assembled.RouteKey);
                return OperationResult<string>.Fail($"route already exists: {assembled.RouteKey}", KernelErrorCode.RouteBusy);
            }

            // 分支4：注册到 Router；失败时先归还 RouteKey 占位，再回滚连接资源。
            if (!Facade.TryRegister(assembled.RouteEntry)) {
                _routeIdByKey.TryRemove(assembled.RouteKey, out _);
                await assembled.RollbackAsync(cancellationToken).ConfigureAwait(false);
                _logger.LogWarning("RegisterRoute: router rejected registration for '{RouteId}'.", resolvedRouteId);
                return OperationResult<string>.Fail("router rejected route registration", KernelErrorCode.RouteBusy);
            }

            var registration = new RouteRuntimeRegistration(
                resolvedRouteId,
                assembled.RouteKey,
                assembled.Endpoint,
                assembled.TransportId,
                assembled.RouteEntry,
                assembled.IsSerialRoute,
                assembled.MinIoIntervalMs);

            // 占位已确保此处不存在并发写入同一 RouteId 的可能。
            _registrationsByRouteId[resolvedRouteId] = registration;
            reservationCommitted = true;

            PublishStatus(resolvedRouteId, online: true, KernelErrorCode.None, string.Empty);
            _logger.LogInformation("RegisterRoute succeeded: '{RouteId}' ({RouteKey}).", resolvedRouteId, assembled.RouteKey);
            return OperationResult<string>.Ok(resolvedRouteId);
        } finally {
            // 未转正（失败或异常）：释放占位，允许该 RouteId 重新尝试注册。
            if (!reservationCommitted)
                _routeIdReservations.TryRemove(resolvedRouteId, out _);
        }
    }

    /// <summary>
    /// 注销路由并释放所有关联资源（串口节流信号量 + TransportClient + 协议驱动）。
    /// 完成后广播一次终态下线事件，使所有订阅方感知该路由已消失。
    /// </summary>
    public async Task<OperationResult> UnregisterRouteAsync(string routeId, CancellationToken cancellationToken) {
        if (string.IsNullOrWhiteSpace(routeId))
            return OperationResult.InvalidArgument("route_id is required");

        if (!_registrationsByRouteId.TryRemove(routeId, out RouteRuntimeRegistration? reg))
            return OperationResult.Fail($"route not found: {routeId}", KernelErrorCode.RouteNotFound);

        _routeIdByKey.TryRemove(reg.RouteKey, out _);
        // 释放 RouteId 占位，使同名路由可被重新注册（更新设备参数即依赖此路径）。
        _routeIdReservations.TryRemove(routeId, out _);

        bool disposed = await Facade.Orchestrator.TryRemoveAndDisposeAsync(reg.RouteKey, cancellationToken)
            .ConfigureAwait(false);

        if (disposed)
            _logger.LogInformation("UnregisterRoute: '{RouteId}' removed and disposed.", routeId);
        else
            _logger.LogWarning("UnregisterRoute: '{RouteId}' was not in router (state mismatch).", routeId);

        // 传输资源已释放，此时再释放节流信号量。
        // 并发中的 I/O 若在此之后调用 Release，会得到 ObjectDisposedException，
        // 该异常已在 ExecuteWithRoutePolicyAsync 的 finally 中被吞掉。
        reg.DisposeGate();

        // 广播终态：先摘除快照再发事件，避免 PublishStatus 把快照又写回去。
        PublishFinalOffline(routeId);

        return OperationResult.Ok;
    }

    /// <summary>
    /// 广播路由注销后的终态下线事件，并清除其状态快照。
    /// </summary>
    /// <remarks>
    /// 不复用 <see cref="PublishStatus"/>：后者会把快照写回 <c>_routeStatuses</c>，
    /// 使已注销的路由在 SnapshotStatuses 中留下幽灵条目。
    /// 若不广播此事件，其他客户端的 WatchRouteStatus 流将永远停留在最后已知状态，
    /// 界面上表现为「设备已被别人删除，但本机仍显示在线」。
    /// </remarks>
    private void PublishFinalOffline(string routeId) {
        var snapshot = new RouteStatusSnapshot {
            RouteId      = routeId,
            Online       = false,
            ErrorCode    = KernelErrorCode.RouteNotFound,
            ErrorMessage = "route unregistered",
            TimestampUtc = DateTimeOffset.UtcNow
        };

        lock (_statusPublishLock) {
            _routeStatuses.TryRemove(routeId, out _);
        }

        RouteStatusChanged?.Invoke(snapshot);
    }

    /// <summary>通过 route_id 执行读取。</summary>
    public async Task<OperationResult<byte[]>> ReadByRouteIdAsync(
        string routeId, string dataAddress, int length, CancellationToken cancellationToken) {

        if (!_registrationsByRouteId.TryGetValue(routeId, out RouteRuntimeRegistration? registration))
            return OperationResult<byte[]>.Fail("route not found", KernelErrorCode.RouteNotFound);

        if (length <= 0)
            return OperationResult<byte[]>.Fail("length must be greater than 0", KernelErrorCode.InvalidArgument);

        return await Facade.ExecuteReadAsync(
            new ReadRequestKey(registration.RouteKey, dataAddress, length),
            token => ExecuteWithRoutePolicyAsync(
                registration,
                ct => registration.RouteEntry.ProtocolDriver.ReadAsync(
                    registration.RouteEntry.TransportClient, dataAddress, length, ct),
                token),
            cancellationToken).ConfigureAwait(false);
    }

    /// <summary>通过 route_id 执行写入。</summary>
    public async Task<OperationResult> WriteByRouteIdAsync(
        string routeId, string dataAddress, byte[] payload, CancellationToken cancellationToken) {

        if (!_registrationsByRouteId.TryGetValue(routeId, out RouteRuntimeRegistration? registration))
            return OperationResult.Fail("route not found", KernelErrorCode.RouteNotFound);

        byte[] effectivePayload = payload ?? Array.Empty<byte>();

        return await Facade.ExecuteWriteAsync(
            registration.RouteKey,
            token => ExecuteWithRoutePolicyAsync(
                registration,
                ct => registration.RouteEntry.ProtocolDriver.WriteAsync(
                    registration.RouteEntry.TransportClient, dataAddress, effectivePayload, ct),
                token),
            cancellationToken).ConfigureAwait(false);
    }

    private async Task<OperationResult<byte[]>> ExecuteWithRoutePolicyAsync(
        RouteRuntimeRegistration registration,
        Func<CancellationToken, Task<OperationResult<byte[]>>> ioAction,
        CancellationToken cancellationToken) {

        // 仅当 WaitSerialWindowAsync 正常返回才算持有信号量；
        // 取消路径下它已自行归还，此处不得重复 Release。
        bool gateAcquired = false;

        try {
            if (registration.IsSerialRoute) {
                await WaitSerialWindowAsync(registration, cancellationToken).ConfigureAwait(false);
                gateAcquired = true;
            }

            OperationResult<byte[]> result = await ioAction(cancellationToken).ConfigureAwait(false);
            if (result.Success) {
                PublishStatus(registration.RouteId, online: true, KernelErrorCode.None, string.Empty);
                return result;
            }

            if (ShouldAttemptReconnect(result.ErrorCode)) {
                _logger.LogWarning("Route '{RouteId}': IO error {Code}, attempting reconnect.", registration.RouteId, result.ErrorCode);
                bool reconnected = await TryReconnectAsync(registration, cancellationToken).ConfigureAwait(false);
                if (reconnected) {
                    OperationResult<byte[]> retry = await ioAction(cancellationToken).ConfigureAwait(false);
                    PublishStatus(registration.RouteId, retry.Success, retry.ErrorCode, retry.Success ? string.Empty : retry.ErrorMessage);
                    if (!retry.Success)
                        _logger.LogError("Route '{RouteId}': retry after reconnect also failed: {Error}", registration.RouteId, retry.ErrorMessage);
                    return retry;
                }
            }

            PublishStatus(registration.RouteId, online: false, result.ErrorCode, result.ErrorMessage);
            _logger.LogWarning("Route '{RouteId}': IO failed: {Code} {Error}", registration.RouteId, result.ErrorCode, result.ErrorMessage);
            return result;
        } catch (Exception ex) {
            _logger.LogError(ex, "Route '{RouteId}': unhandled exception in IO action.", registration.RouteId);
            PublishStatus(registration.RouteId, online: false, KernelErrorCode.TransportIoError, ex.Message);
            return OperationResult<byte[]>.Fail(ex.Message, KernelErrorCode.TransportIoError);
        } finally {
            if (gateAcquired) {
                registration.MarkSerialIoCompleted();
                try {
                    registration.SerialIoGate.Release();
                } catch (ObjectDisposedException) {
                    // 路由在本次 I/O 期间被并发注销，信号量已释放：无需归还。
                }
            }
        }
    }

    private async Task<OperationResult> ExecuteWithRoutePolicyAsync(
        RouteRuntimeRegistration registration,
        Func<CancellationToken, Task<OperationResult>> ioAction,
        CancellationToken cancellationToken) {

        // 同读路径：仅在确实持有信号量时才在 finally 中释放。
        bool gateAcquired = false;

        try {
            if (registration.IsSerialRoute) {
                await WaitSerialWindowAsync(registration, cancellationToken).ConfigureAwait(false);
                gateAcquired = true;
            }

            OperationResult result = await ioAction(cancellationToken).ConfigureAwait(false);
            if (result.Success) {
                PublishStatus(registration.RouteId, online: true, KernelErrorCode.None, string.Empty);
                return result;
            }

            if (ShouldAttemptReconnect(result.ErrorCode)) {
                _logger.LogWarning("Route '{RouteId}': write IO error {Code}, attempting reconnect.", registration.RouteId, result.ErrorCode);
                bool reconnected = await TryReconnectAsync(registration, cancellationToken).ConfigureAwait(false);
                if (reconnected) {
                    OperationResult retry = await ioAction(cancellationToken).ConfigureAwait(false);
                    PublishStatus(registration.RouteId, retry.Success, retry.ErrorCode, retry.Success ? string.Empty : retry.ErrorMessage);
                    if (!retry.Success)
                        _logger.LogError("Route '{RouteId}': write retry after reconnect failed: {Error}", registration.RouteId, retry.ErrorMessage);
                    return retry;
                }
            }

            PublishStatus(registration.RouteId, online: false, result.ErrorCode, result.ErrorMessage);
            _logger.LogWarning("Route '{RouteId}': write failed: {Code} {Error}", registration.RouteId, result.ErrorCode, result.ErrorMessage);
            return result;
        } catch (Exception ex) {
            _logger.LogError(ex, "Route '{RouteId}': unhandled exception in write IO action.", registration.RouteId);
            PublishStatus(registration.RouteId, online: false, KernelErrorCode.TransportIoError, ex.Message);
            return OperationResult.Fail(ex.Message, KernelErrorCode.TransportIoError);
        } finally {
            if (gateAcquired) {
                registration.MarkSerialIoCompleted();
                try {
                    registration.SerialIoGate.Release();
                } catch (ObjectDisposedException) {
                    // 路由在本次 I/O 期间被并发注销，信号量已释放：无需归还。
                }
            }
        }
    }

    /// <summary>
    /// 获取串口 I/O 窗口：先取信号量，再补足最小 I/O 间隔。
    /// 正常返回即表示调用方持有信号量，必须由调用方在 finally 中释放。
    /// </summary>
    /// <remarks>
    /// 节流等待期间若被取消，本方法负责归还已获取的信号量再抛出——
    /// 否则该串口路由的信号量将永久耗尽，后续所有读写无限期挂起。
    /// </remarks>
    private static async Task WaitSerialWindowAsync(
        RouteRuntimeRegistration registration, CancellationToken cancellationToken) {

        await registration.SerialIoGate.WaitAsync(cancellationToken).ConfigureAwait(false);

        try {
            int elapsedMs = registration.GetElapsedSinceLastIoMs();
            int delayMs = registration.MinIoIntervalMs - elapsedMs;
            if (delayMs > 0)
                await Task.Delay(delayMs, cancellationToken).ConfigureAwait(false);
        } catch {
            // 已持有信号量但未能进入 I/O：必须归还，否则路由死锁。
            try {
                registration.SerialIoGate.Release();
            } catch (ObjectDisposedException) {
                // 路由已被并发注销，信号量随之释放：无需归还。
            }
            throw;
        }
    }

    private static bool ShouldAttemptReconnect(KernelErrorCode errorCode) =>
        errorCode is KernelErrorCode.TransportIoError
            or KernelErrorCode.TransportUnavailable
            or KernelErrorCode.Timeout;

    private async Task<bool> TryReconnectAsync(RouteRuntimeRegistration registration, CancellationToken cancellationToken) {
        try {
            await registration.RouteEntry.TransportClient.DisconnectAsync(cancellationToken).ConfigureAwait(false);
            OperationResult reconnect = await registration.RouteEntry.TransportClient
                .ConnectAsync(registration.Endpoint, cancellationToken)
                .ConfigureAwait(false);

            if (reconnect.Success) {
                _logger.LogInformation("Route '{RouteId}': reconnected successfully.", registration.RouteId);
                PublishStatus(registration.RouteId, online: true, KernelErrorCode.None, string.Empty);
                return true;
            }

            _logger.LogWarning("Route '{RouteId}': reconnect failed: {Error}", registration.RouteId, reconnect.ErrorMessage);
            PublishStatus(registration.RouteId, online: false, reconnect.ErrorCode, reconnect.ErrorMessage);
            return false;
        } catch (Exception ex) {
            _logger.LogError(ex, "Route '{RouteId}': reconnect threw exception.", registration.RouteId);
            PublishStatus(registration.RouteId, online: false, KernelErrorCode.TransportIoError, ex.Message);
            return false;
        }
    }

    /// <summary>
    /// 发布路由状态。仅在状态实际发生变化时才写入快照并广播事件。
    /// </summary>
    /// <remarks>
    /// 每次成功 I/O 都会调用本方法。若无变化检测，轮询场景下
    /// （N 个变量 × 每秒若干次）会产生等量的"状态变化"事件推给所有订阅客户端，
    /// 淹没 UI 线程且分配大量无意义快照对象。
    /// 时间戳语义因此是「状态变为当前值的时刻」，而非「最近一次 I/O 时刻」。
    /// </remarks>
    private void PublishStatus(string routeId, bool online, KernelErrorCode errorCode, string errorMessage) {
        string message = errorMessage ?? string.Empty;
        RouteStatusSnapshot? published = null;

        // 比较+写入需原子完成，否则并发 I/O 可能都判定为"未变化"而丢失事件。
        lock (_statusPublishLock) {
            if (_routeStatuses.TryGetValue(routeId, out RouteStatusSnapshot? prev)
                && prev.Online == online
                && prev.ErrorCode == errorCode
                && string.Equals(prev.ErrorMessage, message, StringComparison.Ordinal)) {
                // 状态未变化：保留原快照（含其原始时间戳），不广播。
                return;
            }

            published = new RouteStatusSnapshot {
                RouteId      = routeId,
                Online       = online,
                ErrorCode    = errorCode,
                ErrorMessage = message,
                TimestampUtc = DateTimeOffset.UtcNow
            };
            _routeStatuses[routeId] = published;
        }

        // 在锁外触发，避免订阅方回调阻塞其他路由的状态发布。
        RouteStatusChanged?.Invoke(published);
    }

    // ── Inner types ───────────────────────────────────────────────────────────

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

    public sealed class RouteRuntimeInfo {
        public required string RouteId { get; init; }
        public required string TransportId { get; init; }
        public required RouteKey RouteKey { get; init; }
        public required TransportEndpoint Endpoint { get; init; }
    }

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
            string routeId, RouteKey routeKey, TransportEndpoint endpoint,
            string transportId, RouteEntry routeEntry, bool isSerialRoute, int minIoIntervalMs) {

            RouteId          = routeId;
            RouteKey         = routeKey;
            Endpoint         = endpoint;
            TransportId      = transportId;
            RouteEntry       = routeEntry;
            IsSerialRoute    = isSerialRoute;
            MinIoIntervalMs  = Math.Max(0, minIoIntervalMs);
            _lastSerialIoCompletedUtcTicks = DateTimeOffset.MinValue.UtcTicks;
        }

        public string           RouteId         { get; }
        public RouteKey         RouteKey        { get; }
        public TransportEndpoint Endpoint       { get; }
        public string           TransportId     { get; }
        public RouteEntry       RouteEntry      { get; }
        public bool             IsSerialRoute   { get; }
        public int              MinIoIntervalMs { get; }
        public SemaphoreSlim    SerialIoGate    { get; } = new(1, 1);

        public int GetElapsedSinceLastIoMs() {
            long ticks = Interlocked.Read(ref _lastSerialIoCompletedUtcTicks);
            if (ticks == DateTimeOffset.MinValue.UtcTicks) return int.MaxValue;
            var elapsed = DateTimeOffset.UtcNow - new DateTimeOffset(ticks, TimeSpan.Zero);
            return (int)Math.Max(0, elapsed.TotalMilliseconds);
        }

        public void MarkSerialIoCompleted() =>
            Interlocked.Exchange(ref _lastSerialIoCompletedUtcTicks, DateTimeOffset.UtcNow.UtcTicks);

        /// <summary>
        /// 释放串口节流信号量。仅在路由注销、传输资源已释放后调用。
        /// </summary>
        public void DisposeGate() {
            try {
                SerialIoGate.Dispose();
            } catch (Exception) {
                // 释放阶段的异常不影响注销流程的其余步骤。
            }
        }
    }
}
