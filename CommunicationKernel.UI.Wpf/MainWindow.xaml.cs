#nullable disable

// -----------------------------------------------------------------------------
// 文件: MainWindow.xaml.cs
// 层级: UI 层 — WPF 主窗口 code-behind
// 作用: 管理 NavSidebar → Frame 导航；实现无边框窗口拖动/最大化/关闭；
//       订阅 GrpcDeviceService 连接状态更新顶栏指示灯。
// 调用链:
//   navSidebar.NavigateRequested → NavigateTo(pageType)
//     → DI.GetRequiredService(pageType) → MainFrame.Navigate(page)
// -----------------------------------------------------------------------------

using System;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using CommunicationKernel.UI.Wpf.Core.Logging;
using CommunicationKernel.UI.Wpf.Services;
using Microsoft.Extensions.DependencyInjection;

namespace CommunicationKernel.UI.Wpf;

/// <summary>
/// 主窗口 code-behind：只负责纯 UI 行为（导航、窗口控制、状态灯更新）。
/// 业务逻辑均在 ViewModel / Service 层。
/// </summary>
public partial class MainWindow : Window {

    // -------------------------------------------------------------------------
    // 私有字段
    // -------------------------------------------------------------------------

    /// <summary>DI 服务提供者，用于懒解析页面实例。</summary>
    private readonly IServiceProvider _services;

    /// <summary>应用日志记录器，可为 null（此时不记录日志）。</summary>
    private readonly IAppLogger _log;

    /// <summary>健康轮询任务的取消源，窗口关闭时触发取消。</summary>
    private readonly CancellationTokenSource _healthCts = new CancellationTokenSource();

    // -------------------------------------------------------------------------
    // 构造函数
    // -------------------------------------------------------------------------

    /// <param name="services">从 App DI 容器注入，用于获取 Page 实例。</param>
    public MainWindow(IServiceProvider services) {
        _services = services ?? throw new ArgumentNullException(nameof(services));

        // 日志器为可选依赖：取不到时降级为不记录，不阻断窗口构造
        _log = services.GetService<IAppLogger>();

        InitializeComponent();

        // 将 NavSidebar 的导航事件绑定到本窗口的 NavigateTo 方法
        if (navSidebar != null)
            navSidebar.NavigateRequested += NavigateTo;

        Loaded  += MainWindow_Loaded;
        Closed  += MainWindow_Closed;
    }

    /// <summary>窗口关闭：取消健康轮询，避免后台任务在应用退出后继续运行。</summary>
    private void MainWindow_Closed(object sender, EventArgs e) {
        try {
            _healthCts.Cancel();
            _healthCts.Dispose();
        } catch {
            // 关闭阶段的异常静默处理，不阻塞退出流程
        }
    }

    // -------------------------------------------------------------------------
    // 初始化
    // -------------------------------------------------------------------------

    /// <summary>窗口加载完成后：导航到默认首页，并启动 EngineHost 健康轮询。</summary>
    private void MainWindow_Loaded(object sender, RoutedEventArgs e) {
        // 默认显示 MES 监控页（与 NavSidebar 默认选中项一致）
        NavigateTo(typeof(Views.Pages.MesMonitor.DataMonitorPage));

        // 获取 gRPC 客户端并启动后台健康检查轮询
        EngineHostGrpcClient client = _services.GetService<EngineHostGrpcClient>();
        if (client != null)
            StartHealthPolling(client);
    }

    // -------------------------------------------------------------------------
    // 健康检查轮询
    // -------------------------------------------------------------------------

    /// <summary>
    /// 在后台循环调用 HealthAsync，每 10 秒一次，结果更新顶栏指示灯。
    /// 窗口关闭时通过 <see cref="_healthCts"/> 取消，任务随之结束。
    /// </summary>
    /// <remarks>
    /// 必须可取消：无取消的 while(true) 会在应用退出后继续存活，
    /// 向已释放的 gRPC 通道发请求、向已关闭的 Dispatcher 排队回调。
    /// </remarks>
    private void StartHealthPolling(EngineHostGrpcClient client) {
        CancellationToken ct = _healthCts.Token;

        // 在线程池后台运行，不阻塞 UI 线程
        Task.Run(async () => {
            while (!ct.IsCancellationRequested) {
                try {
                    // 发起健康检查，内部已设置 5 秒截止时间
                    (bool ok, string ver, int routes) = await client.HealthAsync(ct)
                        .ConfigureAwait(false);

                    // 拼接顶栏状态文字
                    string info = ok
                        ? string.Format("v{0}  路由: {1}", ver, routes)
                        : string.Empty;

                    UpdateConnectionStatus(ok, info);
                } catch (OperationCanceledException) {
                    // 窗口关闭触发的取消：正常退出
                    return;
                } catch (Exception ex) {
                    // 网络异常时标记为离线，不中断轮询
                    _log?.Warn("Host", "健康检查失败: " + ex.Message);
                    UpdateConnectionStatus(false);
                }

                // 每 10 秒检查一次，避免频繁 gRPC 调用
                try {
                    await Task.Delay(TimeSpan.FromSeconds(10), ct).ConfigureAwait(false);
                } catch (OperationCanceledException) {
                    return;
                }
            }
        }, ct);
    }

