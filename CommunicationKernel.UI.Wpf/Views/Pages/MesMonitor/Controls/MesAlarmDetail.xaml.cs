#nullable disable

// -----------------------------------------------------------------------------
// 文件: Views/Pages/MesMonitor/Controls/MesAlarmDetail.xaml.cs
// 层级: UI 层 — MES 监控页子控件
// 作用: 告警详情子面板；关闭/返回列表通过事件交给宿主处理。
// -----------------------------------------------------------------------------

using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

namespace CommunicationKernel.UI.Wpf.Views.Pages.MesMonitor.Controls {
    /// <summary>
    /// 告警详情子面板（MesAlarmDetail.xaml 的交互逻辑）。
    /// 从告警列表面板点击“查看详情”后切换到本面板，支持返回列表或直接关闭告警弹窗。
    /// </summary>
    public partial class MesAlarmDetail : UserControl {
        /// <summary>请求关闭整个告警弹窗。</summary>
        public event Action CloseRequested;
        /// <summary>请求返回告警列表子面板。</summary>
        public event Action BackRequested;

        public MesAlarmDetail () {
            // 解析 XAML，构建视觉树
            InitializeComponent();
        }

        // ============================================================================
        // 按钮
        // ============================================================================

        /// <summary>关闭图标点击：转发 CloseRequested 事件。</summary>
        private void BtnX_Click (object sender, MouseButtonEventArgs e) {
            CloseRequested?.Invoke();
        }

        /// <summary>返回按钮点击：转发 BackRequested 事件，回到告警列表。</summary>
        private void BtnBack_Click (object sender, RoutedEventArgs e) {
            BackRequested?.Invoke();
        }
    }
}
