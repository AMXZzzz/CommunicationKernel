using CommunicationKernel.Engine.Models;
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

namespace CommunicationKernel.Engine;

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
/// 3) UnregisterRouteAsync 统一注销入口：摘表后经 Orchestrator 释放 RouteEntry/TransportClient。
/// 4) 读写互斥与串口帧间隔由 RouteEntry.ExecuteExclusiveAsync 承担；此处负责重连与状态发布。
/// -----------------------------------------------------------------------------
/// </summary>
public sealed class HostRuntime : IAsyncDisposable {
    //! 用于存储路由注册信息，键为 RouteId，值为 RouteRuntimeRegistration 对象。
    private readonly ConcurrentDictionary<string, RouteRuntimeRegistration> _registrationsByRouteId = new(StringComparer.OrdinalIgnoreCase);

    //! 用于存储路由状态快照，键为 RouteKey，值为 RouteId。
    private readonly ConcurrentDictionary<RouteKey, string> _routeIdByKey = new();

    //! 用于存储路由状态快照，键为 RouteId，值为 RouteStatusSnapshot。
    private readonly ConcurrentDictionary<string, RouteStatusSnapshot> _routeStatuses = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// RouteId 占位表：值无意义，仅用键表示「已注册或正在注册中」。
    /// </summary>
    /// <remarks>
    /// 装配（含建立真实 TCP/串口连接）是耗时的异步过程。若仅在装配前后各做一次
    /// ContainsKey 检查，两个同 RouteId 的并发注册会双双通过检查，
    /// 后者覆盖前者的登记项，导致前者的 TransportClient 成为无人释放的孤儿。
    /// 因此改为在装配开始前用 TryAdd 原子占位，失败路径在 finally 中释放。
    /// </remarks>
    private readonly ConcurrentDictionary<string, byte> _routeIdReservations = new(StringComparer.OrdinalIgnoreCase);

    //! 依赖注入的路由装配服务：负责根据注册命令组装 RouteEntry 与连接资源。
    private readonly IRouteAssemblyService _routeAssemblyService;

    //! 日志记录器：用于记录 HostRuntime 的运行信息和错误。
    private readonly ILogger<HostRuntime> _logger;

    /// <summary>保护「状态比较 + 写入」的原子性，见 <see cref="PublishStatus"/>。</summary>
    private readonly object _statusPublishLock = new();

    /// <summary>当路由状态变化时触发，用于 gRPC 实时推流。</summary>
    public event Action<RouteStatusSnapshot>? RouteStatusChanged;

    //! 路由编排器：管理路由表与读合并
    private readonly IRouterOrchestrator _orchestrator;

    /// <summary>
    /// 构造 HostRuntime。
    /// </summary>
    /// <param name="routeAssemblyService">路由装配服务，负责组装 RouteEntry 与连接资源。</param>
    /// <param name="orchestrator">路由编排器。</param>
    /// <param name="logger">日志记录器；为 null 时不记录。</param>
    /// <remarks>
    /// 两个依赖均为必填且面向接口——不再在内部 new 具体实现，
    /// 组合根成为唯一知晓具体类型的位置。
    /// </remarks>
    public HostRuntime(
        IRouteAssemblyService routeAssemblyService,
        IRouterOrchestrator orchestrator,
        ILogger<HostRuntime>? logger = null) {

        ArgumentNullException.ThrowIfNull(routeAssemblyService);
        ArgumentNullException.ThrowIfNull(orchestrator);

        _routeAssemblyService = routeAssemblyService;
        _orchestrator         = orchestrator;
        _logger               = logger ?? NullLogger<HostRuntime>.Instance;

        _logger.LogInformation("HostRuntime initialized.");
    }

    /// <summary>当前注册路由数量（供 gRPC Health / Diagnostics 端点使用）。</summary>
    public int RouteCount => _orchestrator.RouteCount;