    // -------------------------------------------------------------------------
    // 导航
    // -------------------------------------------------------------------------

    /// <summary>
    /// 从 DI 容器解析 Page 实例并让 Frame 导航到该页。
    /// 支持构造注入（DevicePage、LogPage 等有 ViewModel 注入）。
    /// </summary>
    /// <param name="pageType">目标页面 Type，必须继承 Page 并在 DI 中注册。</param>
    private void NavigateTo(Type pageType) {
        // 防空检查
        if (MainFrame == null || pageType == null) return;

        // 只接受 Page 子类
        if (!typeof(Page).IsAssignableFrom(pageType)) return;

        try {
            // 从 DI 容器解析页面（支持构造注入）
            object resolved = _services.GetRequiredService(pageType);
            Page page = resolved as Page;
            if (page != null)
                MainFrame.Navigate(page);
        } catch (Exception ex) {
            // 导航失败显示错误对话框，不崩溃整个应用
            MessageBox.Show("打开页面失败:\n" + ex.Message, "导航错误",
                MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    /// <summary>
    /// 每次导航完成后清空返回栈。
    /// 本应用页面注册为 Transient，导航即新建实例；若保留日志，
    /// Frame 会持有全部历史页面实例导致无限累积（页面又持有 ViewModel 订阅）。
    /// 界面本身也没有前进/后退入口，日志无使用价值。
    /// </summary>
    private void MainFrame_Navigated(object sender, System.Windows.Navigation.NavigationEventArgs e) {
        if (MainFrame == null) return;

        // 清空返回/前进栈，释放对历史 Page 实例的引用
        while (MainFrame.CanGoBack)
            MainFrame.RemoveBackEntry();
    }

    // -------------------------------------------------------------------------
    // 顶栏状态更新（公开，供外部服务调用）
    // -------------------------------------------------------------------------

    /// <summary>更新顶栏连接指示灯和状态文字。</summary>
    /// <param name="connected">是否在线。</param>
    /// <param name="info">状态补充文字（版本号或离线原因）。</param>
    public void UpdateConnectionStatus(bool connected, string info = "") {
        // 必须在 UI 线程执行，允许从后台线程调用
        Dispatcher.InvokeAsync(() => {
            if (connected) {
                // 在线：绿色指示灯
                statusIndicator.Fill = new SolidColorBrush(Color.FromRgb(0x4E, 0xC9, 0xB0));
                txtStatus.Text = "EngineHost在线";
                txtCurrentDevice.Text = string.IsNullOrEmpty(info) ? "EngineHost在线" : info;
            } else {
                // 离线：灰色指示灯
                statusIndicator.Fill = (Brush)FindResource("SF.Brush.Text.Secondary");
                txtStatus.Text = "未连接";
                txtCurrentDevice.Text = "EngineHost离线";
            }
        });
    }

    // -------------------------------------------------------------------------
    // 无边框窗口控制
    // -------------------------------------------------------------------------

    /// <summary>标题栏拖动：鼠标左键拖拽移动无边框窗口。</summary>
    private void Window_MouseLeftButtonDown(object sender, MouseButtonEventArgs e) {
        // 按住鼠标移动窗口
        if (e.ButtonState == MouseButtonState.Pressed)
            DragMove();
    }

    /// <summary>最小化按钮。</summary>
    private void BtnMinimize_Click(object sender, RoutedEventArgs e)
        => WindowState = WindowState.Minimized;

    /// <summary>最大化/还原按钮。</summary>
    private void BtnMaximize_Click(object sender, RoutedEventArgs e) {
        // 当前最大化 → 还原；当前正常 → 最大化
        if (WindowState == WindowState.Maximized) {
            WindowState = WindowState.Normal;
            btnMaximize.Content = "□";
        } else {
            WindowState = WindowState.Maximized;
            btnMaximize.Content = "❐";
        }
    }

    /// <summary>关闭按钮。</summary>
    private void BtnClose_Click(object sender, RoutedEventArgs e)
        => Close();
}
