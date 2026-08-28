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

        /// <summary>「添加变量」被点击。</summary>
        public event Action AddClicked;

        /// <summary>「批量添加」被点击。</summary>
        public event Action BatchAddClicked;

        /// <summary>构造：解析 XAML，构建视觉树。</summary>
        public VariableDeviceHeader () {
            // 解析 XAML，构建视觉树
            InitializeComponent();
        }

        // ============================================================================
        // 标题刷新
        // ============================================================================

        /// <summary>刷新标题与元信息行。</summary>
        /// <param name="deviceId">
        /// 设备 Id。为空、服务未注入、或该设备已不存在时，一律回到「请选择设备」占位——
        /// 保留上一台设备的标题会让操作员以为变量表还是那台机器的。
        /// </param>
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

        /// <summary>「添加变量」按钮。</summary>
        /// <param name="sender">事件源。</param>
        /// <param name="e">事件参数。</param>
        private void BtnAdd_Click (object sender, RoutedEventArgs e) =>
            AddClicked?.Invoke();

        /// <summary>「批量添加」按钮。</summary>
        /// <param name="sender">事件源。</param>
        /// <param name="e">事件参数。</param>
        private void BtnBatchAdd_Click (object sender, RoutedEventArgs e) =>
            BatchAddClicked?.Invoke();
    }
}
