// -----------------------------------------------------------------------------
// 文件: EngineRuntime.cs
// 层级: Engine.Runtime
// 作用: 通讯内核入口，负责路由注册、读写策略、重连与状态发布。
// -----------------------------------------------------------------------------

using CommunicationKernel.Engine.Runtime.Models;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using CommunicationKernel.Communication.Protocol.Abstractions;
using CommunicationKernel.Communication.Transport.Abstractions;
using CommunicationKernel.Core.Abstractions.Errors;
using CommunicationKernel.Core.Abstractions.Results;
using CommunicationKernel.Engine.Router;
using CommunicationKernel.Engine.Router.Abstractions;
using CommunicationKernel.Engine.Router.Models;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace CommunicationKernel.Engine.Runtime;

/// <summary>
/// -----------------------------------------------------------------------------
/// 文件: EngineRuntime.cs
/// 层级: Engine.Runtime
/// 作用: 通讯内核入口，负责路由注册与运行时执行策略。
/// 说明:
/// 1) Facade 属性为 internal：外部（gRPC 服务）应通过 EngineRuntime 公开方法
///    访问路由信息，禁止穿透至 Facade/Orchestrator 内部。
/// 2) RegisterRoute 通过装配抽象服务完成 RouteEntry 获取并落表；
///    重复 RouteId / RouteKey 均被拒绝，已建立的连接通过 RollbackAsync 回滚。
/// 3) UnregisterRouteAsync 统一注销入口：摘表后经 Orchestrator 释放 RouteEntry/TransportClient。
/// 4) 读写互斥与串口帧间隔由 RouteEntry.ExecuteExclusiveAsync 承担；此处负责重连与状态发布。
/// -----------------------------------------------------------------------------
/// </summary>
public sealed class EngineRuntime : IAsyncDisposable {
    // RouteId → 运行时登记项：持有 RouteEntry、端点、传输标识，读写都经此定位物理连接
    private readonly ConcurrentDictionary<string, RouteRuntimeRegistration> _registrationsByRouteId = new(StringComparer.OrdinalIgnoreCase);

    // RouteKey → RouteId：同一物理设备（协议+介质+地址+站号）只允许一条路由
    private readonly ConcurrentDictionary<RouteKey, string> _routeIdByKey = new();

    // RouteId → 最新连接状态快照，供 WatchRouteStatus 流初始化与变化检测
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

    // 装配服务：选协议/传输工厂、建链、造驱动；失败时由 RollbackAsync 释放已建连接
    private readonly IRouteAssemblyService _routeAssemblyService;

    // 运行日志；未注入时退化为 NullLogger，避免测试与嵌入场景强制依赖日志组件
    private readonly ILogger<EngineRuntime> _logger;

    /// <summary>保护「状态比较 + 写入」的原子性，见 <see cref="PublishStatus"/>。</summary>
    private readonly object _statusPublishLock = new();

    /// <summary>当路由状态变化时触发，用于 gRPC 实时推流。</summary>
    public event Action<RouteStatusSnapshot>? RouteStatusChanged;

    // 路由编排器：管理路由表与读合并，注销时保证「先摘表、再释放」
    private readonly IRouterOrchestrator _orchestrator;

    /// <summary>
    /// 本机只允许一份 EngineRuntime（跨进程）。
    /// 两份会争用同一条串口/同一台 PLC 的 TCP。
    /// </summary>
    internal const string ProcessMutexName = @"Local\CommunicationKernel.Engine.Runtime";

    private static readonly object ProcessMutexGate = new();
    private static Mutex? ProcessMutex;
    private static int ProcessInstanceCount;
    private readonly bool _holdsProcessMutex;

    // ============================================================================
    // 构造
    // ============================================================================

