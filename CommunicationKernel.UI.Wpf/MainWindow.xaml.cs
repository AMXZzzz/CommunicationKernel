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

    // -------------------------------------------------------------------------
    // 构造函数
    // -------------------------------------------------------------------------

    /// <param name="services">从 App DI 容器注入，用于获取 Page 实例。</param>
    public MainWindow(IServiceProvider services) {
        _services = services ?? throw new ArgumentNullException(nameof(services));
        InitializeComponent();

        // 将 NavSidebar 的导航事件绑定到本窗口的 NavigateTo 方法
        if (navSidebar != null)
            navSidebar.NavigateRequested += NavigateTo;

        Loaded += MainWindow_Loaded;
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
    /// 窗口关闭时无需手动取消——进程退出后台任务自然结束。
    /// </summary>
    private void StartHealthPolling(EngineHostGrpcClient client) {
        // 在线程池后台运行，不阻塞 UI 线程
        Task.Run(async () => {
            while (true) {
                try {
                    // 发起健康检查，内部已设置 5 秒超时
                    (bool ok, string ver, int routes) = await client.HealthAsync()
                        .ConfigureAwait(false);

                    // 拼接顶栏状态文字
                    string info = ok
                        ? string.Format("v{0}  路由: {1}", ver, routes)
                        : string.Empty;

                    // 切回 UI 线程更新状态灯（UpdateConnectionStatus 内部已做 Dispatcher.InvokeAsync）
                    UpdateConnectionStatus(ok, info);
                } catch (Exception) {
                    // 网络异常时标记为离线，不中断轮询
                    UpdateConnectionStatus(false);
                }

                // 每 10 秒检查一次，避免频繁 gRPC 调用
                await Task.Delay(TimeSpan.FromSeconds(10)).ConfigureAwait(false);
            }
        });
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
