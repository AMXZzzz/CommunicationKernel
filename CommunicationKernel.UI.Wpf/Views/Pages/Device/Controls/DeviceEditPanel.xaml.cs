#nullable disable

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

        /// <summary>批量重建介质下拉框期间抑制 SelectionChanged，避免中间态触发布局刷新。</summary>
        private bool _suppressTransportEvent;

        /// <summary>
        /// 协议解析器（由 DevicePage 赋值）。用于填充协议下拉，不解析地址语义。
        /// </summary>
        public IProtocolResolver ProtocolResolver { get; set; }

        /// <summary>
        /// 串口清单提供者（由 DevicePage 赋值），可为 null。
        /// </summary>
        /// <remarks>
        /// 清单来自 <b>EngineHost 所在的机器</b>。宿主跑在树莓派时，
        /// 操作员要选的是树莓派上的 /dev/ttyUSB0，而不是本机的 COM1。
        /// 为 null、宿主不可达或现场无串口时，下拉框留空但仍可手工输入。
        /// </remarks>
        public ISerialPortProvider SerialPortProvider { get; set; }

        public DeviceEditPanel () {
            InitializeComponent();
        }

        public bool IsNew => string.IsNullOrEmpty(_editingId);

        /// <summary>
        /// 向宿主拉取串口清单并填入下拉框，保留调用前已有的文本值。
        /// </summary>
        /// <param name="currentValue">当前已选/已输入的串口名，拉取后需原样保留。</param>
        /// <remarks>
        /// 全程不抛异常：拿不到清单时下拉框为空，操作员仍可手工输入。
        /// 串口配不出来会让整台设备不可用，绝不能因为一次查询失败就卡住配置流程。
        /// </remarks>
        private async System.Threading.Tasks.Task LoadSerialPortsAsync (string currentValue) {
            if (cmbSerialPort == null || SerialPortProvider == null)
                return;

            IReadOnlyList<SerialPortDto> ports;
            try {
                ports = await SerialPortProvider
                    .GetPortsAsync(System.Threading.CancellationToken.None)
                    .ConfigureAwait(true);   // 需回到 UI 线程操作控件
            } catch (Exception) {
                return;
            }

            if (cmbSerialPort == null) return;   // 面板可能已在等待期间关闭

            cmbSerialPort.Items.Clear();
            foreach (SerialPortDto port in ports) {
                cmbSerialPort.Items.Add(new ComboBoxItem {
                    Content = port.Display,
                    // Tag 存纯设备名：Content 可能带 by-id 说明，
                    // 直接拿 Content 去注册会得到一个不存在的设备名
                    Tag     = port.PortName
                });
            }

            // 清单刷新会清空 Text，这里恢复原值——
            // 编辑既有设备时它必须留在框里，哪怕宿主清单里已经没有这个口
            //（设备暂时拔了线不代表配置该被抹掉）。
            cmbSerialPort.Text = currentValue ?? "";
        }

        /// <summary>下拉选中某个串口时，把纯设备名写回文本框。</summary>
        private void OnSerialPortSelected (object sender, SelectionChangedEventArgs e) {
            if (cmbSerialPort?.SelectedItem is ComboBoxItem item && item.Tag is string portName)
                cmbSerialPort.Text = portName;
        }

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
        /// <summary>
        /// 填充「连接方式」下拉框。
        /// 协议只支持一种介质时隐藏该选择器，避免无意义的单选项。
        /// </summary>
        private void LoadTransportList (ProtocolDescriptorDto descriptor, string preferred) {
            if (cmbTransport == null) return;

            _suppressTransportEvent = true;
            try {
                cmbTransport.Items.Clear();

                IReadOnlyList<string> kinds = descriptor != null
                    ? descriptor.SupportedTransports
                    : null;

                if (kinds == null || kinds.Count == 0)
                    kinds = new[] { "Tcp" };

                foreach (string kind in kinds) {
                    cmbTransport.Items.Add(new ComboBoxItem {
                        Content = DescribeTransport(kind),
                        Tag     = kind
                    });
                }

                // 还原此前选择；未匹配则取首项
                int index = 0;
                if (!string.IsNullOrWhiteSpace(preferred)) {
                    for (int i = 0; i < cmbTransport.Items.Count; i++) {
                        ComboBoxItem item = cmbTransport.Items[i] as ComboBoxItem;
                        if (item != null
                            && string.Equals(item.Tag as string, preferred, StringComparison.OrdinalIgnoreCase)) {
                            index = i;
                            break;
                        }
                    }
                }
                cmbTransport.SelectedIndex = index;

                // 只有一种介质时无需让操作员选择
                if (panelTransport != null)
                    panelTransport.Visibility = cmbTransport.Items.Count > 1
                        ? Visibility.Visible
                        : Visibility.Collapsed;
            } finally {
                _suppressTransportEvent = false;
            }
        }

        /// <summary>介质标识 → 面向操作员的说明文案。</summary>
        private static string DescribeTransport (string kind) {
            if (string.Equals(kind, "Serial", StringComparison.OrdinalIgnoreCase))
                return "串口直连（RS-232 / RS-485）";
            if (string.Equals(kind, "Tcp", StringComparison.OrdinalIgnoreCase))
                return "以太网（含 TCP 转串口透传装置）";
            return kind;
        }

        /// <summary>取当前选中的介质标识；无选择时回落到协议默认介质。</summary>
        private string GetSelectedTransport () {
            ComboBoxItem item = cmbTransport != null
                ? cmbTransport.SelectedItem as ComboBoxItem
                : null;

            string kind = item != null ? item.Tag as string : null;
            if (!string.IsNullOrWhiteSpace(kind))
                return kind;

            ProtocolDescriptorDto d = GetSelectedDescriptor();
            return d != null ? d.DefaultTransport : "Tcp";
        }

        /// <summary>连接方式变化：仅切换连接参数表单，不改变协议选择。</summary>
        private void CmbTransport_SelectionChanged (object sender, SelectionChangedEventArgs e) {
            if (_suppressTransportEvent) return;
            ApplyTransportLayout(GetSelectedDescriptor(), GetSelectedTransport());
        }

        private void ApplyProtocolLayout (ProtocolDescriptorDto descriptor) {
            // 协议变化时重建介质列表，尽量保留操作员此前的选择
            LoadTransportList(descriptor, GetSelectedTransportTagOrNull());
            ApplyTransportLayout(descriptor, GetSelectedTransport());
        }

        /// <summary>读取当前介质选择，用于协议切换时尽量保留；无选择返回 null。</summary>
        private string GetSelectedTransportTagOrNull () {
            ComboBoxItem item = cmbTransport != null
                ? cmbTransport.SelectedItem as ComboBoxItem
                : null;
            return item != null ? item.Tag as string : null;
        }

        /// <summary>
        /// 按「所选介质」渲染连接参数表单，并按「所选协议」决定站号可见性。
        /// </summary>
        private void ApplyTransportLayout (ProtocolDescriptorDto descriptor, string transportKind) {
            bool isSerial = string.Equals(transportKind, "Serial", StringComparison.OrdinalIgnoreCase);

            if (panelTcp != null)
                panelTcp.Visibility = isSerial ? Visibility.Collapsed : Visibility.Visible;
            if (panelSerial != null)
                panelSerial.Visibility = isSerial ? Visibility.Visible : Visibility.Collapsed;

            // 站号：仅在协议确实需要时展示，避免让操作员填写无意义字段
            bool needStation = descriptor == null || descriptor.RequiresStation;
            if (panelStation != null)
                panelStation.Visibility = needStation ? Visibility.Visible : Visibility.Collapsed;

            // 不需要站号时给出明确说明，而不是留下一片空白让人以为功能缺失
            if (txtStationNotApplicable != null)
                txtStationNotApplicable.Visibility = needStation
                    ? Visibility.Collapsed
                    : Visibility.Visible;

            // 站号范围提示直接来自插件元信息，无需 UI 硬编码
            if (runStationHint != null) {
                string hint = descriptor != null ? descriptor.StationHint : null;
                runStationHint.Text = string.IsNullOrWhiteSpace(hint) ? "" : "  " + hint;
            }

            // 告知操作员当前配置需要填哪些字段，切换时字段变化便不再突兀
            if (txtTransportHint != null) {
                if (descriptor == null) {
                    txtTransportHint.Text = "";
                } else if (isSerial) {
                    txtTransportHint.Text = needStation
                        ? "串口连接：需填写串口号、波特率与站号"
                        : "串口连接：需填写串口号与波特率";
                } else {
                    txtTransportHint.Text = needStation
                        ? "以太网连接：需填写 IP、端口与站号"
                        : "以太网连接：需填写 IP 与端口";
                }
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
            if (cmbSerialPort != null) {
                // 先把已保存的值填上，再异步拉清单——顺序不能反：
                // 拉取要走一趟 RPC，等它回来再回填的话，
                // 面板会先空着一段时间，编辑既有设备时看起来像配置丢了。
                cmbSerialPort.Text = info.SerialPort ?? "";
                _ = LoadSerialPortsAsync(cmbSerialPort.Text);
            }
            if (txtBaudRate != null)
                txtBaudRate.Text = info.BaudRate > 0 ? info.BaudRate.ToString() : "9600";

            // 站号：设备级配置。变量地址因此可以保持干净（DT100 而非 01:DT100）
            if (txtStationNo != null)
                txtStationNo.Text = info.StationNo > 0 ? info.StationNo.ToString() : "1";

            // 依据当前选中协议重建介质列表，并还原设备已保存的连接方式
            ProtocolDescriptorDto current = GetSelectedDescriptor();
            LoadTransportList(current, info.TransportKind);
            ApplyTransportLayout(current, GetSelectedTransport());

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

            // 传输介质：取操作员在「连接方式」中的选择。
            // 协议只支持一种介质时该下拉框隐藏，此处自动取那唯一一项，
            // 因此不会留空导致服务端 Enum.TryParse 失败。
            string transportKind = GetSelectedTransport();
            bool   isSerial      = string.Equals(transportKind, "Serial", StringComparison.OrdinalIgnoreCase);
            d.TransportKind = isSerial ? "Serial" : "Tcp";

            if (isSerial) {
                // 串口路由：串口名走 SerialPort 字段，IP/端口留空。
                // 取 Text 而非 SelectedItem——下拉框是可编辑的，
                // 手工输入的设备名（宿主清单里没有的）同样必须被保留。
                d.SerialPort = cmbSerialPort != null ? (cmbSerialPort.Text ?? "").Trim() : "";

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
