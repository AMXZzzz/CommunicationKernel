using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Input;
using CommunicationKernel.UI.Wpf.Core.Enums;
using CommunicationKernel.UI.Wpf.Core.Interfaces;
using CommunicationKernel.UI.Wpf.Core.Models;
using CommunicationKernel.UI.Wpf.Services;

namespace CommunicationKernel.UI.Wpf.Views.Pages.Device {
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

        /// <summary>
        /// 填充协议下拉框。
        /// 每个 ComboBoxItem 的 Content 为展示名，Tag 挂载完整描述符——
        /// 注册路由时必须取 Tag 中的 ProtocolId，绝不能用展示名。
        /// </summary>
        private void LoadProtocolList () {
            if (cmbProtocol == null)
                return;

            cmbProtocol.Items.Clear();

            if (ProtocolResolver == null)
                return;

            IList<ProtocolDescriptorDto> protocols = ProtocolResolver.GetProtocols();
            if (protocols == null)
                return;

            foreach (ProtocolDescriptorDto p in protocols) {
                if (p == null || string.IsNullOrWhiteSpace(p.ProtocolId))
                    continue;

                // Content 给人看，Tag 给程序用
                cmbProtocol.Items.Add(new ComboBoxItem {
                    Content = string.IsNullOrWhiteSpace(p.DisplayName) ? p.ProtocolId : p.DisplayName,
                    Tag     = p
                });
            }

            if (cmbProtocol.Items.Count > 0)
                cmbProtocol.SelectedIndex = 0;
        }

        /// <summary>
        /// 协议选择变化：按所选协议的描述符切换连接参数表单。
        /// UI 不内置任何协议知识，全部依据服务端下发的描述符渲染。
        /// </summary>
        private void CmbProtocol_SelectionChanged (object sender, SelectionChangedEventArgs e) {
            ApplyProtocolLayout(GetSelectedDescriptor());
        }

        /// <summary>
        /// 依据协议描述符决定：显示 TCP 参数还是串口参数、是否显示站号、站号提示文案。
        /// descriptor 为 null（协议列表尚未就绪）时退化为 TCP + 显示站号的通用布局。
        /// </summary>
        private void ApplyProtocolLayout (ProtocolDescriptorDto descriptor) {
            // 判定传输介质：描述符缺失时按 TCP 处理，保证表单始终可用
            bool isSerial = descriptor != null
                && string.Equals(descriptor.TransportKind, "Serial", StringComparison.OrdinalIgnoreCase);

            if (panelTcp != null)
                panelTcp.Visibility = isSerial ? Visibility.Collapsed : Visibility.Visible;
            if (panelSerial != null)
                panelSerial.Visibility = isSerial ? Visibility.Visible : Visibility.Collapsed;

            // 站号：仅在协议确实需要时展示，避免让操作员填写无意义字段
            bool needStation = descriptor == null || descriptor.RequiresStation;
            if (panelStation != null)
                panelStation.Visibility = needStation ? Visibility.Visible : Visibility.Collapsed;

            // 站号范围提示直接来自插件元信息，无需 UI 硬编码
            if (runStationHint != null) {
                string hint = descriptor != null ? descriptor.StationHint : null;
                runStationHint.Text = string.IsNullOrWhiteSpace(hint) ? "" : "  " + hint;
            }
        }

        /// <summary>取当前选中项挂载的协议描述符；未选中或无 Tag 时返回 null。</summary>
        private ProtocolDescriptorDto GetSelectedDescriptor () {
            ComboBoxItem item = cmbProtocol != null
                ? cmbProtocol.SelectedItem as ComboBoxItem
                : null;
            return item != null ? item.Tag as ProtocolDescriptorDto : null;
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
                txtPort.Text = info.Port > 0 ? info.Port.ToString() : "502";

            // 串口参数（串口类协议使用）
            if (txtSerialPort != null)
                txtSerialPort.Text = info.SerialPort ?? "";
            if (txtBaudRate != null)
                txtBaudRate.Text = info.BaudRate > 0 ? info.BaudRate.ToString() : "9600";

            // 站号：设备级配置。变量地址因此可以保持干净（DT100 而非 01:DT100）
            if (txtStationNo != null)
                txtStationNo.Text = info.StationNo > 0 ? info.StationNo.ToString() : "1";

            // 依据当前选中协议切换连接参数表单
            ApplyProtocolLayout(GetSelectedDescriptor());

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

            ProtocolDescriptorDto descriptor = GetSelectedDescriptor();

            // 协议：必须写 ProtocolId（服务端匹配键），不能写展示名
            d.Protocol = GetSelectedProtocolId();

            // 传输介质：由协议描述符决定，不再留空导致服务端 Enum.TryParse 失败
            bool isSerial = descriptor != null
                && string.Equals(descriptor.TransportKind, "Serial", StringComparison.OrdinalIgnoreCase);
            d.TransportKind = isSerial ? "Serial" : "Tcp";

            if (isSerial) {
                // 串口路由：串口名走 SerialPort 字段，IP/端口留空
                d.SerialPort = txtSerialPort != null ? txtSerialPort.Text.Trim() : "";

                int baud;
                d.BaudRate = (txtBaudRate != null && int.TryParse(txtBaudRate.Text.Trim(), out baud) && baud > 0)
                    ? baud
                    : 9600;

                d.Ip   = "";
                d.Port = 0;
            } else {
                // TCP 路由：IP + 端口
                d.Ip = txtIp != null ? txtIp.Text.Trim() : "";

                int port;
                d.Port = (txtPort != null && int.TryParse(txtPort.Text.Trim(), out port) && port > 0)
                    ? port
                    : 502;

                d.SerialPort = "";
                d.BaudRate   = 0;
            }

            // 站号：不需要站号的协议（如 S7）统一写 0，Station 字符串留空
            if (descriptor != null && !descriptor.RequiresStation) {
                d.StationNo = 0;
                d.Station   = "";
            } else {
                int station = 1;
                if (txtStationNo != null && int.TryParse(txtStationNo.Text.Trim(), out int parsed) && parsed > 0)
                    station = parsed;

                d.StationNo = station;
                // 同步写 Station 字符串：注册路由时传的是它，此前只写 StationNo 导致站号丢失
                d.Station = station.ToString();
            }

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

        /// <summary>
        /// 按 ProtocolId 选中下拉项（编辑既有设备时还原选择）。
        /// 匹配依据是 Tag 中的 ProtocolId，不是展示名。
        /// </summary>
        private void SelectProtocol (string protocolId) {
            if (cmbProtocol == null || cmbProtocol.Items.Count == 0)
                return;

            if (!string.IsNullOrWhiteSpace(protocolId)) {
                for (int i = 0; i < cmbProtocol.Items.Count; i++) {
                    ComboBoxItem item = cmbProtocol.Items[i] as ComboBoxItem;
                    ProtocolDescriptorDto d = item != null ? item.Tag as ProtocolDescriptorDto : null;

                    if (d != null
                        && string.Equals(d.ProtocolId, protocolId, StringComparison.OrdinalIgnoreCase)) {
                        cmbProtocol.SelectedIndex = i;
                        return;
                    }
                }
            }

            // 未匹配到（协议已下架或首次新增）：回落到第一项
            cmbProtocol.SelectedIndex = 0;
        }

        /// <summary>
        /// 取当前选中协议的 ProtocolId（服务端匹配用的键）。
        /// 描述符缺失时返回空串，由上层校验拦截。
        /// </summary>
        private string GetSelectedProtocolId () {
            ProtocolDescriptorDto d = GetSelectedDescriptor();
            return d != null ? d.ProtocolId : "";
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
