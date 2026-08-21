#nullable disable

using System;
using System.ComponentModel;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using CommunicationKernel.UI.Wpf.Core.Enums;
using CommunicationKernel.UI.Wpf.Core.Models;

namespace CommunicationKernel.UI.Wpf.Views.Pages.Device {
    /// <summary>
    /// PLC 设备卡片。
    /// 数据来自依赖属性 <see cref="Device"/>；文本由 XAML 绑定；
    /// 状态灯 / 主按钮由 <see cref="ApplyStatusVisual"/> 更新；
    /// 多选时通过 <see cref="SetSelectionMode"/> 显示隐藏 CheckBox。
    /// <para>
    /// 服务：由父页 <c>DevicePage</c> 通过属性注入 <see cref="DeviceService"/>，
    /// </para>
    /// </summary>
    public partial class DeviceCard : UserControl {
        /// <summary>当前已订阅 PropertyChanged 的设备。</summary>
        private DeviceInfo _subscribed;

        /// <summary>
        /// 连接委托（由 DevicePage 注入）：(deviceId, token) → Task。
        /// Card 不持有 IDeviceService，通过委托与 VM 解耦。
        /// </summary>
        public Func<string, System.Threading.CancellationToken, System.Threading.Tasks.Task>
            ConnectDevice { get; set; }

        /// <summary>断开委托（由 DevicePage 注入）：deviceId → void。</summary>
        public Action<string> DisconnectDevice { get; set; }

        /// <summary>勾选变化（设备 Id, 是否选中）。页面侧可选使用。</summary>
        public event Action<string, bool> SelectionChanged;

        /// <summary>绑定设备数据。</summary>
        public static readonly DependencyProperty DeviceProperty =
            DependencyProperty.Register(
                "Device",
                typeof(DeviceInfo),
                typeof(DeviceCard),
                new PropertyMetadata(null, OnDeviceChanged));

        /// <summary>当前设备。</summary>
        public DeviceInfo Device {
            get { return (DeviceInfo)GetValue(DeviceProperty); }
            set { SetValue(DeviceProperty, value); }
        }

        /// <summary>是否勾选（多选删除用）。</summary>
        public bool IsSelected {
            get { return chkSelect != null && chkSelect.IsChecked == true; }
            set {
                if (chkSelect != null)
                    chkSelect.IsChecked = value;
            }
        }

        public DeviceCard () {
            InitializeComponent();

            if (btnEdit != null)
                btnEdit.Click += BtnEdit_Click;

            if (btnPrimary != null)
                btnPrimary.Click += BtnPrimary_Click;

            Unloaded += DeviceCard_Unloaded;
        }

        /// <summary>
        /// 多选模式：显示或隐藏勾选框；退出时清空勾选。
        /// </summary>
        public void SetSelectionMode (bool enabled) {
            if (chkSelect == null)
                return;

            chkSelect.Visibility = enabled ? Visibility.Visible : Visibility.Collapsed;
            if (!enabled)
                chkSelect.IsChecked = false;
        }

        /// <summary>CheckBox 勾选变化。</summary>
        private void ChkSelect_Changed (object sender, RoutedEventArgs e) {
            DeviceInfo info = Device;
            if (info == null || string.IsNullOrEmpty(info.Id))
                return;

            SelectionChanged?.Invoke(info.Id, IsSelected);
        }

        /// <summary>卸下时取消 PropertyChanged 订阅，避免泄漏。</summary>
        private void DeviceCard_Unloaded (object sender, RoutedEventArgs e) {
            if (_subscribed != null) {
                _subscribed.PropertyChanged -= Device_PropertyChanged;
                _subscribed = null;
            }

            Unloaded -= DeviceCard_Unloaded;
        }

        /// <summary>Device 变更：设置 DataContext 并订阅状态。</summary>
        private static void OnDeviceChanged (DependencyObject d, DependencyPropertyChangedEventArgs e) {
            var card = (DeviceCard)d;
            card.DataContext = e.NewValue;
            card.ApplyDevice(e.NewValue as DeviceInfo);
        }

        /// <summary>切换数据源并刷新状态外观。</summary>
        private void ApplyDevice (DeviceInfo info) {
            if (_subscribed != null)
                _subscribed.PropertyChanged -= Device_PropertyChanged;

            _subscribed = info;

            if (info != null) {
                info.PropertyChanged += Device_PropertyChanged;
                ApplyStatusVisual(info.StatusType);
            }
        }

