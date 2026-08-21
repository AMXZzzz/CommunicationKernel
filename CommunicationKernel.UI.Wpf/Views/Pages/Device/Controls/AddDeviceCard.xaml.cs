using CommunicationKernel.UI.Wpf.Views.Tools;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace CommunicationKernel.UI.Wpf.Views.Pages.Device {
    /// <summary>
    /// “添加新 PLC”占位卡片。本身不携带任何设备数据，仅在被点击时找到所属的 <see cref="DevicePage"/>
    /// 并调用其 OpenAddDevice() 弹出新增设备的编辑面板。
    /// </summary>
    public partial class AddDeviceCard : UserControl {
        /// <summary>构造卡片并将鼠标样式设为手形，提示用户可点击。</summary>
        public AddDeviceCard () {
            InitializeComponent();
            Cursor = Cursors.Hand;
        }

        /// <summary>
        /// 处理添加设备卡片的点击事件。
        /// 优先通过视觉树/逻辑树向上查找所属的 <see cref="DevicePage"/>，
        /// 若未找到则从当前窗口的视觉树中搜索。
        /// 找到后调用 <see cref="DevicePage.OpenAddDevice"/>，并标记事件已处理，防止事件继续冒泡。
        /// </summary>
        private void AddDeviceCard_MouseLeftButtonUp (object sender, MouseButtonEventArgs e) {
            if (e.Handled) return;

            // 1. 优先向上查找
            var page = this.FindAncestor<DevicePage>();

            // 2. 找不到再从窗口向下搜索（保底）
            if (page == null) {
                var window = Window.GetWindow(this);
                page = window?.FindDescendant<DevicePage>();
            }

            if (page == null) return;

            e.Handled = true;
            page.OpenAddDevice();
        }
    }
}