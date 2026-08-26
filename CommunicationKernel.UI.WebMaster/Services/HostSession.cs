// -----------------------------------------------------------------------------
// 文件: Services/HostSession.cs
// 层级: UI 层 — Blazor Server
// 作用: 持有当前 HostClient、健康轮询、全量状态流、路由清单、设备对账。
//       页面禁止各自开 WatchRouteStatus：N 条流会在断线时把卡片钉死在「在线」。
// -----------------------------------------------------------------------------

using System.Collections.Concurrent;
using CommunicationKernel.EngineHost.Sdk;

namespace CommunicationKernel.UI.WebMaster.Services;

/// <summary>Web UI 对 EngineHost.App 的会话门面。单例 + IHostedService。</summary>
public sealed class HostSession : IHostedService, IAsyncDisposable
{
    /// <summary>日志工厂，用于为切换地址后新建的 <see cref="HostClient"/> 造记录器。</summary>
    private readonly ILoggerFactory _loggerFactory;

    /// <summary>框架日志器，记录会话循环自身的异常。</summary>
    private readonly ILogger<HostSession> _logger;

    /// <summary>设置存储，提供 Host 地址。</summary>
    private readonly WebSettingsStore _settings;

    /// <summary>本地设备配置库，宿主恢复后按它对账重新注册。</summary>
    private readonly WebDeviceStore _devices;

    /// <summary>面向操作员的日志。</summary>
    private readonly AppLogStore _log;

    /// <summary>
    /// 各路由的最新状态，来自 WatchRouteStatus 流。
    /// </summary>
    /// <remarks>
    /// 用 <see cref="ConcurrentDictionary{TKey,TValue}"/> 而非加锁：写入方是状态流线程，
    /// 读取方是每次页面渲染（<see cref="IsRouteOnline"/> 在表格里逐行调用），
    /// 读多写少且要求无锁快路径。
    /// </remarks>
    private readonly ConcurrentDictionary<string, RouteStatusDto> _status = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>保护 <see cref="_client"/> 引用替换的锁。切换 Host 地址时会换掉整个客户端。</summary>
    private readonly object _clientGate = new();

    /// <summary>保护 <see cref="_routes"/> 引用替换的锁。</summary>
    private readonly object _routesGate = new();

    /// <summary>当前 gRPC 客户端。地址变更时整体替换，不复用旧通道。</summary>
    private HostClient _client;

    /// <summary>两个后台循环的取消源；<see cref="StartAsync"/> 时重建，停止时取消。</summary>
    private CancellationTokenSource _loopCts = new();

    /// <summary>
    /// 对账限流闸门，0 = 空闲，1 = 对账进行中。
    /// </summary>
    /// <remarks>
    /// 宿主恢复瞬间健康循环与状态流可能同时察觉，各自触发一次对账。
    /// 并发对账会对同一批设备重复发起注册，宿主侧表现为大量 RouteBusy。
    /// 用 <see cref="Interlocked"/> 保证同一时刻只有一次在跑。
    /// </remarks>
    private int _reconcileGate;

    /// <summary>0 = 存活，1 = 已释放。见 <see cref="DisposeAsync"/> 中关于重复释放的说明。</summary>
    private int _disposed;

    /// <summary>最近一次拉到的路由清单，见 <see cref="Routes"/>。</summary>
    private IReadOnlyList<RouteDto> _routes = Array.Empty<RouteDto>();

    /// <param name="loggerFactory">日志工厂。</param>
    /// <param name="logger">框架日志器。</param>
    /// <param name="settings">设置存储，构造时即用于读出 Host 地址。</param>
    /// <param name="devices">本地设备配置库。</param>
    /// <param name="log">操作员日志。</param>
    /// <remarks>
    /// 构造时就建好客户端而不等到 <see cref="StartAsync"/>：
    /// Blazor 组件可能在 HostedService 启动之前就注入本类并访问 <see cref="Client"/>，
    /// 那时若 <c>_client</c> 还是 null 就会空引用崩溃。
    /// </remarks>
    public HostSession(
        ILoggerFactory loggerFactory,
        ILogger<HostSession> logger,
        WebSettingsStore settings,
        WebDeviceStore devices,
        AppLogStore log)
    {
        _loggerFactory = loggerFactory;
        _logger = logger;
        _settings = settings;
        _devices = devices;
        _log = log;

        Address = _settings.LoadAddress();
        _client = CreateClient(Address);
    }