    /// <summary>
    /// 构造 EngineRuntime。
    /// </summary>
    /// <param name="routeAssemblyService">路由装配服务，负责组装 RouteEntry 与连接资源。</param>
    /// <param name="orchestrator">路由编排器。</param>
    /// <param name="logger">日志记录器；为 null 时不记录。</param>
    /// <param name="linkCheckIntervalMs">
    /// 链路巡检间隔（毫秒），默认 5 秒。
    /// <c>&lt;= 0</c> 表示关闭巡检——注册后没人读写的路由将一直停留在最后已知状态，
    /// 只有确定「状态完全由读写驱动」的场景（如单元测试）才应该关。
    /// </param>
    /// <remarks>
    /// 两个依赖均为必填且面向接口——不再在内部 new 具体实现，
    /// 组合根成为唯一知晓具体类型的位置。
    /// </remarks>
    public EngineRuntime(
        IRouteAssemblyService routeAssemblyService,
        IRouterOrchestrator orchestrator,
        ILogger<EngineRuntime>? logger = null,
        int linkCheckIntervalMs = DefaultLinkCheckIntervalMs) {

        // 装配服务缺失则无法建链；编排器缺失则无法落表——二者均为内核必填依赖
        ArgumentNullException.ThrowIfNull(routeAssemblyService);
        ArgumentNullException.ThrowIfNull(orchestrator);

        _routeAssemblyService = routeAssemblyService;
        _orchestrator         = orchestrator;
        _logger               = logger ?? NullLogger<EngineRuntime>.Instance;

        // 本机只能有一份引擎。测试宿主（testhost）放行，以便套件并行 new 多个。
        _holdsProcessMutex = TryEnterProcess();

        // 间隔 <= 0 表示关闭链路巡检（单元测试与「只按需读写」的场景用）
        if (linkCheckIntervalMs > 0) {
            _linkCheckCts = new CancellationTokenSource();
            _linkCheckTask = Task.Run(() => LinkCheckLoopAsync(linkCheckIntervalMs, _linkCheckCts.Token));
        }

        _logger.LogInformation("EngineRuntime initialized.");
    }

    // ========================================================================
    // 链路巡检
    // ========================================================================

    /// <summary>链路巡检默认间隔（毫秒）。</summary>
    private const int DefaultLinkCheckIntervalMs = 5_000;

    private readonly CancellationTokenSource? _linkCheckCts;
    private readonly Task? _linkCheckTask;

    /// <summary>
    /// 周期性检查各路由的介质连接是否还活着，状态变化立即广播。
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>为什么必须有。</b>此前路由状态只在「注册成功」与「每次读写」时更新。
    /// 一条注册后没人读写的路由，即使 PLC 早已断电，界面也会一直显示在线。
    /// 实测过：杀掉从站三分钟，卡片仍是绿的。
    /// 显示「在线」而实际断开，比显示离线危险得多——操作员据此以为数据是新的。
    /// </para>
    /// <para>
    /// <b>只查不发。</b>巡检调用 <see cref="ITransportClient.IsConnectionAlive"/>，
    /// 那是介质层的廉价探测（查套接字/端口句柄），不产生任何协议报文，
    /// 因此不占用路由的 I/O 门，也不会干扰正在进行的读写或串口帧间静默。
    /// 套接字仍活着、但本周期没人读写时，再发一帧协议心跳（<see cref="IProtocolDriver.ProbeAsync"/>），
    /// 避免从站/模拟器按空闲超时拆掉 TCP，也让「只降不升」的状态能自己探回来。
    /// </para>
    /// <para>
    /// <b>套接字活着不够。</b>半开连接、PLC 死机都可能维持着 TCP。
    /// 所以「Poll 说还连着」不能单独把状态从离线改回在线；
    /// 必须有一次真正的协议心跳或读写成功才标绿。
    /// </para>
    /// </remarks>
    private async Task LinkCheckLoopAsync(int intervalMs, CancellationToken ct) {
        while (!ct.IsCancellationRequested) {
            try {
                await Task.Delay(intervalMs, ct).ConfigureAwait(false);
            } catch (OperationCanceledException) {
                return;
            }

            foreach (RouteRuntimeRegistration registration in _registrationsByRouteId.Values) {
                if (ct.IsCancellationRequested) return;

                try {
                    await InspectRouteLinkAsync(registration, intervalMs, ct).ConfigureAwait(false);
                } catch (OperationCanceledException) when (ct.IsCancellationRequested) {
                    return;
                } catch (Exception ex) {
                    // 单条路由探测失败不能中断整轮巡检
                    _logger.LogWarning(ex,
                        "链路巡检：检查路由 '{RouteId}' 时异常。", registration.RouteId);
                }
            }
        }
    }

    /// <summary>
    /// 检查一条路由：套接字死了就重连；闲置则发协议心跳。
    /// </summary>
    private async Task InspectRouteLinkAsync(
        RouteRuntimeRegistration registration, int intervalMs, CancellationToken ct)
    {
        ITransportClient transport = registration.RouteEntry.TransportClient;

        if (!transport.IsConnectionAlive)
        {
            await RecoverDeadLinkAsync(registration, ct).ConfigureAwait(false);
            return;
        }

        // 本周期已有读写（含上一轮心跳）：不必再打一枪
        if (registration.RouteEntry.HasCompletedIoWithin(intervalMs))
            return;

        await ProbeIdleRouteAsync(registration, ct).ConfigureAwait(false);
    }

