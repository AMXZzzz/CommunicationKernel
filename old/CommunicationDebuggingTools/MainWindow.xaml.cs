using CommunicationDebuggingTools.Core.Logging;
using Microsoft.Extensions.DependencyInjection;
using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace CommunicationDebuggingTools {

    /// <summary>
    /// 主窗口：无边框布局 + 侧栏导航。
    /// 页面通过 IServiceProvider 创建——支持构造注入，无需 Activator.CreateInstance。
    /// </summary>
    public partial class MainWindow : Window {

        private readonly IServiceProvider _services;

        public MainWindow (IServiceProvider services) {
            _services = services ?? throw new ArgumentNullException(nameof(services));
            InitializeComponent();

            if (navSidebar != null)
                navSidebar.NavigateRequested += NavigateTo;

            Loaded += MainWindow_Loaded;
            Closed += MainWindow_Closed;

            // 默认页：MES 监控
            NavigateTo(typeof(Views.Pages.Monitor.DataMonitorPage));
        }

        /// <summary>
        /// 从 DI 容器解析页面实例并导航。
        /// 支持构造注入（DevicePage、VariableConfigPage、LogPage 各有 VM 注入）。
        /// </summary>
        private void NavigateTo (Type pageType) {
            if (MainFrame == null || pageType == null) return;
            if (!typeof(Page).IsAssignableFrom(pageType)) return;
            try {
                Page page = _services.GetRequiredService(pageType) as Page;
                if (page != null)
                    MainFrame.Navigate(page);
            } catch (Exception ex) {
                try {
                    (_services.GetService(typeof(IAppLogger)) as IAppLogger)
                        ?.Error("Nav", "打开页面失败: " + pageType.Name, ex);
                } catch { }
                MessageBox.Show("打开页面失败:\n" + ex.Message, "导航错误");
            }
        }

        private void MainWindow_Loaded (object sender, RoutedEventArgs e) {
            App.RemoteConnectionChanged += OnRemoteConnectionChanged;
            UpdateRemoteStatus(App.IsRemoteConnected);
        }

        private void MainWindow_Closed (object sender, EventArgs e) {
            App.RemoteConnectionChanged -= OnRemoteConnectionChanged;
        }

        private void OnRemoteConnectionChanged (bool connected) {
            Dispatcher.Invoke(() => UpdateRemoteStatus(connected));
        }

        private void UpdateRemoteStatus (bool connected) {
            if (txtStatus != null) txtStatus.Text = connected ? "EngineHost在线" : "EngineHost离线";
            if (statusIndicator != null) {
                statusIndicator.Fill = connected
                    ? Brushes.LimeGreen
                    : (FindResource("SF.Brush.Text.Secondary") as Brush);
            }
        }

        // ── 无边框窗口控件 ────────────────────────────

        private void Window_MouseLeftButtonDown (object sender, MouseButtonEventArgs e) {
            if (e.ButtonState == MouseButtonState.Pressed) DragMove();
        }

        private void BtnLanguage_Click (object sender, RoutedEventArgs e) { /* 预留 */ }

        private void BtnFullscreen_Click (object sender, RoutedEventArgs e) {
            if (WindowState != WindowState.Maximized) {
                WindowState = WindowState.Maximized;
                btnFullscreen.Content = "退出全屏";
            } else {
                WindowState = WindowState.Normal;
                btnFullscreen.Content = "全屏";
            }
        }

        private void BtnMinimize_Click (object sender, RoutedEventArgs e) =>
            WindowState = WindowState.Minimized;

        private void BtnMaximize_Click (object sender, RoutedEventArgs e) =>
            WindowState = WindowState == WindowState.Maximized
                ? WindowState.Normal : WindowState.Maximized;

        private void BtnClose_Click (object sender, RoutedEventArgs e) => Close();
    }
}