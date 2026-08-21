using System;
using System.Windows.Controls;
using System.Windows.Input;

namespace CommunicationDebuggingTools.Views.Pages.MESMonitor.Controls {
    /// <summary>
    /// 产线控制栏控件（MesLineControlBar.xaml 的交互逻辑）。
    /// 包含告警入口，点击后通过 AlarmClickedEvent 通知宿主弹出告警列表面板。
    /// </summary>
    public partial class MesLineControlBar : UserControl {
        public MesLineControlBar () {
            InitializeComponent();
        }

        /// <summary>点击告警入口时触发，用于通知宿主弹出告警列表。</summary>
        public event Action AlarmClickedEvent;

        /// <summary>告警图标区域点击：转发 AlarmClickedEvent 事件。</summary>
        private void AlarmPanel_Click (object sender, MouseButtonEventArgs e) {
            AlarmClickedEvent?.Invoke();
        }

    }
}