    /// <summary>套接字已死：在独占门里重连；连不上或连上仍死则标离线。</summary>
    private async Task RecoverDeadLinkAsync(
        RouteRuntimeRegistration registration, CancellationToken ct)
    {
        if (_routeStatuses.TryGetValue(registration.RouteId, out RouteStatusSnapshot? prev)
            && !prev.Online)
        {
            // 已离线则仍尝试重连，但不再刷同一条离线事件
        }
        else
        {
            _logger.LogWarning(
                "链路巡检：路由 '{RouteId}' 的连接已断开（{Endpoint}），尝试重连。",
                registration.RouteId, registration.Endpoint);
        }

        bool reconnected = await registration.RouteEntry.ExecuteExclusiveAsync(
            inner => TryReconnectAsync(registration, inner),
            ct).ConfigureAwait(false);

        if (!reconnected || !registration.RouteEntry.TransportClient.IsConnectionAlive)
        {
            PublishStatus(
                registration.RouteId,
                online: false,
                KernelErrorCode.TransportIoError,
                "链路巡检发现连接已断开");
        }
    }

    /// <summary>闲置路由发一帧协议心跳；失败则重连再探一次。</summary>
    private async Task ProbeIdleRouteAsync(
        RouteRuntimeRegistration registration, CancellationToken ct)
    {
        await registration.RouteEntry.ExecuteExclusiveAsync(async inner =>
        {
            IProtocolDriver driver = registration.RouteEntry.ProtocolDriver;
            ITransportClient transport = registration.RouteEntry.TransportClient;

            OperationResult probe = await driver.ProbeAsync(transport, inner).ConfigureAwait(false);
            if (probe.Success)
            {
                PublishStatus(registration.RouteId, online: true, KernelErrorCode.None, string.Empty);
                return true;
            }

            if (!ShouldAttemptReconnect(probe.ErrorCode))
            {
                PublishStatus(registration.RouteId, online: false, probe.ErrorCode, probe.ErrorMessage);
                return false;
            }

            _logger.LogWarning(
                "链路心跳：路由 '{RouteId}' 探活失败（{Error}），尝试重连。",
                registration.RouteId, probe.ErrorMessage);

            bool reconnected = await TryReconnectAsync(registration, inner).ConfigureAwait(false);
            if (!reconnected)
                return false;

            OperationResult again = await driver.ProbeAsync(transport, inner).ConfigureAwait(false);
            if (again.Success)
            {
                PublishStatus(registration.RouteId, online: true, KernelErrorCode.None, string.Empty);
                return true;
            }

            PublishStatus(
                registration.RouteId,
                online: false,
                again.ErrorCode,
                again.ErrorMessage);
            return false;
        }, ct).ConfigureAwait(false);
    }

    /// <summary>当前注册路由数量（供 gRPC Health / Diagnostics 端点使用）。</summary>
    public int RouteCount => _orchestrator.RouteCount;

    /// <summary>
    /// 已占位但尚未完成装配的路由数量。
    /// 持续大于 0 通常意味着某条路由的 ConnectAsync 卡住了。
    /// </summary>
    public int PendingRouteCount => _routeIdReservations.Count - _registrationsByRouteId.Count;

    // ============================================================================
    // 查询快照
    // ============================================================================

    /// <summary>获取路由快照（用于查询接口）。</summary>
    public IReadOnlyList<RouteRuntimeInfo> SnapshotRoutes() {
        // 只投影元数据，不把持有 socket/串口的 RouteEntry 暴露给 gRPC/UI
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
        // 未指定 RouteId：返回全部路由当前状态，供 WatchRouteStatus 首包填充
        if (string.IsNullOrWhiteSpace(routeId))
            return _routeStatuses.Values.ToList();

        // 指定 RouteId：只回该路由；未登记过则空数组（不是错误，避免订阅方误判）
        return _routeStatuses.TryGetValue(routeId, out RouteStatusSnapshot? snapshot)
            ? new[] { snapshot }
            : Array.Empty<RouteStatusSnapshot>();
    }

    // ============================================================================
    // 注册 / 注销
    // ============================================================================

