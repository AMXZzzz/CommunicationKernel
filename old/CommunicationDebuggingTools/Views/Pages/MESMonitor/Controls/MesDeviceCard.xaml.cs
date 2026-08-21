using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace CommunicationDebuggingTools.Views.Pages.MESMonitor.Controls {
    /// <summary>
    /// MES 监控页上的设备状态展示卡片（仅用于 MES 监控页面的演示展示，
    /// 与 Device 页面使用的 DeviceCard 为两个独立控件）。目前通过 SetInfo/SetStatus 方法手动设置内容，
    /// 尚未接入业务数据绑定。
    /// </summary>
    public partial class MesDeviceCard : UserControl {
        public MesDeviceCard () {
            InitializeComponent();
        }

        /// <summary>
        /// 设置卡片状态彽徽（文字与颜色）。
        /// </summary>
        /// <param name="statusText">状态显示文本，如 "RUN"/"ALARM"/"离线"。</param>
        /// <param name="statusType">状态类型标识："Success" 运行中、"Error" 告警、其他视为离线。</param>
        public void SetStatus (string statusText, string statusType) {
            txtStatus.Text = statusText;

            Brush bg;
            Brush fg;

            switch (statusType) {
                case "Success": // RUN
                    bg = (Brush)FindResource("SF.Brush.Mes.LiveBg");
                    fg = (Brush)FindResource("SF.Brush.Mes.LiveFg");
                    break;
                case "Error":   // ALARM
                    bg = (Brush)FindResource("SF.Brush.Mes.AlarmBg");
                    fg = (Brush)FindResource("SF.Brush.Mes.AlarmFg");
                    break;
                default:        // 离线
                    bg = (Brush)FindResource("SF.Brush.Bg.Hover");
                    fg = (Brush)FindResource("SF.Brush.Text.Secondary");
                    break;
            }

            statusBadge.Background = bg;
            txtStatus.Foreground = fg;
        }

        /// <summary>
        /// 设置基本信息（名称、副标题、轨道类型），并根据是否双轨切换轨道标签的配色。
        /// </summary>
        /// <param name="name">设备名称。</param>
        /// <param name="sub">副标题，通常用于展示型号/IP 等附加信息。</param>
        /// <param name="lane">轨道标签文本，如 "单轨"/"双轨"。</param>
        /// <param name="isDual">是否为双轨模式，影响标签配色。</param>
        public void SetInfo (string name, string sub, string lane, bool isDual) {
            txtName.Text = name;
            txtSub.Text = sub;
            txtLane.Text = lane;

            if (isDual) {
                laneBadge.Background = (Brush)FindResource("SF.Brush.Mes.TagDualBg");
                txtLane.Foreground = (Brush)FindResource("SF.Brush.Mes.WidthTitle");
            } else {
                laneBadge.Background = (Brush)FindResource("SF.Brush.Mes.TagSingleBg");
                txtLane.Foreground = (Brush)FindResource("SF.Brush.Mes.TagSingleFg");
            }
        }
    }
}