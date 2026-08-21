using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

namespace CommunicationDebuggingTools.Views.Pages.MESMonitor.Controls {
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
            InitializeComponent();
        }

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