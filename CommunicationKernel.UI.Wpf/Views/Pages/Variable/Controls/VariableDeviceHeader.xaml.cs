#nullable disable

// -----------------------------------------------------------------------------
// 文件: Views/Pages/Variable/Controls/VariableDeviceHeader.xaml.cs
// 层级: UI 层 — WPF Views
// 作用: 右侧当前设备标题栏；展示名称/元信息，转发添加与批量添加。
// -----------------------------------------------------------------------------

using System;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using CommunicationKernel.UI.Wpf.Core.Models;
using CommunicationKernel.UI.Wpf.Core.Interfaces;

namespace CommunicationKernel.UI.Wpf.Views.Pages.Variable.Controls {
    /// <summary>右侧当前设备标题与操作按钮。</summary>
    public partial class VariableDeviceHeader : UserControl {
        /// <summary>由页面注入。</summary>
        public IDeviceService DeviceService { get; set; }

        public event Action AddClicked;
        public event Action BatchAddClicked;

        public VariableDeviceHeader () {
            // 解析 XAML，构建视觉树
            InitializeComponent();
        }

        // ============================================================================
        // 标题刷新
        // ============================================================================

        public void Show (string deviceId) {
            // 未选设备或服务未注入：回到占位文案
            if (string.IsNullOrEmpty(deviceId) || DeviceService == null) {
                txtTitle.Text = "请选择设备";
                txtMeta.Text = "";
                return;
            }

            DeviceInfo d = DeviceService.Devices.FirstOrDefault(x => x != null && x.Id == deviceId);
            if (d == null) {
                txtTitle.Text = "请选择设备";
                txtMeta.Text = "";
                return;
            }

            txtTitle.Text = string.IsNullOrEmpty(d.Name) ? d.Id : d.Name;
            txtMeta.Text = string.Format("{0} · {1} · {2}",
                d.Model ?? "", d.Ip ?? "", d.Protocol ?? "");
        }

        // ============================================================================
        // 按钮
        // ============================================================================

        private void BtnAdd_Click (object sender, RoutedEventArgs e) =>
            AddClicked?.Invoke();

        private void BtnBatchAdd_Click (object sender, RoutedEventArgs e) =>
            BatchAddClicked?.Invoke();
    }
}
