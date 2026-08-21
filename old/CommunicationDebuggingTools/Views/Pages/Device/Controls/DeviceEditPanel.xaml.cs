using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Input;
using CommunicationDebuggingTools.Core.Enums;
using CommunicationDebuggingTools.Core.Interfaces;
using CommunicationDebuggingTools.Core.Models;

namespace CommunicationDebuggingTools.Views.Pages.Device {
    /// <summary>
    /// 设备新增 / 编辑面板。
    /// 只收集共性字段（含 StationNo）；不拼 unitId/station JSON，不解析协议语义。
    /// 协议名列表来自属性注入的 <see cref="ProtocolResolver"/>
    /// </summary>
    public partial class DeviceEditPanel : UserControl {
        public event Action CloseRequested;
        public event Action SaveRequested;
        public event Action DeleteRequested;

        private string _editingId;

        private bool _dragging;
        private Point _dragStart;
        private double _originX;
        private double _originY;
        private bool _isDual;

        /// <summary>编辑时保留原扩展 JSON，一期界面不改。</summary>
        private string _extraSettingsJson = "{}";

        /// <summary>
        /// 协议解析器（由 DevicePage 赋值）。用于填充协议下拉，不解析地址语义。
        /// </summary>
        public IProtocolResolver ProtocolResolver { get; set; }

        public DeviceEditPanel () {
            InitializeComponent();
        }

        public bool IsNew => string.IsNullOrEmpty(_editingId);

        private void LoadProtocolList () {
            if (cmbProtocol == null)
                return;

            cmbProtocol.Items.Clear();

            if (ProtocolResolver == null)
                return;

            IList<string> names = ProtocolResolver.GetProtocolNames();
            if (names == null)
                return;

            foreach (string name in names) {
                if (string.IsNullOrWhiteSpace(name))
                    continue;
                cmbProtocol.Items.Add(new ComboBoxItem { Content = name });
            }

            if (cmbProtocol.Items.Count > 0)
                cmbProtocol.SelectedIndex = 0;
        }

        /// <summary>载入设备到表单。</summary>
        public void LoadData (DeviceInfo info, bool isNew) {
            ResetPosition();
            if (info == null)
                info = new DeviceInfo();

            _editingId = isNew ? null : info.Id;
            _isDual = info.IsDualLane;
            _extraSettingsJson = string.IsNullOrWhiteSpace(info.ExtraSettingsJson)
                ? "{}"
                : info.ExtraSettingsJson;

            LoadProtocolList();
            SelectProtocol(info.Protocol);

            if (txtName != null)
                txtName.Text = info.Name ?? "";
            if (txtModel != null)
                txtModel.Text = info.Model ?? "";
            if (txtIp != null)
                txtIp.Text = info.Ip ?? "";
            if (txtPort != null)
                txtPort.Text = info.Port.ToString();

            // 站号：只绑 StationNo（控件名可为 txtStationNo，语义是站号）
            if (txtStationNo != null)
                txtStationNo.Text = info.StationNo.ToString();

            if (txtStatus != null) {
                txtStatus.Text = info.StatusText ?? "离线";
                ApplyStatusColor(info.StatusType);
            }

            UpdateLaneButtons();
        }

        /// <summary>从表单构建 DeviceInfo。</summary>
        public DeviceInfo BuildDeviceInfo () {
            DeviceInfo d = new DeviceInfo();
            if (!string.IsNullOrEmpty(_editingId))
                d.Id = _editingId;

            d.Name = txtName != null ? txtName.Text.Trim() : "";
            d.Model = txtModel != null ? txtModel.Text.Trim() : "";
            d.Ip = txtIp != null ? txtIp.Text.Trim() : "";

            int port;
            d.Port = (txtPort != null && int.TryParse(txtPort.Text.Trim(), out port))
                ? port
                : 502;

            int station = 1;
            if (txtStationNo != null)
                int.TryParse(txtStationNo.Text.Trim(), out station);
            if (station < 0)
                station = 0;
            d.StationNo = station;

            d.Protocol = GetSelectedProtocol();
            d.IsDualLane = _isDual;
            d.ExtraSettingsJson = string.IsNullOrWhiteSpace(_extraSettingsJson)
                ? "{}"
                : _extraSettingsJson;

            if (string.IsNullOrEmpty(_editingId)) {
                d.IsConnected = false;
                d.StatusType = DeviceStatusType.Offline;
            }

            return d;
        }