    /// <summary>
    /// 已占位但尚未完成装配的路由数量。
    /// 持续大于 0 通常意味着某条路由的 ConnectAsync 卡住了。
    /// </summary>
    public int PendingRouteCount => _routeIdReservations.Count - _registrationsByRouteId.Count;

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
            if (!_orchestrator.TryRegister(assembled.RouteEntry)) {
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
    /// 注销路由并释放关联资源（RouteEntry / TransportClient / 协议驱动）。
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

        bool disposed = await _orchestrator.TryRemoveAndDisposeAsync(reg.RouteKey, cancellationToken)
            .ConfigureAwait(false);

        if (disposed)
            _logger.LogInformation("UnregisterRoute: '{RouteId}' removed and disposed.", routeId);
        else
            _logger.LogWarning("UnregisterRoute: '{RouteId}' was not in router (state mismatch).", routeId);

        // 广播终态：先摘除快照再发事件，避免 PublishStatus 把快照又写回去。
        PublishFinalOffline(routeId);

        return OperationResult.Ok;
    }

    /// <summary>
    /// 注销全部路由并关闭所有 PLC 连接。
    /// </summary>
    /// <remarks>
    /// 进程退出时若不执行，TCP 连接只能等对端超时才断开，串口句柄则保持占用，
    /// 重启宿主会因串口被占用而连不上。DI 容器在关闭时会调用本方法。
    /// </remarks>
    public async ValueTask DisposeAsync() {
        string[] routeIds = _registrationsByRouteId.Keys.ToArray();
        if (routeIds.Length == 0) return;

        _logger.LogInformation("HostRuntime disposing: closing {Count} route(s).", routeIds.Length);

        foreach (string routeId in routeIds) {
            try {
                await UnregisterRouteAsync(routeId, CancellationToken.None).ConfigureAwait(false);
            } catch (Exception ex) {
                // 退出阶段最大努力释放：单条路由失败不应阻断其余路由的关闭
                _logger.LogError(ex, "HostRuntime dispose: failed to unregister route '{RouteId}'.", routeId);
            }
        }
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

        RaiseStatusChanged(snapshot);
    }

    /// <summary>
    /// 向订阅方扇出状态事件，逐个隔离异常。
    /// </summary>
    /// <remarks>
    /// 事件在 I/O 线程上同步触发。若不隔离，任意一个订阅方（例如某个断开中的
    /// gRPC 流）抛出异常，都会沿调用栈冒泡回 <see cref="PublishStatus"/> 的调用点，
    /// 把一次<b>成功的读取</b>变成失败——一个观察者的问题不该影响被观察的操作。
    /// </remarks>
    private void RaiseStatusChanged(RouteStatusSnapshot snapshot) {
        Action<RouteStatusSnapshot>? handlers = RouteStatusChanged;
        if (handlers is null) return;

        foreach (Delegate d in handlers.GetInvocationList()) {
            try {
                ((Action<RouteStatusSnapshot>)d)(snapshot);
            } catch (Exception ex) {
                _logger.LogError(ex,
                    "RouteStatusChanged 订阅方抛出异常，已隔离（route={RouteId}）。", snapshot.RouteId);
            }
        }
    }

