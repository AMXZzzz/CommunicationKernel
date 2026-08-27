// -----------------------------------------------------------------------------
// 文件: Services/TrayHost.cs
// 层级: UI 层 — WebMaster 托盘驻留
// 作用: 在右下角放一个图标。关浏览器不等于退出；退出、打开界面、看日志都走这里。
// -----------------------------------------------------------------------------

using System.Runtime.Versioning;
using System.Windows.Forms;
using Microsoft.AspNetCore.Hosting.Server;

namespace CommunicationKernel.UI.WebMaster.Services;

/// <summary>
/// Windows 托盘。后台 STA 线程跑消息循环，不挡住 Kestrel。
/// </summary>
[SupportedOSPlatform("windows")]
internal sealed class TrayHost : IHostedService, IDisposable
{
    /// <summary>
    /// 单实例互斥体名。<c>Local\</c> 前缀表示仅在本登录会话内唯一。
    /// </summary>
    /// <remarks>
    /// 刻意不用 <c>Global\</c>：多用户远程桌面场景下，全局互斥会让第二个用户
    /// 根本起不来，而他们本该各跑各的（各自的端口、各自的 config 目录）。
    /// </remarks>
    internal const string MutexName = @"Local\CommunicationKernel.UI.WebMaster";

    /// <summary>
    /// 「请把界面打开」的跨进程信号名。
    /// </summary>
    /// <remarks>
    /// 再次双击 exe 时，新进程发现互斥体已被占用，就置位本事件后自行退出，
    /// 由已在运行的实例把浏览器打开——而不是再起一份去抢同一个端口。
    /// </remarks>
    internal const string ShowEventName = @"Local\CommunicationKernel.UI.WebMaster.Show";

    /// <summary>应用生命周期，用于「退出」菜单停机与监听启动完成。</summary>
    private readonly IHostApplicationLifetime _lifetime;
    /// <summary>Kestrel 服务器，用于取实际绑定的地址（端口可能被配置改过）。</summary>
    private readonly IServer _server;
    /// <summary>日志缓冲，托盘的「查看日志」窗口直接读它。</summary>
    private readonly AppLogStore _logs;
    /// <summary>见 <see cref="ShowEventName"/>。由 Program.cs 创建后注入。</summary>
    private readonly EventWaitHandle _showEvent;

    /// <summary>承载 WinForms 消息循环的 STA 线程。</summary>
    private Thread? _sta;
    /// <summary>托盘图标本体。只能在 STA 线程上操作。</summary>
    private NotifyIcon? _icon;
    /// <summary>日志窗口，按需创建并复用；被用户关掉后会是 Disposed 状态。</summary>
    private TrayLogForm? _logForm;
    /// <summary>图标句柄。NotifyIcon 不负责释放它，必须自己留着在 Dispose 里放掉。</summary>
    private Icon? _iconHandle;
    /// <summary>
    /// 是否正在停机。volatile：由 STA 线程与信号监听线程共同读写。
    /// </summary>
    /// <remarks>
    /// 停机时必须让 <see cref="WatchShowSignal"/> 尽快退出，
    /// 否则它会在已释放的事件句柄上继续等待。
    /// </remarks>
    private volatile bool _stopping;

    /// <param name="lifetime">应用生命周期。</param>
    /// <param name="server">Kestrel 服务器，用于取实际监听地址。</param>
    /// <param name="logs">日志缓冲。</param>
    /// <param name="showEvent">跨进程唤起信号，由 Program.cs 创建。</param>
    public TrayHost(
        IHostApplicationLifetime lifetime,
        IServer server,
        AppLogStore logs,
        EventWaitHandle showEvent)
    {
        _lifetime = lifetime;
        _server = server;
        _logs = logs;
        _showEvent = showEvent;
    }

    /// <summary>
    /// 起 STA 线程跑 WinForms 消息循环，并开始监听跨进程唤起信号。
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>必须是独立的 STA 线程。</b>WinForms 的消息循环会阻塞所在线程，
    /// 跑在主线程上 Kestrel 就起不来；而 NotifyIcon 又要求 STA 单元。
    /// </para>
    /// <para>
    /// 立即返回已完成的 Task：IHostedService.StartAsync 会阻塞应用启动流程。
    /// </para>
    /// </remarks>
    public Task StartAsync(CancellationToken cancellationToken)
    {
        _sta = new Thread(RunMessageLoop)
        {
            IsBackground = true,
            Name = "WebMaster.Tray",
        };
        _sta.SetApartmentState(ApartmentState.STA);
        _sta.Start();

        _ = Task.Run(() => WatchShowSignal(cancellationToken), cancellationToken);
        return Task.CompletedTask;
    }

    /// <summary>
    /// 隐藏并释放托盘图标，结束消息循环。
    /// </summary>
    /// <remarks>
    /// 先置 _stopping 再置位事件，把 WatchShowSignal 从等待里唤醒并让它退出。
    /// 图标操作要回到 STA 线程，因此判断 InvokeRequired——
    /// 跨线程碰 NotifyIcon 在某些 Windows 版本上会留下一个永不消失的僵尸图标。
    /// </remarks>
    public Task StopAsync(CancellationToken cancellationToken)
    {
        _stopping = true;
        try { _showEvent.Set(); } catch { }
        try
        {
            if (_icon is not null)
            {
                void Hide()
                {
                    if (_icon is not null)
                    {
                        _icon.Visible = false;
                        _icon.Dispose();
                        _icon = null;
                    }
                    Application.Exit();
                }

                if (_icon.ContextMenuStrip is not null && _icon.ContextMenuStrip.InvokeRequired)
                    _icon.ContextMenuStrip.BeginInvoke(Hide);
                else
                    Hide();
            }
        }
        catch
        {
            // 进程正在退出，托盘资源最大努力释放
        }

        return Task.CompletedTask;
    }

