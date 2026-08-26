// -----------------------------------------------------------------------------
// 文件: Services/VariablePoller.cs
// 层级: UI 层 — Blazor Server
// 作用: 按 ScanRateMs 轮询已勾选的变量；Host 离线或路由离线时跳过，避免错误刷屏。
// -----------------------------------------------------------------------------

using CommunicationKernel.EngineHost.Sdk;

namespace CommunicationKernel.UI.WebMaster.Services;

/// <summary>后台变量轮询。单例 HostedService。</summary>
/// <remarks>
/// 轮询不依附任何页面：操作员切走页面甚至关掉浏览器，产线数据仍要继续采集，
/// 因为 MES 监控页与变量页共用同一份读值。
/// </remarks>
public sealed class VariablePoller : IHostedService
{
    /// <summary>会话，提供 Host 在线状态与各路由的实时状态。</summary>
    private readonly HostSession _session;

    /// <summary>变量表，读值回填的目标。</summary>
    private readonly WebVariableStore _store;

    /// <summary>变量读写服务。轮询与页面共用同一实现，避免字节序处理再次分叉。</summary>
    private readonly IWebVariableService _variables;

    /// <summary>框架日志器，仅用于记录轮询循环自身的异常。</summary>
    private readonly ILogger<VariablePoller> _logger;

    /// <summary>轮询取消源，<see cref="StopAsync"/> 时触发。</summary>
    private CancellationTokenSource? _cts;

    /// <summary>
    /// 每条变量下一次到期的时刻（UTC）。
    /// </summary>
    /// <remarks>
    /// 各变量的 <c>ScanRateMs</c> 互不相同，不能用一个统一间隔驱动全部。
    /// 只被轮询循环这一个线程访问，因此用普通 <see cref="Dictionary{TKey,TValue}"/> 即可。
    /// </remarks>
    private readonly Dictionary<string, DateTime> _nextDue = new();

    /// <param name="session">会话服务。</param>
    /// <param name="store">变量表。</param>
    /// <param name="variables">变量读写服务。</param>
    /// <param name="logger">框架日志器。</param>
    public VariablePoller(HostSession session, WebVariableStore store, IWebVariableService variables, ILogger<VariablePoller> logger)
    {
        _session = session;
        _store = store;
        _variables = variables;
        _logger = logger;
    }

    /// <summary>
    /// 启动后台轮询循环。
    /// </summary>
    /// <remarks>
    /// 用 <c>Task.Run</c> 起循环并<b>立即</b>返回已完成的 Task：
    /// <see cref="IHostedService.StartAsync"/> 会阻塞应用启动流程，
    /// 在这里 await 循环会让 Web 服务器永远起不来。
    /// 令牌用 <c>CreateLinkedTokenSource</c> 串联，使框架关停与主动 Stop 都能生效。
    /// </remarks>
    public Task StartAsync(CancellationToken cancellationToken)
    {
        _cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        _ = Task.Run(() => LoopAsync(_cts.Token), _cts.Token);

        return Task.CompletedTask;
    }

    /// <summary>停止轮询。</summary>
    /// <remarks>
    /// 只取消不等待：循环最多在下一个 200ms 节拍退出，
    /// 而应用关停不应为此多等一轮。
    /// </remarks>
    public async Task StopAsync(CancellationToken cancellationToken)
    {
        if (_cts is not null)
            await _cts.CancelAsync().ConfigureAwait(false);
    }

    /// <summary>
    /// 轮询主循环，固定 200ms 节拍。
    /// </summary>
    /// <remarks>
    /// <para>
    /// 节拍与扫描周期是两件事：循环每 200ms 醒一次，
    /// 但只处理那些已经到期的变量（见 <see cref="_nextDue"/>）。
    /// 200ms 也是允许的最小扫描周期，再小没有意义。
    /// </para>
    /// <para>
    /// Host 离线时整批跳过：逐条去读只会得到一串必然失败的 RPC，
    /// 既拖慢节拍又把日志刷满。
    /// </para>
    /// <para>
    /// 捕获所有异常但不退出循环——单次异常多半是瞬时的网络问题，
    /// 让整个轮询器就此停摆会导致产线数据静默停止更新，且没有任何提示。
    /// </para>
    /// </remarks>
    private async Task LoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                if (_session.Online)
                    await TickAsync(ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException) { return; }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "变量轮询异常");
            }

            try { await Task.Delay(200, ct).ConfigureAwait(false); }
            catch (OperationCanceledException) { return; }
        }
    }

    /// <summary>
    /// 一个节拍：清理过期记账，然后读取所有到期的变量。
    /// </summary>
    /// <remarks>
    /// 刻意<b>串行</b>读取：同一路由的并发读会被 Router 合并或排队，并发发起没有收益；
    /// 串口设备还有帧间静默要求，并发只会制造超时。
    /// </remarks>
    private async Task TickAsync(CancellationToken ct)
    {
        DateTime now = DateTime.UtcNow;
        IReadOnlyList<WebVariable> all = _store.GetAll();

        // 清掉已删除变量的到期记账。不清会让 _nextDue 随着增删操作单向增长——
        // 这是个长期运行的常驻服务，泄漏会一直累积到进程重启
        HashSet<string> liveIds = new(all.Select(v => v.Id), StringComparer.Ordinal);
        foreach (string stale in _nextDue.Keys.Where(id => !liveIds.Contains(id)).ToArray())
            _nextDue.Remove(stale);

        foreach (WebVariable v in all)
        {
            // 未勾选轮询，或定义不完整（无路由/无地址）的直接跳过
            if (!v.Polling) continue;
            if (string.IsNullOrWhiteSpace(v.RouteId) || string.IsNullOrWhiteSpace(v.Address)) continue;

            // 路由已知离线：标记一次 OFFLINE 就不再发起 I/O。
            // 判断 DisplayValue 是为了避免每个节拍都触发 Changed 事件把界面刷爆——
            // 状态没变就不用通知
            RouteStatusDto? st = _session.GetStatus(v.RouteId);
            if (st is { Online: false })
            {
                if (v.DisplayValue != "OFFLINE" || !v.IsError)
                    _store.ApplyRead(v.Id, "OFFLINE", error: true);
                continue;
            }

            // 未到期就跳过；到期则先记下下一次时刻，再发起读取。
            // 先记账是有意的：读取耗时会被计入下一个周期，
            // 否则慢设备会让实际周期变成"扫描周期 + 每次读取耗时"而逐渐漂移
            int rate = Math.Max(200, v.ScanRateMs);
            if (_nextDue.TryGetValue(v.Id, out DateTime due) && now < due)
                continue;
            _nextDue[v.Id] = now.AddMilliseconds(rate);

            // 经服务读取：解码时会带上该设备配置的字节序。
            // 此处曾直接调 ValueCodec.Decode 且漏传字节序，导致轮询列与页面手动读
            // 对同一个 CDAB 设备显示不同的值，两边还都报成功。
            VariableReadOutcome result = await _variables.ReadAsync(v, ct).ConfigureAwait(false);

            // 失败时填错误码而非描述：表格那一格很窄，完整描述会被截断成看不懂的半句
            if (result.Success)
                _store.ApplyRead(v.Id, result.DisplayValue, error: false);
            else
                _store.ApplyRead(v.Id, result.ErrorCode, error: true);
        }
    }
}
