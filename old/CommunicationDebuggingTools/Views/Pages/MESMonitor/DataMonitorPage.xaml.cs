using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;

namespace CommunicationDebuggingTools.Views.Pages.Monitor {
    /// <summary>
    /// MES 数据监控首页：展示产线设备状态卡片，并管理告警列表/告警详情两个弹出面板之间的切换。
    /// 当前设备卡片数据为演示用静态数据，尚未接入实时 MES/PLC 数据源。
    /// </summary>
    public partial class DataMonitorPage : Page {
        /// <summary>
        /// 构造页面：初始化演示用设备卡片数据，并串联告警列表弹窗（alarmPopup）与
        /// 其内部“列表”“详情”两个子面板（alarmPanel/alarmDetail）之间的显示切换事件。
        /// </summary>
        public DataMonitorPage () {
            InitializeComponent();

            // 卡片演示数据（TODO: 后续接入真实设备状态数据源）
            card1.SetInfo("上板机", "Siemens S7-1200 · 192.168.0.10", "单轨", false);
            card1.SetStatus("RUN", "Success");

            card2.SetInfo("AOI", "Koh Young Zenith · 192.168.0.60", "双轨", true);
            card2.SetStatus("ALARM", "Error");

            card3.SetInfo("下板机", "Omron NJ · 192.168.0.70", "单轨", false);
            card3.SetStatus("离线", "Offline");

            // 点击产线控制条的告警入口，弹出告警列表面板（默认显示列表，隐藏详情）

            lineControl.AlarmClickedEvent += () =>
            {
                alarmPanel.Visibility = Visibility.Visible;
                alarmDetail.Visibility = Visibility.Collapsed;

                alarmPopup.PlacementTarget = Window.GetWindow(this);
                alarmPopup.Placement = System.Windows.Controls.Primitives.PlacementMode.Center;
                alarmPopup.IsOpen = true;
            };

            // 列表面板请求关闭：关闭整个告警弹窗
            alarmPanel.CloseRequested += () =>
            {
                alarmPopup.IsOpen = false;
            };

            // 列表面板请求查看详情：切换为详情子面板
            alarmPanel.DetailRequested += () =>
            {
                alarmPanel.Visibility = Visibility.Collapsed;
                alarmDetail.Visibility = Visibility.Visible;
            };

            // 详情面板请求返回：切换回列表子面板
            alarmDetail.BackRequested += () =>
            {
                alarmDetail.Visibility = Visibility.Collapsed;
                alarmPanel.Visibility = Visibility.Visible;
            };

            // 详情面板请求关闭：关闭整个告警弹窗
            alarmDetail.CloseRequested += () =>
            {
                alarmPopup.IsOpen = false;
            };
        }


    }
}