// -----------------------------------------------------------------------------
// 文件: Services/VariablePoller.cs
// 层级: UI 层 — Blazor Server
// 作用: 按 ScanRateMs 轮询已勾选的变量；Host 离线或路由离线时跳过，避免错误刷屏。
// -----------------------------------------------------------------------------

using CommunicationKernel.Host.Sdk;

namespace CommunicationKernel.UI.Web.Services;

/// <summary>后台变量轮询。单例 HostedService。</summary>
public sealed class VariablePoller : IHostedService
{
    private readonly HostSession _session;
    private readonly WebVariableStore _store;

    /// <summary>变量读写服务。轮询与页面共用同一实现，避免字节序处理再次分叉。</summary>
    private readonly IWebVariableService _variables;
    private readonly ILogger<VariablePoller> _logger;
    private CancellationTokenSource? _cts;
    private readonly Dictionary<string, DateTime> _nextDue = new();

    public VariablePoller(HostSession session, WebVariableStore store, IWebVariableService variables, ILogger<VariablePoller> logger)
    {
        _session = session;
        _store = store;
        _variables = variables;
        _logger = logger;
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        _cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        _ = Task.Run(() => LoopAsync(_cts.Token), _cts.Token);
        return Task.CompletedTask;
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        if (_cts is not null)
            await _cts.CancelAsync().ConfigureAwait(false);
    }

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

    private async Task TickAsync(CancellationToken ct)
    {
        DateTime now = DateTime.UtcNow;
        IReadOnlyList<WebVariable> all = _store.GetAll();
        HashSet<string> liveIds = new(all.Select(v => v.Id), StringComparer.Ordinal);

        foreach (string stale in _nextDue.Keys.Where(id => !liveIds.Contains(id)).ToArray())
            _nextDue.Remove(stale);

        foreach (WebVariable v in all)
        {
            if (!v.Polling) continue;
            if (string.IsNullOrWhiteSpace(v.RouteId) || string.IsNullOrWhiteSpace(v.Address)) continue;

            RouteStatusDto? st = _session.GetStatus(v.RouteId);
            if (st is { Online: false })
            {
                if (v.DisplayValue != "OFFLINE" || !v.IsError)
                    _store.ApplyRead(v.Id, "OFFLINE", error: true);
                continue;
            }

            int rate = Math.Max(200, v.ScanRateMs);
            if (_nextDue.TryGetValue(v.Id, out DateTime due) && now < due)
                continue;
            _nextDue[v.Id] = now.AddMilliseconds(rate);

            // 经服务读取：解码时会带上该设备配置的字节序。
            // 此处曾直接调 ValueCodec.Decode 且漏传字节序，导致轮询列与页面手动读
            // 对同一个 CDAB 设备显示不同的值，两边还都报成功。
            VariableReadOutcome result = await _variables.ReadAsync(v, ct).ConfigureAwait(false);

            if (result.Success)
                _store.ApplyRead(v.Id, result.DisplayValue, error: false);
            else
                _store.ApplyRead(v.Id, result.ErrorCode, error: true);
        }
    }
}
