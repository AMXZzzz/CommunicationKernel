// -----------------------------------------------------------------------------
// 文件: Services/HostSession.cs
// 层级: UI 层 — Blazor Server
// 作用: 持有当前 HostClient、健康轮询、全量状态流、路由清单、设备对账。
//       页面禁止各自开 WatchRouteStatus：N 条流会在断线时把卡片钉死在「在线」。
// -----------------------------------------------------------------------------

using System.Collections.Concurrent;
using CommunicationKernel.Host.Sdk;

namespace CommunicationKernel.UI.Web.Services;

/// <summary>Web UI 对 Host.App 的会话门面。单例 + IHostedService。</summary>
public sealed class HostSession : IHostedService, IAsyncDisposable
{
    private readonly ILoggerFactory _loggerFactory;
    private readonly ILogger<HostSession> _logger;
    private readonly WebSettingsStore _settings;
    private readonly WebDeviceStore _devices;
    private readonly AppLogStore _log;
    private readonly ConcurrentDictionary<string, RouteStatusDto> _status = new(StringComparer.OrdinalIgnoreCase);
    private readonly object _clientGate = new();
    private readonly object _routesGate = new();

    private HostClient _client;
    private CancellationTokenSource _loopCts = new();
    private int _reconcileGate;

    /// <summary>0 = 存活，1 = 已释放。见 <see cref="DisposeAsync"/> 中关于重复释放的说明。</summary>
    private int _disposed;
    private IReadOnlyList<RouteDto> _routes = Array.Empty<RouteDto>();

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

    public HostClient Client
    {
        get { lock (_clientGate) return _client; }
    }

    public string Address { get; private set; }
    public bool Online { get; private set; }
    public string HostVersion { get; private set; } = "--";
    public int RouteCount { get; private set; }
    public string LastError { get; private set; } = string.Empty;

    /// <summary>最近一次从宿主拉到的路由清单。Host 离线时保留快照，卡片仍能画出来。</summary>
    public IReadOnlyList<RouteDto> Routes
    {
        get { lock (_routesGate) return _routes; }
    }

    /// <summary>后台线程触发；页面必须 InvokeAsync(StateHasChanged)。</summary>
    public event Action? Changed;

    public bool IsRouteOnline(string routeId) =>
        _status.TryGetValue(routeId, out RouteStatusDto? dto) && dto.Online;

    public RouteStatusDto? GetStatus(string routeId) =>
        _status.TryGetValue(routeId, out RouteStatusDto? dto) ? dto : null;

    public IReadOnlyDictionary<string, RouteStatusDto> StatusSnapshot() =>
        new Dictionary<string, RouteStatusDto>(_status, StringComparer.OrdinalIgnoreCase);

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

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        await CancelLoopsAsync().ConfigureAwait(false);
    }

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
    public async Task<(bool Ok, string Version, int RouteCount)> ProbeAddressAsync(
        string address,
        CancellationToken ct = default)
    {
        string normalized = (address ?? string.Empty).Trim();
        if (string.IsNullOrWhiteSpace(normalized))
            return (false, string.Empty, 0);

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

    public async Task ProbeAsync(CancellationToken ct = default)
    {
        HostClient client = Client;
        var (ok, version, count) = await client.HealthAsync(ct).ConfigureAwait(false);
        bool wasOnline = Online;
        Online = ok;
        HostVersion = ok ? version : "--";
        RouteCount = count;
        LastError = ok ? string.Empty : "Host.App 无响应";
        if (ok && !wasOnline)
            _ = ReconcileAsync(ct);
        else if (ok)
            _ = RefreshRoutesAsync(ct);
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

    private HostClient CreateClient(string address) =>
        new(address, _loggerFactory.CreateLogger<HostClient>());

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

    private void MarkAllOffline()
    {
        foreach (string key in _status.Keys.ToArray())
        {
            if (_status.TryGetValue(key, out RouteStatusDto? prev))
                _status[key] = prev with { Online = false, ErrorCode = "DISCONNECTED", ErrorMessage = "状态流中断" };
        }
    }

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

    private void RaiseChanged()
    {
        try { Changed?.Invoke(); }
        catch (Exception ex) { _logger.LogError(ex, "HostSession.Changed 订阅方异常，已隔离"); }
    }
}
