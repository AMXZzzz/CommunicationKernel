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

    /// <summary>
    /// 状态或连接标志变化时触发，供界面重绘。
    /// </summary>
    /// <remarks>
    /// 没有本事件时，设置页只在打开那一刻读一次 <see cref="Status"/>，
    /// 之后停在那个快照上——隧道明明已经连上，界面却一直显示「连接中」，
    /// 让人以为没通而去反复排查一个并不存在的故障。
    /// <para>
    /// <b>在后台线程上触发</b>（frpc 输出回调、监管循环），
    /// 订阅方必须自行切回自己的同步上下文再碰 UI。
    /// </para>
    /// </remarks>
    public event Action? Changed;

    /// <summary>
    /// 改写状态并在确有变化时通知订阅方。
    /// </summary>
    /// <remarks>
    /// 所有状态改动都必须走这里，不要直接赋值给两个属性——
    /// 散落的赋值点迟早会漏掉通知，表现为界面偶尔卡在旧状态上，极难复现。
    /// <para>
    /// 值没变就不通知：frpc 每次重连都会重复报同样的状态，
    /// 无条件触发会让界面空转重绘。
    /// </para>
    /// </remarks>
    /// <param name="connected">隧道是否已连上。</param>
    /// <param name="status">状态描述。</param>
    private void SetState(bool connected, string status)
    {
        if (Connected == connected && Status == status) return;

        Connected = connected;
        Status = status;

        // 订阅方（界面）抛异常不能连累 frpc 托管——那是两件不相干的事
        try { Changed?.Invoke(); } catch { }
    }

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
            SetState(false, "未找到 frpc.exe");
            _log.Warn("Tunnel",
                "已启用内网穿透，但 exe 目录下没有 " + WebTunnelSettingsStore.FrpcFileName +
                "。请自行下载后放入，再重启。");
            return Task.CompletedTask;
        }

        if (!_settings.IsComplete)
        {
            SetState(false, "配置不完整");
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
            SetState(false, "写配置失败");
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

        // 登录失败不退出，交给 frpc 自己按退避重试。
        //
        // 默认 loginFailExit = true：连不上就整个进程退出，于是变成
        // 「本类拉起 → 10 秒超时 → 进程退出 → 等 5 秒 → 再拉起」的循环，
        // 每轮都往日志里灌一整段启动横幅，真正有用的那行错误被淹掉。
        // 服务器防火墙没放行、域名还没生效这类情况可能持续几分钟到几小时，
        // 那段时间日志会被刷得没法看。
        //
        // 交给 frpc 自己重试后：进程一直在，只在每次尝试失败时打一行，
        // 且它的退避比我们固定 5 秒更合理。本类的监管循环仍然保留——
        // 那是给「进程真的崩了」兜底的，与登录失败是两回事。
        sb.AppendLine("loginFailExit = false");
        sb.AppendLine();
        sb.AppendLine("[[proxies]]");
        sb.AppendLine($"name = \"{ProxyName()}\"");
        sb.AppendLine("type = \"tcp\"");
        sb.AppendLine("localIP = \"127.0.0.1\"");
        sb.AppendLine($"localPort = {_localPort}");
        sb.AppendLine($"remotePort = {_settings.RemotePort}");

        File.WriteAllText(path, sb.ToString());
    }

    /// <summary>生成的 frpc 配置路径，与其它配置同放 config/。</summary>
    private static string ConfigPath => Path.Combine(WebPaths.Root, "frpc.toml");

    /// <summary>
    /// 本机的代理名，形如 <c>ck-web-WORKSHOP01</c>。
    /// </summary>
    /// <remarks>
    /// 代理名在一台 frps 上必须全局唯一。此前写死为 <c>ck-web</c>，
    /// 于是同一台 frps 只能接一台 WebMaster：第二台登录能成功，
    /// 注册代理时被拒（<c>proxy [ck-web] already exists</c>），
    /// 而界面上只显示「已连接」，根因完全看不出来。
    /// <para>
    /// 带上机器名即可各不相同。注意<b>远端口仍需各机不同</b>，
    /// 且 frps 的 <c>allowPorts</c> 要放开相应范围——名字唯一只解决了一半。
    /// </para>
    /// </remarks>
    /// <returns>只含字母、数字、连字符的代理名。</returns>
    private static string ProxyName()
    {
        string host;
        try { host = Environment.MachineName; }
        catch { host = string.Empty; }

        // frp 的代理名不接受任意字符，中文机器名尤其常见。
        // 逐字符过滤而不是整体校验：留下能用的部分，比因为一个字符就整个放弃要好。
        StringBuilder safe = new();
        foreach (char c in host)
        {
            if (char.IsAsciiLetterOrDigit(c)) safe.Append(c);
            else if (c is '-' or '_') safe.Append('-');
        }

        // 机器名全是中文时上面会过滤成空串，退回原来的固定名——
        // 那种情况下多机部署仍会冲突，但至少不会生成一个非法的 name 让 frpc 起不来。
        return safe.Length > 0 ? "ck-web-" + safe : "ck-web";
    }

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
                SetState(false, "启动失败");
                _log.Error("Tunnel", "frpc 启动失败: " + ex.Message);
            }

            if (ct.IsCancellationRequested) return;

            SetState(false, "已断开，重连中");

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
        SetState(false, "已启动，连接中");
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

        if (line.Contains("start proxy success", StringComparison.OrdinalIgnoreCase))
        {
            SetState(true, "隧道就绪");
        }
        else if (line.Contains("start error", StringComparison.OrdinalIgnoreCase))
        {
            // 代理注册失败：登录成功 ≠ 隧道可用。
            //
            // 这两件事是分开的：先向 frps 登录（认证 token），再注册代理（占远端口）。
            // 第二步失败时界面若停留在「已连接」，会让人以为一切正常，
            // 转头去查 Nginx 或浏览器——而真正的问题在这里，且日志里那行
            // 混在 frpc 的输出中很容易被划过去。
            SetState(false, line.Contains("already exists", StringComparison.OrdinalIgnoreCase)
                ? "代理名冲突：同一台 frps 上已有别的 WebMaster"
                : "未建立（代理注册失败，见日志）");
        }
        else if (line.Contains("login to server success", StringComparison.OrdinalIgnoreCase))
        {
            // 只代表认证通过，代理还没注册。真正可用要等 start proxy success
            SetState(true, "已连接");
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

        // 保留原状态文案，只把"已连接"降下来：
        // 本方法在停机与重启两条路径上都会走，覆盖成固定文案会把
        // 「已断开，重连中」这类更有信息量的描述冲掉。
        SetState(false, Status);

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
