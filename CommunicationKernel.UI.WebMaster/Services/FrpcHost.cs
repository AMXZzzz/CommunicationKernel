// -----------------------------------------------------------------------------
// 文件: Services/FrpcHost.cs
// 层级: UI 层 — WebMaster 内网穿透
// 作用: 托管 frpc 子进程：生成配置、拉起、崩溃重启、日志汇聚、退出时清干净。
//
// frpc.exe 不随包分发，由用户自行放到 exe 旁边，理由见 WebTunnelSettings.cs 文件头。
// 公网服务器上仍需自行部署 frps——隧道两头都要有，这半边内嵌不了。
// -----------------------------------------------------------------------------

using System.Diagnostics;
using System.Text;

namespace CommunicationKernel.UI.WebMaster.Services;

/// <summary>把 frpc 作为子进程托管，随 WebMaster 一起启停。</summary>
public sealed class FrpcHost : IHostedService, IDisposable
{
    /// <summary>崩溃后重启前的等待（毫秒）。</summary>
    /// <remarks>
    /// 服务器没起来时 frpc 会立刻退出，不等待就会变成疯狂重启把日志刷满。
    /// 5 秒足以让人看清失败原因，也不至于恢复太慢。
    /// </remarks>
    private const int RestartDelayMs = 5_000;

    /// <summary>本进程要监听的 Web 端口，隧道把它顶到公网。</summary>
    private readonly int _localPort;

    /// <summary>隧道设置。</summary>
    private readonly WebTunnelSettings _settings;

    /// <summary>应用日志。</summary>
    private readonly AppLogStore _log;

    /// <summary>监管循环的取消源。</summary>
    private readonly CancellationTokenSource _cts = new();

    /// <summary>当前 frpc 进程；未运行时为 null。</summary>
    private Process? _proc;

    /// <summary>监管循环句柄，停止时等它收尾。</summary>
    private Task? _loop;

    /// <summary>隧道当前是否已连上（据 frpc 输出判断）。</summary>
    public bool Connected { get; private set; }

    /// <summary>最近一次的状态描述，供设置页显示。</summary>
    public string Status { get; private set; } = "未启动";

    /// <param name="settings">隧道设置。</param>
    /// <param name="localPort">本进程 Web 监听端口。</param>
    /// <param name="log">应用日志。</param>
    public FrpcHost(WebTunnelSettings settings, int localPort, AppLogStore log)
    {
        _settings = settings;
        _localPort = localPort;
        _log = log;
    }

    /// <summary>写出配置并启动监管循环。</summary>
    /// <remarks>
    /// 三个前提任一不满足就安静退出，不视为错误：
    /// 没启用、没放 frpc.exe、配置没填全。它们都是正常状态。
    /// </remarks>
    public Task StartAsync(CancellationToken cancellationToken)
    {
        if (!_settings.Enabled) return Task.CompletedTask;

        if (!WebTunnelSettingsStore.FrpcPresent)
        {
            Status = "未找到 frpc.exe";
            _log.Warn("Tunnel",
                "已启用内网穿透，但 exe 目录下没有 " + WebTunnelSettingsStore.FrpcFileName +
                "。请自行下载后放入，再重启。");
            return Task.CompletedTask;
        }

        if (!_settings.IsComplete)
        {
            Status = "配置不完整";
            _log.Warn("Tunnel", "内网穿透配置不完整（缺服务器地址或 token），未启动。");
            return Task.CompletedTask;
        }

        // 先清掉上一次可能残留的 frpc：WebMaster 若被强杀，子进程会活下来，
        // 占着同一个 remotePort，导致新的一份连上去就被服务器拒绝。
        KillOrphans();

        try
        {
            WriteConfig();
        }
        catch (Exception ex)
        {
            Status = "写配置失败";
            _log.Error("Tunnel", "生成 frpc 配置失败: " + ex.Message);
            return Task.CompletedTask;
        }

        _loop = Task.Run(() => SuperviseAsync(_cts.Token));
        return Task.CompletedTask;
    }