    /// <summary>通过 route_id 执行读取。</summary>
    public async Task<OperationResult<byte[]>> ReadByRouteIdAsync(
        string routeId, string dataAddress, int length, CancellationToken cancellationToken) {

        if (!_registrationsByRouteId.TryGetValue(routeId, out RouteRuntimeRegistration? registration))
            return OperationResult<byte[]>.Fail("route not found", KernelErrorCode.RouteNotFound);

        if (length <= 0)
            return OperationResult<byte[]>.Fail("length must be greater than 0", KernelErrorCode.InvalidArgument);

        // 先经读合并（同键并发共享一次 I/O），再由路由门控串行化物理访问
        return await _orchestrator.ExecuteReadAsync(
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

        // 写入不做合并，直接进入路由门控——与读共用同一把锁，
        // 保证同一物理连接上读写不会交织
        return await ExecuteWithRoutePolicyAsync(
            registration,
            ct => registration.RouteEntry.ProtocolDriver.WriteAsync(
                registration.RouteEntry.TransportClient, dataAddress, effectivePayload, ct),
            cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// 在路由独占门控下执行一次读取，并附加重连与状态发布策略。
    /// </summary>
    /// <remarks>
    /// 门控由 <see cref="RouteEntry.ExecuteExclusiveAsync{TResult}"/> 承担，覆盖读写两条路径：
    /// 一条路由对应一个物理连接，而 NetworkStream / SerialPort 都不支持并发读。
    /// 最小 I/O 间隔（串口帧间静默）也一并由门控内部补足。
    /// </remarks>
    private Task<OperationResult<byte[]>> ExecuteWithRoutePolicyAsync(
        RouteRuntimeRegistration registration,
        Func<CancellationToken, Task<OperationResult<byte[]>>> ioAction,
        CancellationToken cancellationToken)
        => registration.RouteEntry.ExecuteExclusiveAsync(
            ct => RunReadWithPolicyAsync(registration, ioAction, ct),
            cancellationToken);

    private async Task<OperationResult<byte[]>> RunReadWithPolicyAsync(
        RouteRuntimeRegistration registration,
        Func<CancellationToken, Task<OperationResult<byte[]>>> ioAction,
        CancellationToken cancellationToken) {

        try {
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
        }
    }

    /// <summary>在路由独占门控下执行一次写入，并附加重连与状态发布策略。</summary>
    private Task<OperationResult> ExecuteWithRoutePolicyAsync(
        RouteRuntimeRegistration registration,
        Func<CancellationToken, Task<OperationResult>> ioAction,
        CancellationToken cancellationToken)
        => registration.RouteEntry.ExecuteExclusiveAsync(
            ct => RunWriteWithPolicyAsync(registration, ioAction, ct),
            cancellationToken);

    private async Task<OperationResult> RunWriteWithPolicyAsync(
        RouteRuntimeRegistration registration,
        Func<CancellationToken, Task<OperationResult>> ioAction,
        CancellationToken cancellationToken) {

        try {
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
        }
    }

    /// <remarks>
    /// <see cref="KernelErrorCode.Cancelled"/> 刻意<b>不在</b>重连之列：
    /// 用户主动取消（关页面、停轮询、退出应用）不是链路故障，
    /// 误判会在批量停止轮询时引发几十条路由同时重连。
    /// </remarks>
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

        RaiseStatusChanged(published);
    }

    // ── Inner types ───────────────────────────────────────────────────────────

    // RegisterRouteCommand / RouteRuntimeInfo / RouteStatusSnapshot
    // 已提为顶层类型，见 Engine/Models/。
    // 作为嵌套类型时，实现 IRouteAssemblyService 必须依赖 HostRuntime 这个具体类。


    /// <summary>
    /// Host 侧路由登记项：RouteId 与 Router 层 RouteEntry 的对应关系。
    /// 读写互斥与串口帧间隔在 <see cref="RouteEntry"/> 内实现，此处不再持有第二套信号量。
    /// </summary>
    private sealed class RouteRuntimeRegistration {
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
        }

        public string            RouteId         { get; }
        public RouteKey          RouteKey        { get; }
        public TransportEndpoint Endpoint       { get; }
        public string            TransportId     { get; }
        public RouteEntry        RouteEntry      { get; }
        /// <summary>装配时是否为串口路由（诊断/扩展用；节流已在 RouteEntry）。</summary>
        public bool              IsSerialRoute   { get; }
        /// <summary>装配时采用的最小 I/O 间隔（毫秒）；实际节流在 RouteEntry.MinIoIntervalMs。</summary>
        public int               MinIoIntervalMs { get; }
    }
}