    /// <summary>
    /// 当前 gRPC 客户端。
    /// </summary>
    /// <remarks>
    /// 加锁读取：切换 Host 地址时本字段会被整体替换，
    /// 无锁读可能拿到正在被释放的旧客户端。
    /// 调用方应当每次都经本属性取用，不要缓存到局部字段跨 await 使用。
    /// </remarks>
    public HostClient Client
    {
        get { lock (_clientGate) return _client; }
    }

    /// <summary>当前 EngineHost.App 地址，形如 <c>http://192.168.1.10:5000</c>。</summary>
    public string Address { get; private set; }

    /// <summary>EngineHost.App 是否可达。由健康循环维护。</summary>
    public bool Online { get; private set; }

    /// <summary>宿主版本号；离线时为 "--"。</summary>
    public string HostVersion { get; private set; } = "--";

    /// <summary>宿主当前登记的路由条数；离线时保留最后一次的值。</summary>
    public int RouteCount { get; private set; }

    /// <summary>最近一次连接失败的原因，显示在底部状态栏；连上后清空。</summary>
    public string LastError { get; private set; } = string.Empty;

    /// <summary>最近一次从宿主拉到的路由清单。Host 离线时保留快照，卡片仍能画出来。</summary>
    public IReadOnlyList<RouteDto> Routes
    {
        get { lock (_routesGate) return _routes; }
    }

    /// <summary>后台线程触发；页面必须 InvokeAsync(StateHasChanged)。</summary>
    public event Action? Changed;

    /// <summary>
    /// 某条路由当前是否在线。
    /// </summary>
    /// <remarks>
    /// 状态表里<b>没有</b>该路由时返回 false——未知一律按离线处理。
    /// 反过来（未知按在线）会让刚添加、还没建连接的设备显示成绿灯，
    /// 操作员据此以为可以读写。
    /// </remarks>
    public bool IsRouteOnline(string routeId) =>
        _status.TryGetValue(routeId, out RouteStatusDto? dto) && dto.Online;

    /// <summary>取某条路由的完整状态；未知时返回 null（注意与「已知离线」的区别）。</summary>
    public RouteStatusDto? GetStatus(string routeId) =>
        _status.TryGetValue(routeId, out RouteStatusDto? dto) ? dto : null;