    /// <summary>停止监管并结束 frpc。</summary>
    public async Task StopAsync(CancellationToken cancellationToken)
    {
        try { _cts.Cancel(); } catch (ObjectDisposedException) { }

        KillCurrent();

        if (_loop is not null)
        {
            // 给循环一点时间收尾，但不无限等——停机不该被子进程拖住
            try { await _loop.WaitAsync(TimeSpan.FromSeconds(3), cancellationToken).ConfigureAwait(false); }
            catch { /* 超时或取消都直接放行 */ }
        }
    }

    /// <summary>释放取消源与进程句柄。</summary>
    public void Dispose()
    {
        KillCurrent();
        try { _cts.Dispose(); } catch { }
    }

    // ========================================================================
    // 配置
    // ========================================================================

    /// <summary>
    /// 生成 frpc.toml。
    /// </summary>
    /// <remarks>
    /// 每次启动都重写：设置改过之后要立刻反映，而且用户手改过的内容
    /// 与界面上的设置不一致时，以界面为准更符合预期。
    /// <para>
    /// <c>localIP</c> 固定 127.0.0.1：隧道只需要连到本机，
    /// 写 0.0.0.0 不会更通，反而在多网卡机器上可能选错。
    /// </para>
    /// </remarks>
    private void WriteConfig()
    {
        string path = ConfigPath;
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);

        StringBuilder sb = new();
        sb.AppendLine("# 本文件由 WebMaster 自动生成，手工修改会在下次启动时被覆盖。");
        sb.AppendLine("# 要改请到「系统设置 → 内网穿透」。");
        sb.AppendLine();
        sb.AppendLine($"serverAddr = \"{_settings.ServerAddr.Trim()}\"");
        sb.AppendLine($"serverPort = {_settings.ServerPort}");
        sb.AppendLine("auth.method = \"token\"");
        sb.AppendLine($"auth.token = \"{_settings.Token}\"");
        sb.AppendLine();
        sb.AppendLine("[[proxies]]");
        sb.AppendLine("name = \"ck-web\"");
        sb.AppendLine("type = \"tcp\"");
        sb.AppendLine("localIP = \"127.0.0.1\"");
        sb.AppendLine($"localPort = {_localPort}");
        sb.AppendLine($"remotePort = {_settings.RemotePort}");