    /// <summary>注册路由并完成插件工厂组装。</summary>
    public async Task<OperationResult<string>> RegisterRouteAsync(
        RegisterRouteCommand command, CancellationToken cancellationToken) {

        // 命令体缺失无法解析协议/地址，拒绝空引用进入装配
        ArgumentNullException.ThrowIfNull(command);

        // RouteId 是后续读写的句柄，缺失则无法在登记表中落位
        if (string.IsNullOrWhiteSpace(command.RouteId))
            return OperationResult<string>.Fail("route_id is required", KernelErrorCode.InvalidArgument);

        // 拒绝字段中的 '|'。
        //
        // 注意这与键的唯一性无关：RouteKey 是 record struct，相等性逐字段比较，
        // 无论字段内容含什么字符都不会撞键。此处纯粹是为了诊断可读性——
        // RouteKey.ToString() 以 '|' 分隔字段输出到日志，字段内混入分隔符会让
        // 日志无法断字段边界，故在入口一次性挡掉。
        // 已支持的五种协议其地址形态（40001 / DB1.DBW0 / DT100 等）均不含该字符。
        if (command.ProtocolId?.Contains('|') == true)
            return OperationResult<string>.Fail("protocol_id must not contain '|'", KernelErrorCode.InvalidArgument);
        if (command.Address?.Contains('|') == true)
            return OperationResult<string>.Fail("address must not contain '|'", KernelErrorCode.InvalidArgument);
        if (command.Station?.Contains('|') == true)
            return OperationResult<string>.Fail("station must not contain '|'", KernelErrorCode.InvalidArgument);

        // 去掉首尾空白，避免 "PLC1" 与 "PLC1 " 被当成两条不同路由
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

            // 选工厂/建链/造驱动任一失败：保留错误码原样返回，不把半成品写入路由表
            if (!assemble.Success) {
                _logger.LogError("RegisterRoute failed to assemble route '{RouteId}': {Error}", resolvedRouteId, assemble.ErrorMessage);
                return OperationResult<string>.Fail(assemble.ErrorMessage, assemble.ErrorCode);
            }

            RouteAssemblyResult assembled = assemble.Value;

            // 分支3：原子占位 RouteKey（协议+地址+站号），拒绝指向同一物理设备的重复路由。
            if (!_routeIdByKey.TryAdd(assembled.RouteKey, resolvedRouteId)) {
                // 物理连接已建好但键冲突：必须回滚，否则 socket/串口句柄无人释放
                await assembled.RollbackAsync(cancellationToken).ConfigureAwait(false);
                _logger.LogWarning("RegisterRoute rejected: RouteKey '{RouteKey}' already exists.", assembled.RouteKey);
                return OperationResult<string>.Fail($"route already exists: {assembled.RouteKey}", KernelErrorCode.RouteBusy);
            }

            // 分支4：注册到 Router；失败时先归还 RouteKey 占位，再回滚连接资源。
            if (!_orchestrator.TryRegister(assembled.RouteEntry)) {
                // 编排器拒绝（通常是路由表里已有同一 RouteKey）：先还键，再断链
                _routeIdByKey.TryRemove(assembled.RouteKey, out _);
                await assembled.RollbackAsync(cancellationToken).ConfigureAwait(false);
                _logger.LogWarning("RegisterRoute: router rejected registration for '{RouteId}'.", resolvedRouteId);
                return OperationResult<string>.Fail("router rejected route registration", KernelErrorCode.RouteBusy);
            }

            // 把 RouteId 与 RouteEntry / 端点绑定，后续读写经此定位物理连接
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

            // 首次登记视为上线，推给 WatchRouteStatus 订阅方（UI 设备列表立即变绿）
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
        // RouteId 是注销句柄，空值无法定位登记项
        if (string.IsNullOrWhiteSpace(routeId))
            return OperationResult.InvalidArgument("route_id is required");

        // 从 Host 侧登记表摘除；未命中说明该路由从未注册或已被别人注销
        if (!_registrationsByRouteId.TryRemove(routeId, out RouteRuntimeRegistration? reg))
            return OperationResult.Fail($"route not found: {routeId}", KernelErrorCode.RouteNotFound);

        // 归还 RouteKey 占位，使同一物理设备可被重新登记
        _routeIdByKey.TryRemove(reg.RouteKey, out _);
        // 释放 RouteId 占位，使同名路由可被重新注册（更新设备参数即依赖此路径）。
        _routeIdReservations.TryRemove(routeId, out _);

        // 编排器「先摘表、再 Dispose」：避免并发读写拿到已关闭的 socket/串口
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
        try
        {
        // 先停巡检：否则它可能在路由注销途中读到半拆状态的 RouteEntry
        if (_linkCheckCts is not null) {
            await _linkCheckCts.CancelAsync().ConfigureAwait(false);
            if (_linkCheckTask is not null) {
                try {
                    await _linkCheckTask.ConfigureAwait(false);
                } catch (OperationCanceledException) {
                    // 正常收尾
                }
            }
            _linkCheckCts.Dispose();
        }

        // 先复制键列表：Unregister 会改字典，直接遍历 Keys 会抛 InvalidOperationException
        string[] routeIds = _registrationsByRouteId.Keys.ToArray();
        if (routeIds.Length == 0)
            return;

        _logger.LogInformation("EngineRuntime disposing: closing {Count} route(s).", routeIds.Length);

        foreach (string routeId in routeIds) {
            try {
                // 逐条走统一注销入口，保证摘表、释放、终态广播语义一致
                await UnregisterRouteAsync(routeId, CancellationToken.None).ConfigureAwait(false);
            } catch (Exception ex) {
                // 退出阶段最大努力释放：单条路由失败不应阻断其余路由的关闭
                _logger.LogError(ex, "EngineRuntime dispose: failed to unregister route '{RouteId}'.", routeId);
            }
        }
        }
        finally
        {
            LeaveProcess();
        }
    }