    /// <summary>释放图标句柄。NotifyIcon 不接管它的所有权，必须自己放。</summary>
    public void Dispose()
    {
        _iconHandle?.Dispose();
        _iconHandle = null;
    }

    /// <summary>STA 线程主体：建图标与菜单，然后进入消息循环直到 Application.Exit。</summary>
    private void RunMessageLoop()
    {
        Application.EnableVisualStyles();
        Application.SetCompatibleTextRenderingDefault(false);

        _iconHandle = LoadIcon();
        ContextMenuStrip menu = BuildMenu();

        _icon = new NotifyIcon
        {
            Icon = _iconHandle,
            Visible = true,
            Text = "CommunicationKernel Web",
            ContextMenuStrip = menu,
        };
        _icon.DoubleClick += (_, _) => OpenUi();

        _lifetime.ApplicationStarted.Register(OnStarted);

        Application.Run();
    }

    /// <summary>
    /// 监听就绪后更新悬停提示并弹一次气球。
    /// </summary>
    /// <remarks>
    /// 必须等 ApplicationStarted：此前 IServer 还没有绑定地址，取到的是空集合。
    /// 那句气球提示是有必要的——不说明的话，用户关掉浏览器会以为程序已经退出。
    /// </remarks>
    private void OnStarted()
    {
        if (_icon is null) return;
        try
        {
            string url = LanAccess.LocalBrowserUrl(_server) ?? "http://localhost:64000";
            _icon.Text = "CommunicationKernel Web\n" + url;
            _icon.BalloonTipTitle = "Web 上位机已在托盘运行";
            _icon.BalloonTipText = "关闭浏览器不会退出。右键图标可打开界面、查看日志或退出。";
            _icon.BalloonTipIcon = ToolTipIcon.Info;
            _icon.ShowBalloonTip(4000);
        }
        catch
        {
            // 气球提示失败不影响驻留
        }
    }

    /// <summary>构造右键菜单：打开界面 / 查看日志 / 复制访问地址 / 退出。</summary>
    private ContextMenuStrip BuildMenu()
    {
        ContextMenuStrip menu = new();
        menu.Items.Add("打开界面", null, (_, _) => OpenUi());
        menu.Items.Add("查看日志", null, (_, _) => ShowLogs());
        menu.Items.Add("复制访问地址", null, (_, _) => CopyUrl());
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("退出", null, (_, _) => Exit());
        return menu;
    }

    /// <summary>用默认浏览器打开本机界面。</summary>
    private void OpenUi() => LanAccess.OpenBrowser(_server);

    /// <summary>
    /// 打开托盘日志窗口。
    /// </summary>
    /// <remarks>
    /// 判 IsDisposed 后重建：用户点过关闭的窗体已被释放，直接 Show 会抛异常。
    /// 这个窗口的意义在于「浏览器打不开时还能看日志」——
    /// 端口冲突、静态文件缺失这类故障恰恰会让网页方案失灵。
    /// </remarks>
    private void ShowLogs()
    {
        if (_logForm is null || _logForm.IsDisposed)
            _logForm = new TrayLogForm(_logs, () => LanAccess.OpenBrowser(_server, "/log"));
        _logForm.Show();
        _logForm.WindowState = FormWindowState.Normal;
        _logForm.Activate();
    }

    /// <summary>把访问地址复制到剪贴板，便于发给同事或在手机上打开。</summary>
    private void CopyUrl()
    {
        try
        {
            string url = LanAccess.LocalBrowserUrl(_server) ?? "http://localhost:64000";
            Clipboard.SetText(url);
            if (_icon is not null)
            {
                _icon.BalloonTipTitle = "已复制";
                _icon.BalloonTipText = url;
                _icon.BalloonTipIcon = ToolTipIcon.Info;
                _icon.ShowBalloonTip(2000);
            }
        }
        catch
        {
            // 剪贴板被占用时忽略
        }
    }

    /// <summary>
    /// 退出应用。
    /// </summary>
    /// <remarks>
    /// 先隐藏图标再停应用：反过来的话，停机耗时期间托盘还留着一个点不动的图标，
    /// 用户会以为没退成而反复点击。
    /// </remarks>
    private void Exit()
    {
        _stopping = true;
        if (_icon is not null)
            _icon.Visible = false;
        _lifetime.StopApplication();
        Application.Exit();
    }

    /// <summary>
    /// 监听跨进程唤起信号：再次双击 exe 时把界面打开。
    /// </summary>
    /// <remarks>
    /// 用 500ms 轮询而非无限等待，是为了能及时响应 _stopping 与取消令牌；
    /// 无限等待时停机要靠 StopAsync 置位事件才能唤醒，多一条依赖就多一处能卡住的地方。
    /// </remarks>
    private void WatchShowSignal(CancellationToken ct)
    {
        // 再双击一次 exe：已在跑的实例把界面打开，而不是再起一份去抢端口
        while (!ct.IsCancellationRequested && !_stopping)
        {
            try
            {
                if (_showEvent.WaitOne(500))
                {
                    if (_stopping || ct.IsCancellationRequested) return;
                    if (_icon?.ContextMenuStrip is { IsHandleCreated: true } menu)
                        menu.BeginInvoke(OpenUi);
                    else
                        OpenUi();
                }
            }
            catch (ObjectDisposedException)
            {
                return;
            }
        }
    }

    /// <summary>加载托盘图标，缺文件时回落到系统默认图标而不是崩掉。</summary>
    private static Icon LoadIcon()
    {
        string path = Path.Combine(AppContext.BaseDirectory, "wwwroot", "app.ico");
        if (File.Exists(path))
            return new Icon(path);
        return SystemIcons.Application;
    }
}