        private void ApplyStatusColor (DeviceStatusType type) {
            if (txtStatus == null)
                return;

            string key = "SF.Brush.Text.Secondary";
            switch (type) {
                case DeviceStatusType.Success:
                    key = "SF.Brush.Status.Success";
                    break;
                case DeviceStatusType.Connecting:
                case DeviceStatusType.Warning:
                    key = "SF.Brush.Status.Warning";
                    break;
                case DeviceStatusType.Error:
                    key = "SF.Brush.Status.Error";
                    break;
            }

            try {
                txtStatus.Foreground = (System.Windows.Media.Brush)FindResource(key);
            } catch { }
        }

        private void SelectProtocol (string protocol) {
            if (cmbProtocol == null || string.IsNullOrWhiteSpace(protocol))
                return;

            for (int i = 0; i < cmbProtocol.Items.Count; i++) {
                ComboBoxItem item = cmbProtocol.Items[i] as ComboBoxItem;
                string text = item != null && item.Content != null
                    ? item.Content.ToString()
                    : (cmbProtocol.Items[i] != null ? cmbProtocol.Items[i].ToString() : "");

                if (string.Equals(text, protocol, StringComparison.OrdinalIgnoreCase)) {
                    cmbProtocol.SelectedIndex = i;
                    return;
                }
            }

            if (cmbProtocol.Items.Count > 0)
                cmbProtocol.SelectedIndex = 0;
        }

        private string GetSelectedProtocol () {
            if (cmbProtocol == null || cmbProtocol.SelectedItem == null)
                return "";

            ComboBoxItem item = cmbProtocol.SelectedItem as ComboBoxItem;
            if (item != null && item.Content != null)
                return item.Content.ToString();

            string s = cmbProtocol.SelectedItem as string;
            return string.IsNullOrWhiteSpace(s) ? "" : s.Trim();
        }

        private void UpdateLaneButtons () {
            if (btnLaneSingle == null || btnLaneDual == null)
                return;

            if (_isDual) {
                btnLaneDual.Style = (Style)FindResource("SF.Style.PrimaryButton");
                btnLaneSingle.Style = (Style)FindResource("SF.Style.DarkButton");
            } else {
                btnLaneSingle.Style = (Style)FindResource("SF.Style.PrimaryButton");
                btnLaneDual.Style = (Style)FindResource("SF.Style.DarkButton");
            }
        }

        private void SetLane (bool dual) {
            _isDual = dual;
            UpdateLaneButtons();
        }

        private void BtnLaneSingle_Click (object sender, RoutedEventArgs e) => SetLane(false);
        private void BtnLaneDual_Click (object sender, RoutedEventArgs e) => SetLane(true);
        private void BtnClose_Click (object sender, RoutedEventArgs e) => CloseRequested?.Invoke();
        private void BtnClose_Click (object sender, MouseButtonEventArgs e) => CloseRequested?.Invoke();
        private void BtnSave_Click (object sender, RoutedEventArgs e) => SaveRequested?.Invoke();
        private void BtnDelete_Click (object sender, RoutedEventArgs e) => DeleteRequested?.Invoke();

        /// <summary>打开弹窗时复位拖动偏移（回到遮罩居中）。</summary>
        public void ResetPosition () {
            if (panelTranslate != null) {
                panelTranslate.X = 0;
                panelTranslate.Y = 0;
            }
            _dragging = false;
        }

        /// <summary>标题栏按下：整条色块开始拖动（关闭按钮已被独立命中）。</summary>
        private void TitleBar_MouseLeftButtonDown (object sender, MouseButtonEventArgs e) {
            if (e.ChangedButton != MouseButton.Left)
                return;
            // 点在关闭按钮上不拖
            DependencyObject src = e.OriginalSource as DependencyObject;
            while (src != null) {
                if (src is Button)
                    return;
                src = VisualTreeHelper.GetParent(src);
            }

            _dragging = true;
            _dragStart = e.GetPosition(null);
            _originX = panelTranslate != null ? panelTranslate.X : 0;
            _originY = panelTranslate != null ? panelTranslate.Y : 0;
            titleBar.CaptureMouse();
            e.Handled = true;
        }

        private void TitleBar_MouseMove (object sender, MouseEventArgs e) {
            if (!_dragging || e.LeftButton != MouseButtonState.Pressed)
                return;
            Point p = e.GetPosition(null);
            if (panelTranslate != null) {
                panelTranslate.X = _originX + (p.X - _dragStart.X);
                panelTranslate.Y = _originY + (p.Y - _dragStart.Y);
            }
        }

        private void TitleBar_MouseLeftButtonUp (object sender, MouseButtonEventArgs e) {
            StopDrag();
        }

        private void TitleBar_LostMouseCapture (object sender, MouseEventArgs e) {
            StopDrag();
        }

        private void StopDrag () {
            if (!_dragging)
                return;
            _dragging = false;
            if (titleBar != null && titleBar.IsMouseCaptured)
                titleBar.ReleaseMouseCapture();
        }

    }
}