    /// <summary>取全部路由状态的快照副本，供页面一次性渲染。</summary>
    /// <remarks>
    /// 返回副本而非底层字典：渲染期间状态流仍在写入，
    /// 直接交出并发字典会让同一次渲染里前后两行读到不同代的数据。
    /// </remarks>
    public IReadOnlyDictionary<string, RouteStatusDto> StatusSnapshot() =>
        new Dictionary<string, RouteStatusDto>(_status, StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// 启动健康探测与状态流两个后台循环。
    /// </summary>
    /// <remarks>
    /// <para>
    /// 两个循环职责不同，缺一不可：健康循环回答"宿主还在不在"，
    /// 状态流回答"每条路由通不通"。宿主在线但某台 PLC 掉线是常态。
    /// </para>
    /// <para>
    /// 立即返回已完成的 Task：<see cref="IHostedService.StartAsync"/> 会阻塞应用启动，
    /// 在这里 await 循环会让 Web 服务器永远起不来。
    /// </para>
    /// <para>
    /// 先取旧令牌源的引用再替换、随后释放：支持在切换 Host 地址后重启循环，
    /// 不释放旧的会在每次切换地址时泄漏一个 <see cref="CancellationTokenSource"/>。
    /// </para>
    /// </remarks>
    public Task StartAsync(CancellationToken cancellationToken)
    {
        CancellationTokenSource previous = _loopCts;
        _loopCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        previous.Dispose();

        CancellationToken ct = _loopCts.Token;
        _ = Task.Run(() => HealthLoopAsync(ct), ct);
        _ = Task.Run(() => WatchLoopAsync(ct), ct);

        _log.Info("Host", "会话已启动，目标 " + Address);
        return Task.CompletedTask;
    }

    /// <summary>停止两个后台循环。</summary>
    public async Task StopAsync(CancellationToken cancellationToken)
    {
        await CancelLoopsAsync().ConfigureAwait(false);
    }

    /// <summary>取消循环并释放 gRPC 客户端。幂等。</summary>
    public async ValueTask DisposeAsync()
    {
        // 必须幂等——本实例在容器里被登记了两次：
        //   AddSingleton<HostSession>()                           ← 捕获一次待释放
        //   AddHostedService(sp => sp.GetRequiredService<...>())  ← 工厂返回同一实例，再捕获一次
        // 容器不会去重，关站时同一个对象被释放两遍。第二遍再去 Cancel 已释放的
        // CancellationTokenSource 就会抛 ObjectDisposedException，
        // 表现为关站时冒出一个没人处理的异常。
        //
        // IAsyncDisposable 的契约本就要求可重复调用，所以这里用闸门兜住，
        // 而不是去迁就某种特定的注册写法。
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;

        await CancelLoopsAsync().ConfigureAwait(false);
        _loopCts.Dispose();

        HostClient client;
        lock (_clientGate)
            client = _client;
        await client.DisposeAsync().ConfigureAwait(false);
    }

    /// <summary>
    /// 取消后台循环。CTS 已被释放时视为「循环早就停了」，不再抛出。
    /// </summary>
    /// <remarks>
    /// StopAsync 与 DisposeAsync 都会走到这里，而宿主关站时两者都会被调用，
    /// 顺序与次数由框架决定——这里不能假设自己是第一个。
    /// </remarks>
    private async Task CancelLoopsAsync()
    {
        try
        {
            await _loopCts.CancelAsync().ConfigureAwait(false);
        }
        catch (ObjectDisposedException)
        {
            // 已释放：循环早已随之结束，没有需要取消的东西
        }
    }

    /// <summary>设置页切换地址：释放旧通道，重建客户端并立即探测。</summary>
    public async Task SwitchAddressAsync(string address, CancellationToken ct = default)
    {
        string normalized = (address ?? string.Empty).Trim();
        if (string.IsNullOrWhiteSpace(normalized))
            throw new ArgumentException("地址不能为空。", nameof(address));

        _settings.SaveAddress(normalized);
        HostClient old;
        lock (_clientGate)
        {
            old = _client;
            Address = normalized;
            _client = CreateClient(normalized);
        }
        try { await old.DisposeAsync().ConfigureAwait(false); }
        catch (Exception ex) { _logger.LogDebug(ex, "释放旧 HostClient"); }

        _status.Clear();
        lock (_routesGate)
            _routes = Array.Empty<RouteDto>();
        Online = false;
        HostVersion = "--";
        RouteCount = 0;
        RaiseChanged();
        await ProbeAsync(ct).ConfigureAwait(false);
        _log.Info("Host", "已切换地址: " + normalized);
    }

    /// <summary>
    /// 用临时客户端探测指定地址，不切换当前会话。
    /// 设置页「测试连接」必须走这里，否则测的是旧地址。
    /// </summary>
    public async Task<HealthResultDto> ProbeAddressAsync(
        string address,
        CancellationToken ct = default)
    {
        string normalized = (address ?? string.Empty).Trim();
        if (string.IsNullOrWhiteSpace(normalized))
            return HealthResultDto.Offline();

        HostClient temp = new(normalized, _loggerFactory.CreateLogger<HostClient>());
        try
        {
            return await temp.HealthAsync(ct).ConfigureAwait(false);
        }
        finally
        {
            await temp.DisposeAsync().ConfigureAwait(false);
        }
    }

    /// <summary>
    /// 探测宿主健康状态并据此更新会话状态。
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>离线 → 在线的跃迁要触发对账</b>，而持续在线只需刷新路由清单。
    /// 区分这两者很重要：对账会按本地配置重新注册全部设备，
    /// 每个节拍都做一次会对宿主造成持续的注册风暴。
    /// </para>
    /// <para>
    /// 对账与刷新都用弃元不等待：本方法在健康循环的节拍里调用，
    /// 等待对账完成（可能涉及几十台设备的建链）会让健康探测停摆，
    /// 界面上表现为宿主状态灯长时间不更新。
    /// </para>
    /// </remarks>
    public async Task ProbeAsync(CancellationToken ct = default)
    {
        HostClient client = Client;
        var (ok, version, count) = await client.HealthAsync(ct).ConfigureAwait(false);

        bool wasOnline = Online;
        Online = ok;
        HostVersion = ok ? version : "--";
        RouteCount = count;
        LastError = ok ? string.Empty : "EngineHost.App 无响应";

        if (ok && !wasOnline)
            // 刚恢复：按本地配置补注册所有设备
            _ = ReconcileAsync(ct);
        else if (ok)
            // 持续在线：只同步路由清单，别的 UI 可能也在增删设备
            _ = RefreshRoutesAsync(ct);

        // 宿主不可达时，所有路由状态一律置为离线——
        // 保留旧的"在线"会让操作员对着绿灯以为还能读写
        if (!ok)
            MarkAllOffline();
        RaiseChanged();
    }

    /// <summary>从宿主重新拉取路由清单。注册 / 注销后由页面显式调用。</summary>
    public async Task RefreshRoutesAsync(CancellationToken ct = default)
    {
        try
        {
            IReadOnlyList<RouteDto> live = await Client.QueryRoutesAsync(ct: ct).ConfigureAwait(false);
            bool changed;
            lock (_routesGate)
            {
                changed = !SameRoutes(_routes, live);
                if (changed)
                    _routes = live.ToList();
            }
            RouteCount = live.Count;
            if (changed)
                RaiseChanged();
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "刷新路由清单失败");
        }
    }

