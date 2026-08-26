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
    internal const string MutexName = @"Local\CommunicationKernel.UI.WebMaster";
    internal const string ShowEventName = @"Local\CommunicationKernel.UI.WebMaster.Show";

    private readonly IHostApplicationLifetime _lifetime;
    private readonly IServer _server;
    private readonly AppLogStore _logs;
    private readonly EventWaitHandle _showEvent;

    private Thread? _sta;
    private NotifyIcon? _icon;
    private TrayLogForm? _logForm;
    private Icon? _iconHandle;
    private volatile bool _stopping;

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

    public void Dispose()
    {
        _iconHandle?.Dispose();
        _iconHandle = null;
    }

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

    private void OpenUi() => LanAccess.OpenBrowser(_server);

    private void ShowLogs()
    {
        if (_logForm is null || _logForm.IsDisposed)
            _logForm = new TrayLogForm(_logs, () => LanAccess.OpenBrowser(_server, "/log"));
        _logForm.Show();
        _logForm.WindowState = FormWindowState.Normal;
        _logForm.Activate();
    }

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

    private void Exit()
    {
        _stopping = true;
        if (_icon is not null)
            _icon.Visible = false;
        _lifetime.StopApplication();
        Application.Exit();
    }

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

    private static Icon LoadIcon()
    {
        string path = Path.Combine(AppContext.BaseDirectory, "wwwroot", "app.ico");
        if (File.Exists(path))
            return new Icon(path);
        return SystemIcons.Application;
    }
}
