#nullable disable

// -----------------------------------------------------------------------------
// 文件: MainWindow.xaml.cs
// 层级: UI 层 — WPF 主窗口 code-behind
// 作用: 管理 NavSidebar → Frame 导航；无边框窗口拖动/最大化/关闭；把会话状态画到顶栏。
// 调用链:
//   navSidebar.NavigateRequested → NavigateTo(pageType)
//     → DI.GetRequiredService(pageType) → MainFrame.Navigate(page)
//   HostSessionService.Changed → OnSessionChanged → UpdateConnectionStatus
//
// 职责边界:
//   本类只做"把状态画出来"，不管连接生命周期。
//   健康轮询曾经就写在这里，导致连接节奏、取消逻辑与窗口生命周期绑死，
//   既无法脱离窗口测试，其他页面想知道 Host 在不在线也只能反向来问窗口。
//   现已移入 Services/HostSessionService.cs，本类改为订阅它的 Changed 事件。
//
//   仍保留 IServiceProvider，但<b>仅</b>用于按 Type 解析导航目标页——
//   这是运行时才知道类型的场景，构造注入无法表达。
//   其余依赖一律走构造函数，不要再退回 GetService 取件。
// -----------------------------------------------------------------------------

using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using CommunicationKernel.UI.Wpf.Services;
using Microsoft.Extensions.DependencyInjection;

namespace CommunicationKernel.UI.Wpf;

/// <summary>
/// 主窗口 code-behind：只负责纯 UI 行为（导航、窗口控制、状态灯渲染）。
/// 业务逻辑均在 ViewModel / Service 层。
/// </summary>
public partial class MainWindow : Window {

    // ============================================================================
    // 私有字段
    // ============================================================================

    /// <summary>
    /// DI 服务提供者。
    /// 仅用于 <see cref="NavigateTo"/> 按运行时 Type 解析页面实例，不作他用。
    /// </summary>
    private readonly IServiceProvider _services;

    /// <summary>EngineHostingServiceApp 会话状态源，顶栏指示灯的唯一数据来源。</summary>
    private readonly HostSessionService _session;

    // ============================================================================
    // 构造函数
    // ============================================================================

    /// <param name="services">从 App DI 容器注入，用于按 Type 解析 Page 实例。</param>
    /// <param name="session">会话状态服务，提供在线状态与版本信息。</param>
    public MainWindow(IServiceProvider services, HostSessionService session) {
        // DI 容器必填，导航目标页均从中解析
        _services = services ?? throw new ArgumentNullException(nameof(services));

        // 会话服务必填：顶栏状态灯没有它就没有数据来源
        _session = session ?? throw new ArgumentNullException(nameof(session));

        InitializeComponent();

        // 将 NavSidebar 的导航事件绑定到本窗口的 NavigateTo 方法
        if (navSidebar != null)
            navSidebar.NavigateRequested += NavigateTo;

        // 订阅会话状态变化。注意方向：服务发布、视图订阅，
        // 服务本身不认识 MainWindow 这个类型。
        _session.Changed += OnSessionChanged;

        Loaded += MainWindow_Loaded;
        Closed += MainWindow_Closed;
    }

    /// <summary>窗口关闭：退订会话事件，避免服务持有已关闭窗口的引用。</summary>
    private void MainWindow_Closed(object sender, EventArgs e) {
        // 会话服务是单例，生命周期长于窗口；不退订会让窗口无法被回收
        _session.Changed -= OnSessionChanged;
    }

    // ============================================================================
    // 初始化
    // ============================================================================

    /// <summary>窗口加载完成后：导航到默认首页，并按当前会话状态刷新一次顶栏。</summary>
    private void MainWindow_Loaded(object sender, RoutedEventArgs e) {
        // 默认显示 MES 监控页（与 NavSidebar 默认选中项一致）
        NavigateTo(typeof(Views.Pages.MesMonitor.DataMonitorPage));

        // 主动渲染一次：轮询可能早于窗口加载完成，
        // 那次 Changed 事件本窗口没赶上，不补这一下顶栏会一直停在初始值
        OnSessionChanged();
    }

    // ============================================================================
    // 会话状态渲染
    // ============================================================================

    /// <summary>
    /// 会话状态变化时刷新顶栏。
    /// </summary>
    /// <remarks>
    /// 本方法可能在<b>后台线程</b>上被调用——<see cref="HostSessionService"/>
    /// 刻意不引用 Dispatcher，切回 UI 线程是订阅方的责任。
    /// <see cref="UpdateConnectionStatus"/> 内部已经用 InvokeAsync 包好了。
    /// </remarks>
    private void OnSessionChanged() {
        // 在线时把版本与路由数一并显示，离线时留空由下游填默认文案
        string info = _session.Online
            ? string.Format("v{0}  路由: {1}", _session.HostVersion, _session.RouteCount)
            : string.Empty;

        UpdateConnectionStatus(_session.Online, info);
    }

    // ============================================================================
    // 导航
    // ============================================================================

    /// <summary>
    /// 从 DI 容器解析 Page 实例并让 Frame 导航到该页。
    /// 支持构造注入（DevicePage、LogPage 等有 ViewModel 注入）。
    /// </summary>
    /// <param name="pageType">目标页面 Type，必须继承 Page 并在 DI 中注册。</param>
    private void NavigateTo(Type pageType) {
        // Frame 未就绪或类型为空则无法导航
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

    // ============================================================================
    // 顶栏状态更新（公开，供外部服务调用）
    // ============================================================================

    /// <summary>更新顶栏连接指示灯和状态文字。</summary>
    /// <param name="connected">是否在线。</param>
    /// <param name="info">状态补充文字（版本号或离线原因）。</param>
    public void UpdateConnectionStatus(bool connected, string info = "") {
        // 必须在 UI 线程执行，允许从后台健康轮询调用
        Dispatcher.InvokeAsync(() => {
            if (connected) {
                // 在线：绿色指示灯
                statusIndicator.Fill = new SolidColorBrush(Color.FromRgb(0x4E, 0xC9, 0xB0));
                txtStatus.Text = "EngineHostingServiceApp 在线";
                txtCurrentDevice.Text = string.IsNullOrEmpty(info) ? "EngineHostingServiceApp 在线" : info;
            } else {
                // 离线：灰色指示灯
                statusIndicator.Fill = (Brush)FindResource("SF.Brush.Text.Secondary");
                txtStatus.Text = "未连接";
                txtCurrentDevice.Text = "EngineHostingServiceApp 离线";
            }
        });
    }

    // ============================================================================
    // 无边框窗口控制
    // ============================================================================

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