    /// <summary>本机已有引擎时抛错；测试进程放行。</summary>
    private static bool TryEnterProcess()
    {
        if (IsTestHost())
            return false;

        lock (ProcessMutexGate)
        {
            if (ProcessInstanceCount > 0)
                throw new InvalidOperationException(
                    "当前进程已经有一份 EngineRuntime。通讯内核在同一进程里也只能构造一次。");

            Mutex mutex;
            try
            {
                mutex = new Mutex(initiallyOwned: true, ProcessMutexName, out bool createdNew);
                if (!createdNew)
                {
                    mutex.Dispose();
                    throw new InvalidOperationException(
                        "本机已经有一份 Engine.Runtime 在运行（EngineHost.App 或 WebMaster）。\n" +
                        "同时只能有一份引擎，否则会争用同一条 PLC 连接。\n" +
                        "请先退出正在跑的那一个，再启动本程序。");
                }
            }
            catch (AbandonedMutexException)
            {
                // 上一份崩溃后互斥量被遗弃。再 Open 一次拿到同一把锁即可。
                mutex = Mutex.OpenExisting(ProcessMutexName);
            }

            ProcessMutex = mutex;
            ProcessInstanceCount = 1;
            return true;
        }
    }

    private void LeaveProcess()
    {
        if (!_holdsProcessMutex)
            return;

        lock (ProcessMutexGate)
        {
            ProcessInstanceCount = Math.Max(0, ProcessInstanceCount - 1);
            if (ProcessInstanceCount == 0 && ProcessMutex is not null)
            {
                try { ProcessMutex.Dispose(); }
                catch { }
                ProcessMutex = null;
            }
        }
    }

    /// <summary>单元测试跑在 testhost 里，允许同一进程构造多份。</summary>
    private static bool IsTestHost()
    {
        string? name = Assembly.GetEntryAssembly()?.GetName().Name;
        if (string.IsNullOrEmpty(name))
            return false;
        return name.Contains("testhost", StringComparison.OrdinalIgnoreCase)
            || name.Contains(".Tests", StringComparison.OrdinalIgnoreCase);
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
        // 终态快照：Online=false + RouteNotFound，让所有订阅 UI 立刻把该设备标为已删除
        var snapshot = new RouteStatusSnapshot {
            RouteId      = routeId,
            Online       = false,
            ErrorCode    = KernelErrorCode.RouteNotFound,
            ErrorMessage = "route unregistered",
            TimestampUtc = DateTimeOffset.UtcNow
        };

        // 与 PublishStatus 共用同一把锁，避免并发 I/O 把幽灵快照写回去
        lock (_statusPublishLock) {
            _routeStatuses.TryRemove(routeId, out _);
        }

        // 扇出给 gRPC 流；订阅方异常在 RaiseStatusChanged 内隔离
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
        // 复制委托快照，避免遍历过程中有人取消订阅导致集合变化
        Action<RouteStatusSnapshot>? handlers = RouteStatusChanged;
        if (handlers is null) return;

        foreach (Delegate d in handlers.GetInvocationList()) {
            try {
                // 逐个调用：单个 gRPC 流故障不得中断其余客户端的状态推送
                ((Action<RouteStatusSnapshot>)d)(snapshot);
            } catch (Exception ex) {
                _logger.LogError(ex,
                    "RouteStatusChanged 订阅方抛出异常，已隔离（route={RouteId}）。", snapshot.RouteId);
            }
        }
    }

