using System;
using System.Windows;
using System.Windows.Controls;

namespace CommunicationKernel.UI.Wpf.Views.Pages.MesMonitor.Controls {
    /// <summary>
    /// 告警列表面板（MesAlarmPanel.xaml 的交互逻辑）。
    /// 作为告警弹窗的默认子面板，通过 CloseRequested/DetailRequested 事件将关闭/查看详情的意图
    /// 交给宿主（DataMonitorPage）处理页面切换。
    /// </summary>
    public partial class MesAlarmPanel : UserControl {
        public MesAlarmPanel () {
            InitializeComponent();
        }

        /// <summary>请求关闭整个告警弹窗。</summary>
        public event Action CloseRequested;

        /// <summary>关闭按钮点击：转发 CloseRequested 事件。</summary>
        private void BtnClose_Click (object sender, RoutedEventArgs e) {
            CloseRequested?.Invoke();
        }

        /// <summary>请求切换到告警详情子面板。</summary>
        public event Action DetailRequested;

        /// <summary>查看详情按钮点击：转发 DetailRequested 事件。</summary>
        private void BtnDetail_Click (object sender, RoutedEventArgs e) {
            DetailRequested?.Invoke();
        }
    }

}