        /// <summary>仅状态相关属性变化时更新灯与按钮。</summary>
        private void Device_PropertyChanged (object sender, PropertyChangedEventArgs e) {
            if (e.PropertyName == "StatusType" ||
                e.PropertyName == "StatusText" ||
                e.PropertyName == "IsConnected") {
                if (Device != null)
                    ApplyStatusVisual(Device.StatusType);
            }
        }

        /// <summary>强制按当前 Device 刷新外观。</summary>
        public void RefreshFromDevice () {
            ApplyDevice(Device);
        }

        /// <summary>StatusType → 状态 Key，再交给 SetStatus。</summary>
        private void ApplyStatusVisual (DeviceStatusType type) {
            string statusKey;
            switch (type) {
                case DeviceStatusType.Success:
                    statusKey = "Success";
                    break;
                case DeviceStatusType.Connecting:
                case DeviceStatusType.Warning:
                    statusKey = "Warning";
                    break;
                case DeviceStatusType.Error:
                    statusKey = "Error";
                    break;
                default:
                    statusKey = "Offline";
                    break;
            }

            string statusText = Device != null ? Device.StatusText : "离线";
            SetStatus(statusText, statusKey);
        }

        /// <summary>更新状态文案色、灯、色条、主按钮。</summary>
        public void SetStatus (string statusText, string statusType) {
            if (plcCurrentState != null)
                plcCurrentState.Text = statusText;

            Brush brush;
            switch (statusType) {
                case "Success":
                    brush = (Brush)FindResource("SF.Brush.Status.Success");
                    if (btnPrimary != null) {
                        btnPrimary.Content = "断开";
                        btnPrimary.Style = (Style)FindResource("SF.Style.DangerButton");
                    }
                    break;
                case "Warning":
                    brush = (Brush)FindResource("SF.Brush.Status.Warning");
                    if (btnPrimary != null) {
                        btnPrimary.Content = "取消";
                        btnPrimary.Style = (Style)FindResource("SF.Style.DangerButton");
                    }
                    break;
                case "Error":
                    brush = (Brush)FindResource("SF.Brush.Status.Error");
                    if (btnPrimary != null) {
                        btnPrimary.Content = "重连";
                        btnPrimary.Style = (Style)FindResource("SF.Style.PrimaryButton");
                    }
                    break;
                default:
                    brush = (Brush)FindResource("SF.Brush.Text.Secondary");
                    if (btnPrimary != null) {
                        btnPrimary.Content = "连接";
                        btnPrimary.Style = (Style)FindResource("SF.Style.PrimaryButton");
                    }
                    break;
            }

            if (plcStatusLight != null)
                plcStatusLight.Fill = brush;
            if (plcCurrentState != null)
                plcCurrentState.Foreground = brush;
            if (AccentBar != null)
                AccentBar.Background = brush;
        }

        /// <summary>打开编辑弹窗。</summary>
        private void BtnEdit_Click (object sender, RoutedEventArgs e) {
            DeviceInfo info = Device;
            if (info == null)
                return;

            DevicePage page = FindParentPage(this);
            if (page != null)
                page.OpenEditDevice(info);
        }

        /// <summary>连接 / 断开 / 重连（异步，不阻塞 UI）。</summary>
        private async void BtnPrimary_Click (object sender, RoutedEventArgs e) {
            DeviceInfo info = Device;
            if (info == null || string.IsNullOrEmpty(info.Id))
                return;

            if (ConnectDevice == null || DisconnectDevice == null)
                return;

            // 已连接或连接中 → 断开 / 取消
            if (info.IsConnected || info.StatusType == DeviceStatusType.Connecting) {
                DisconnectDevice(info.Id);
                return;
            }

            info.StatusType = DeviceStatusType.Connecting;
            info.IsConnected = false;

            if (btnPrimary != null)
                btnPrimary.IsEnabled = false;

            string id = info.Id;

            try {
                await ConnectDevice(id, CancellationToken.None);
            } catch {
                if (Device != null && Device.Id == id) {
                    Device.IsConnected = false;
                    Device.StatusType = DeviceStatusType.Error;
                }
            } finally {
                if (btnPrimary != null)
                    btnPrimary.IsEnabled = true;
            }
        }

        /// <summary>向上查找所属 DevicePage。</summary>
        private static DevicePage FindParentPage (DependencyObject d) {
            while (d != null) {
                DevicePage page = d as DevicePage;
                if (page != null)
                    return page;

                DependencyObject parent = VisualTreeHelper.GetParent(d);
                if (parent == null) {
                    FrameworkElement fe = d as FrameworkElement;
                    if (fe != null)
                        parent = fe.Parent as DependencyObject;
                }
                d = parent;
            }
            return null;
        }
    }
}