    // ============================================================================
    // 读写执行
    // ============================================================================

    /// <summary>通过 route_id 执行读取。</summary>
    public async Task<OperationResult<byte[]>> ReadByRouteIdAsync(
        string routeId, string dataAddress, int length, CancellationToken cancellationToken) {

        // Host 侧登记表未命中：路由从未注册或已被注销
        if (!_registrationsByRouteId.TryGetValue(routeId, out RouteRuntimeRegistration? registration))
            return OperationResult<byte[]>.Fail("route not found", KernelErrorCode.RouteNotFound);

        // 长度非法：协议帧无法构造，提前拒绝以免打出错误报文
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

        // Host 侧登记表未命中：写入目标路由不存在
        if (!_registrationsByRouteId.TryGetValue(routeId, out RouteRuntimeRegistration? registration))
            return OperationResult.Fail("route not found", KernelErrorCode.RouteNotFound);

        // Protobuf / 调用方可能传 null；归一为空数组，由协议驱动决定是否接受零长度写
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
        // 进入 RouteEntry 独占门控：读与写共用同一把锁，并补足串口帧间静默
        => registration.RouteEntry.ExecuteExclusiveAsync(
            ct => RunReadWithPolicyAsync(registration, ioAction, ct),
            cancellationToken);

    /// <summary>
    /// 在运行策略下执行一次读：失败且属于链路类错误时重连并重试一次。
    /// </summary>
    /// <remarks>
    /// 只重试<b>一次</b>。链路问题重连一次不成，多半是设备真的断了，
    /// 继续重试只会把调用方拖在这里，而上层的轮询本就会在下个周期再来。
    /// 每次读写的成败都会推送在线状态，这是 UI 状态灯的主要来源。
    /// </remarks>
    private async Task<OperationResult<byte[]>> RunReadWithPolicyAsync(
        RouteRuntimeRegistration registration,
        Func<CancellationToken, Task<OperationResult<byte[]>>> ioAction,
        CancellationToken cancellationToken) {

        try {
            // 第一次尝试：协议驱动经 TransportClient 向 PLC 发读请求
            OperationResult<byte[]> result = await ioAction(cancellationToken).ConfigureAwait(false);
            if (result.Success) {
                // 读成功视为链路健康，把在线状态推给订阅 UI
                PublishStatus(registration.RouteId, online: true, KernelErrorCode.None, string.Empty);
                return result;
            }

            // IO / 不可用 / 超时：链路可能已断，尝试重连后再读一次
            if (ShouldAttemptReconnect(result.ErrorCode)) {
                _logger.LogWarning("Route '{RouteId}': IO error {Code}, attempting reconnect.", registration.RouteId, result.ErrorCode);
                bool reconnected = await TryReconnectAsync(registration, cancellationToken).ConfigureAwait(false);
                if (reconnected) {
                    // 重连成功：在同一独占期内立刻重试，避免把窗口让给其他读写
                    OperationResult<byte[]> retry = await ioAction(cancellationToken).ConfigureAwait(false);
                    PublishStatus(registration.RouteId, retry.Success, retry.ErrorCode, retry.Success ? string.Empty : retry.ErrorMessage);
                    if (!retry.Success)
                        _logger.LogError("Route '{RouteId}': retry after reconnect also failed: {Error}", registration.RouteId, retry.ErrorMessage);
                    return retry;
                }
            }

            // 不可重连的业务错误，或重连失败：标为离线并原样返回
            PublishStatus(registration.RouteId, online: false, result.ErrorCode, result.ErrorMessage);
            _logger.LogWarning("Route '{RouteId}': IO failed: {Code} {Error}", registration.RouteId, result.ErrorCode, result.ErrorMessage);
            return result;
        } catch (Exception ex) {
            // 协议驱动抛出未包装异常：视为传输故障，标离线，避免异常冲出内核
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
        // 写路径同样进入独占门控，与读互斥，防止请求字节在同一流上交织
        => registration.RouteEntry.ExecuteExclusiveAsync(
            ct => RunWriteWithPolicyAsync(registration, ioAction, ct),
            cancellationToken);

    /// <summary>
    /// 在运行策略下执行一次写：失败且属于链路类错误时重连并重试一次。
    /// </summary>
    /// <remarks>
    /// 与读路径同构，但重试的含义不同：写是<b>有副作用</b>的操作。
    /// 这里能安全重试，是因为只有在传输层错误（连接断开、超时）时才重试——
    /// 那意味着请求多半没送达。协议层返回的业务失败一律不重试。
    /// </remarks>
    private async Task<OperationResult> RunWriteWithPolicyAsync(
        RouteRuntimeRegistration registration,
        Func<CancellationToken, Task<OperationResult>> ioAction,
        CancellationToken cancellationToken) {

        try {
            // 第一次尝试：协议驱动向 PLC 写入寄存器/线圈
            OperationResult result = await ioAction(cancellationToken).ConfigureAwait(false);
            if (result.Success) {
                // 写成功视为链路健康
                PublishStatus(registration.RouteId, online: true, KernelErrorCode.None, string.Empty);
                return result;
            }

            // IO / 不可用 / 超时：先重连再写，避免把一次掉线当成业务失败
            if (ShouldAttemptReconnect(result.ErrorCode)) {
                _logger.LogWarning("Route '{RouteId}': write IO error {Code}, attempting reconnect.", registration.RouteId, result.ErrorCode);
                bool reconnected = await TryReconnectAsync(registration, cancellationToken).ConfigureAwait(false);
                if (reconnected) {
                    // 重连成功后在同一独占期内立刻重试本次写入
                    OperationResult retry = await ioAction(cancellationToken).ConfigureAwait(false);
                    PublishStatus(registration.RouteId, retry.Success, retry.ErrorCode, retry.Success ? string.Empty : retry.ErrorMessage);
                    if (!retry.Success)
                        _logger.LogError("Route '{RouteId}': write retry after reconnect failed: {Error}", registration.RouteId, retry.ErrorMessage);
                    return retry;
                }
            }

            // 不可重连或重连失败：标离线，把原始错误交给调用方
            PublishStatus(registration.RouteId, online: false, result.ErrorCode, result.ErrorMessage);
            _logger.LogWarning("Route '{RouteId}': write failed: {Code} {Error}", registration.RouteId, result.ErrorCode, result.ErrorMessage);
            return result;
        } catch (Exception ex) {
            // 未包装异常视为传输故障，避免冲出内核把 gRPC 调用打成 UNKNOWN
            _logger.LogError(ex, "Route '{RouteId}': unhandled exception in write IO action.", registration.RouteId);
            PublishStatus(registration.RouteId, online: false, KernelErrorCode.TransportIoError, ex.Message);
            return OperationResult.Fail(ex.Message, KernelErrorCode.TransportIoError);
        }
    }

    // ============================================================================
    // 重连与状态发布
    // ============================================================================

    /// <remarks>
    /// <see cref="KernelErrorCode.Cancelled"/> 刻意<b>不在</b>重连之列：
    /// 用户主动取消（关页面、停轮询、退出应用）不是链路故障，
    /// 误判会在批量停止轮询时引发几十条路由同时重连。
    /// </remarks>
    private static bool ShouldAttemptReconnect(KernelErrorCode errorCode) =>
        errorCode is KernelErrorCode.TransportIoError
            or KernelErrorCode.TransportUnavailable
            or KernelErrorCode.Timeout;

    /// <summary>
    /// 尝试重建该路由的物理连接。
    /// </summary>
    /// <returns>重连是否成功。</returns>
    /// <remarks>
    /// 必须<b>先断后连</b>：半开的 socket 与被占用的串口句柄不释放，
    /// 后续 Connect 会直接失败（串口报"拒绝访问"），于是这条路由再也起不来。
    /// 端点取自登记时保存的副本，不重新解析配置——配置可能已被改动，
    /// 重连要连的是原来那台设备。
    /// </remarks>
    private async Task<bool> TryReconnectAsync(RouteRuntimeRegistration registration, CancellationToken cancellationToken) {
        try {
            // 先断开旧连接：半开 socket / 被占用的串口句柄必须释放，否则 Connect 会失败
            await registration.RouteEntry.TransportClient.DisconnectAsync(cancellationToken).ConfigureAwait(false);
            // 用登记时保存的端点重新握手（TCP 三次握手或重新打开串口）
            OperationResult reconnect = await registration.RouteEntry.TransportClient
                .ConnectAsync(registration.Endpoint, cancellationToken)
                .ConfigureAwait(false);

            if (reconnect.Success) {
                _logger.LogInformation("Route '{RouteId}': reconnected successfully.", registration.RouteId);
                // 重连成功立即标上线，UI 不必等到下一次读写才变绿
                PublishStatus(registration.RouteId, online: true, KernelErrorCode.None, string.Empty);
                return true;
            }

            _logger.LogWarning("Route '{RouteId}': reconnect failed: {Error}", registration.RouteId, reconnect.ErrorMessage);
            // 重连失败：把传输层错误码原样推给订阅方
            PublishStatus(registration.RouteId, online: false, reconnect.ErrorCode, reconnect.ErrorMessage);
            return false;
        } catch (Exception ex) {
            // Disconnect/Connect 抛异常：视为传输故障，不让异常冲出独占门控
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
        // 归一 null，便于与快照中的空字符串做序数比较
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

            // 状态确有变化：写入新快照，时间戳取「变为当前值」的此刻
            published = new RouteStatusSnapshot {
                RouteId      = routeId,
                Online       = online,
                ErrorCode    = errorCode,
                ErrorMessage = message,
                TimestampUtc = DateTimeOffset.UtcNow
            };
            _routeStatuses[routeId] = published;
        }

        // 锁外扇出，避免订阅方（gRPC 流写入）把状态锁拖死
        RaiseStatusChanged(published);
    }

    // ── Inner types ───────────────────────────────────────────────────────────

    // RegisterRouteCommand / RouteRuntimeInfo / RouteStatusSnapshot
    // 已提为顶层类型，见 Engine/Models/。
    // 作为嵌套类型时，实现 IRouteAssemblyService 必须依赖 EngineRuntime 这个具体类。


    /// <summary>
    /// Host 侧路由登记项：RouteId 与 Router 层 RouteEntry 的对应关系。
    /// 读写互斥与串口帧间隔在 <see cref="RouteEntry"/> 内实现，此处不再持有第二套信号量。
    /// </summary>
    private sealed class RouteRuntimeRegistration {
        /// <param name="routeId">路由 Id，读写句柄。</param>
        /// <param name="routeKey">路由键，用于查重与日志。</param>
        /// <param name="endpoint">连接端点，重连时原样复用。</param>
        /// <param name="transportId">传输插件 Id。</param>
        /// <param name="routeEntry">Router 层的路由实体，读写互斥在其内部实现。</param>
        /// <param name="isSerialRoute">是否串口路由。</param>
        /// <param name="minIoIntervalMs">最小 I/O 间隔；负值钳到 0。</param>
        public RouteRuntimeRegistration(
            string routeId, RouteKey routeKey, TransportEndpoint endpoint,
            string transportId, RouteEntry routeEntry, bool isSerialRoute, int minIoIntervalMs) {

            RouteId          = routeId;
            RouteKey         = routeKey;
            Endpoint         = endpoint;
            TransportId      = transportId;
            RouteEntry       = routeEntry;
            IsSerialRoute    = isSerialRoute;
            // 负间隔无物理含义，钳到 0；实际节流以 RouteEntry.MinIoIntervalMs 为准
            MinIoIntervalMs  = Math.Max(0, minIoIntervalMs);
        }

        /// <summary>路由 Id，UI 侧的读写句柄。</summary>
        public string            RouteId         { get; }
        /// <summary>路由键，标识一条物理连接；用于查重与诊断日志。</summary>
        public RouteKey          RouteKey        { get; }
        /// <summary>连接端点。<b>重连时原样复用</b>，不重新解析配置。</summary>
        public TransportEndpoint Endpoint       { get; }
        /// <summary>传输插件 Id，诊断用。</summary>
        public string            TransportId     { get; }
        /// <summary>Router 层路由实体，持有传输客户端与协议驱动；读写互斥在其内部实现。</summary>
        public RouteEntry        RouteEntry      { get; }
        /// <summary>装配时是否为串口路由（诊断/扩展用；节流已在 RouteEntry）。</summary>
        public bool              IsSerialRoute   { get; }
        /// <summary>装配时采用的最小 I/O 间隔（毫秒）；实际节流在 RouteEntry.MinIoIntervalMs。</summary>
        public int               MinIoIntervalMs { get; }
    }
}