        File.WriteAllText(path, sb.ToString());
    }

    /// <summary>生成的 frpc 配置路径，与其它配置同放 config/。</summary>
    private static string ConfigPath => Path.Combine(WebPaths.Root, "frpc.toml");

    // ========================================================================
    // 进程
    // ========================================================================

    /// <summary>
    /// 监管循环：进程退出就重启，直到被取消。
    /// </summary>
    private async Task SuperviseAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                await RunOnceAsync(ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch (Exception ex)
            {
                Status = "启动失败";
                _log.Error("Tunnel", "frpc 启动失败: " + ex.Message);
            }

            if (ct.IsCancellationRequested) return;

            Connected = false;
            Status = "已断开，重连中";

            try { await Task.Delay(RestartDelayMs, ct).ConfigureAwait(false); }
            catch (OperationCanceledException) { return; }
        }
    }

    /// <summary>启动一次 frpc 并等它退出。</summary>
    private async Task RunOnceAsync(CancellationToken ct)
    {
        ProcessStartInfo psi = new(WebTunnelSettingsStore.FrpcPath)
        {
            // -c 指定配置。用完整路径，避免受工作目录影响
            Arguments = "-c \"" + ConfigPath + "\"",
            WorkingDirectory = AppContext.BaseDirectory,

            // 必须重定向：frpc 的失败原因（token 不对、端口被占、连不上）
            // 只在它的标准输出里，不接过来的话界面上只能看到"断开"，查不出为什么。
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };

        using Process p = new() { StartInfo = psi, EnableRaisingEvents = true };
        p.OutputDataReceived += (_, e) => OnFrpcOutput(e.Data);
        p.ErrorDataReceived += (_, e) => OnFrpcOutput(e.Data);

        if (!p.Start())
            throw new InvalidOperationException("Process.Start 返回 false");

        _proc = p;
        Status = "已启动，连接中";
        _log.Info("Tunnel",
            "frpc 已启动，目标 " + _settings.ServerAddr + ":" + _settings.ServerPort +
            "，远端口 " + _settings.RemotePort);

        p.BeginOutputReadLine();
        p.BeginErrorReadLine();

        await p.WaitForExitAsync(ct).ConfigureAwait(false);

        _proc = null;
        _log.Warn("Tunnel", "frpc 已退出（代码 " + p.ExitCode + "）");
    }

    /// <summary>
    /// 把 frpc 的输出转进应用日志，并据此更新连接状态。
    /// </summary>
    /// <remarks>
    /// frpc 没有可编程的状态接口，只能认输出里的关键字。
    /// 这是脆弱的（上游改文案就会失准），因此只用来做界面提示，
    /// 不拿它做任何控制决策——重启与否只看进程是否退出。
    /// </remarks>
    private void OnFrpcOutput(string? line)
    {
        if (string.IsNullOrWhiteSpace(line)) return;

        if (line.Contains("login to server success", StringComparison.OrdinalIgnoreCase))
        {
            Connected = true;
            Status = "已连接";
        }
        else if (line.Contains("start proxy success", StringComparison.OrdinalIgnoreCase))
        {
            Status = "隧道就绪";
        }

        // frpc 自己的错误行按错误级别记，便于在日志页筛出来
        bool isError = line.Contains("[E]", StringComparison.Ordinal)
                       || line.Contains("error", StringComparison.OrdinalIgnoreCase);

        if (isError) _log.Error("frpc", line);
        else _log.Info("frpc", line);
    }

    /// <summary>结束当前 frpc 进程（连同它可能派生的子进程）。</summary>
    private void KillCurrent()
    {
        Process? p = _proc;
        _proc = null;
        Connected = false;

        if (p is null) return;

        try
        {
            if (!p.HasExited) p.Kill(entireProcessTree: true);
        }
        catch
        {
            // 已经退出或没权限：尽力而为，不因清理失败阻断停机
        }
    }

    /// <summary>
    /// 清掉可能残留的 frpc 进程。
    /// </summary>
    /// <remarks>
    /// WebMaster 被任务管理器强杀时，StopAsync 不会执行，frpc 会活下来并继续
    /// 占用服务器上的 remotePort。下次启动的那一份连上去会被 frps 以
    /// "port already used" 拒绝，而界面上只显示"断开重连中"，
    /// 根因完全看不出来——所以启动时先扫一遍。
    /// <para>
    /// 只杀<b>本目录下</b>那个 frpc：机器上可能还跑着用户自己的 frpc 干别的事，
    /// 按名字无差别杀会误伤。
    /// </para>
    /// </remarks>
    private void KillOrphans()
    {
        string mine = WebTunnelSettingsStore.FrpcPath;

        foreach (Process p in Process.GetProcessesByName("frpc"))
        {
            try
            {
                // MainModule 在 32/64 位不匹配或权限不足时会抛，那种进程一律不动
                string? path = p.MainModule?.FileName;
                if (path is null) continue;

                if (string.Equals(path, mine, StringComparison.OrdinalIgnoreCase))
                {
                    p.Kill(entireProcessTree: true);
                    _log.Warn("Tunnel", "清理了上一次残留的 frpc 进程（PID " + p.Id + "）");
                }
            }
            catch
            {
                // 拿不到路径就不敢动它——宁可漏杀，不可误杀别人的进程
            }
            finally
            {
                p.Dispose();
            }
        }
    }
}
