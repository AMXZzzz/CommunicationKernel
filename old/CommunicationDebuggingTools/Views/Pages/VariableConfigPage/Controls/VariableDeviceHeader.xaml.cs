using System;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using CommunicationDebuggingTools.Core.Models;
using CommunicationDebuggingTools.Core.Interfaces;

namespace CommunicationDebuggingTools.Views.VariableConfigPage.Controls {
    /// <summary>右侧当前设备标题与操作按钮。</summary>
    public partial class VariableDeviceHeader : UserControl {
        /// <summary>由页面注入。</summary>
        public IDeviceService DeviceService { get; set; }

        public event Action AddClicked;
        public event Action BatchAddClicked;

        public VariableDeviceHeader () {
            InitializeComponent();
        }

        public void Show (string deviceId) {
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

        private void BtnAdd_Click (object sender, RoutedEventArgs e) =>
            AddClicked?.Invoke();

        private void BtnBatchAdd_Click (object sender, RoutedEventArgs e) =>
            BatchAddClicked?.Invoke();
    }
}