    /// <summary>操作员手动触发对账（Host 已在线但部分本地设备未上线路由）。</summary>
    public Task ReconcileNowAsync(CancellationToken ct = default) => ReconcileAsync(ct);

    /// <summary>为指定地址创建 gRPC 客户端。</summary>
    /// <remarks>
    /// 每个 <see cref="HostClient"/> 持有独立的 HTTP/2 连接池，切换地址必须整体换新，
    /// 不能复用旧实例——通道的目标地址在构造时就固定了。
    /// </remarks>
    private HostClient CreateClient(string address) =>
        new(address, _loggerFactory.CreateLogger<HostClient>());

    /// <summary>
    /// 健康探测循环，5 秒一次。
    /// </summary>
    /// <remarks>
    /// 5 秒是权衡：更短会让离线期间的失败日志刷屏，更长则状态灯明显滞后于现实。
    /// 任何异常都不退出循环——宿主可能稍后恢复，
    /// 循环一旦停摆，界面会永久停在最后那一刻的状态且没有任何提示。
    /// </remarks>
    private async Task HealthLoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                await ProbeAsync(ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException) { return; }
            // 关站途中 _loopCts 已被释放：属于正常收尾，不是故障。
            // 若按普通异常记警告并继续，循环会在下一轮 Task.Delay 上再炸一次。
            catch (ObjectDisposedException) { return; }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "健康检查循环异常");
            }

            // Task.Delay 会在令牌上注册回调，CTS 已释放时抛 ObjectDisposedException——
            // 只捕获 OperationCanceledException 的话，这个异常会逃成未观测任务异常
            try { await Task.Delay(TimeSpan.FromSeconds(5), ct).ConfigureAwait(false); }
            catch (OperationCanceledException) { return; }
            catch (ObjectDisposedException) { return; }
        }
    }

    /// <summary>
    /// 全站唯一的路由状态流循环，断线后自动重连。
    /// </summary>
    /// <remarks>
    /// <b>页面禁止各自开 WatchRouteStatus。</b>N 个页面开 N 条流时，
    /// 断线只会让其中一条察觉，其余仍停在最后收到的「在线」状态并把卡片钉死在绿灯上。
    /// 单条流集中维护 <see cref="_status"/>，所有页面读同一份，状态不可能分叉。
    /// </remarks>
    private async Task WatchLoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            HostClient client = Client;
            try
            {
                // 空 routeId = 订阅全部路由，全站共用一条流
                await client.WatchRouteStatusAsync(
                    routeId: string.Empty,
                    onStatus: dto =>
                    {
                        _status[dto.RouteId] = dto;
                        RaiseChanged();
                        return Task.CompletedTask;
                    },
                    onDisconnected: () =>
                    {
                        MarkAllOffline();
                        RaiseChanged();
                        return Task.CompletedTask;
                    },
                    ct: ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException) { return; }
            catch (ObjectDisposedException)
            {
                // 两种来源都会走到这里：
                //   · 切换地址时旧客户端被释放 —— 应当继续，下一轮用新客户端；
                //   · 关站时 _loopCts 被释放 —— 应当退出。
                // 用取消状态区分：已请求取消就收尾，否则重试。
                if (ct.IsCancellationRequested) return;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "状态流循环异常");
            }

            try { await Task.Delay(TimeSpan.FromSeconds(2), ct).ConfigureAwait(false); }
            catch (OperationCanceledException) { return; }
            catch (ObjectDisposedException) { return; }
        }
    }

    /// <summary>宿主从离线恢复：把本地配置里尚未在 Host 上的路由重新注册。</summary>
    private async Task ReconcileAsync(CancellationToken ct)
    {
        if (Interlocked.CompareExchange(ref _reconcileGate, 1, 0) != 0)
            return;
        try
        {
            IReadOnlyList<RouteDto> live = await Client.QueryRoutesAsync(ct: ct).ConfigureAwait(false);
            HashSet<string> present = new(live.Select(r => r.RouteId), StringComparer.OrdinalIgnoreCase);
            foreach (WebDeviceRecord rec in _devices.GetAll())
            {
                if (string.IsNullOrWhiteSpace(rec.RouteId)) continue;
                if (present.Contains(rec.RouteId)) continue;
                var (ok, code, msg, _) = await Client.RegisterRouteAsync(
                    rec.RouteId,
                    rec.ProtocolId,
                    rec.TransportKind,
                    rec.Address,
                    rec.Port,
                    rec.Station,
                    rec.SerialPort,
                    rec.BaudRate,
                    rec.MinIoIntervalMs,
                    ct).ConfigureAwait(false);
                if (ok)
                    _log.Info("Reconcile", "已恢复路由 " + rec.RouteId);
                else
                    _log.Warn("Reconcile", rec.RouteId + " 恢复失败: [" + code + "] " + msg);
            }
            await RefreshRoutesAsync(ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            _log.Warn("Reconcile", "对账失败: " + ex.Message);
        }
        finally
        {
            Interlocked.Exchange(ref _reconcileGate, 0);
            RaiseChanged();
        }
    }

    /// <summary>把所有已知路由标记为离线。</summary>
    /// <remarks>
    /// 宿主不可达时调用。逐条改写而非清空字典：清空会让 <see cref="IsRouteOnline"/>
    /// 返回"未知"，页面上那些设备卡片会整个消失，而操作员需要看到它们仍在、只是断了。
    /// </remarks>
    private void MarkAllOffline()
    {
        foreach (string key in _status.Keys.ToArray())
        {
            if (_status.TryGetValue(key, out RouteStatusDto? prev))
                _status[key] = prev with { Online = false, ErrorCode = "DISCONNECTED", ErrorMessage = "状态流中断" };
        }
    }

    /// <summary>
    /// 判断两份路由清单在内容上是否一致。
    /// </summary>
    /// <remarks>
    /// 用于抑制无意义的重绘：路由清单每 5 秒拉一次，绝大多数时候完全没变，
    /// 每次都触发 <see cref="Changed"/> 会让所有页面持续重绘。
    /// 只比较影响显示的字段，逐项按序比对——宿主返回的顺序是稳定的。
    /// </remarks>
    private static bool SameRoutes(IReadOnlyList<RouteDto> left, IReadOnlyList<RouteDto> right)
    {
        if (left.Count != right.Count) return false;
        Dictionary<string, RouteDto> map = new(StringComparer.OrdinalIgnoreCase);
        foreach (RouteDto item in left)
            map[item.RouteId] = item;
        foreach (RouteDto item in right)
        {
            if (!map.TryGetValue(item.RouteId, out RouteDto? prev) || prev != item)
                return false;
        }
        return true;
    }

    /// <summary>触发 <see cref="Changed"/>，通知所有订阅页面重绘。</summary>
    /// <remarks>
    /// 在<b>后台线程</b>上触发，订阅方负责切回 UI 线程。
    /// 本类刻意不引用 Blazor 的渲染上下文——那会让它无法在测试或其他宿主里复用。
    /// </remarks>
    private void RaiseChanged()
    {
        try { Changed?.Invoke(); }
        catch (Exception ex) { _logger.LogError(ex, "HostSession.Changed 订阅方异常，已隔离"); }
    }
}
