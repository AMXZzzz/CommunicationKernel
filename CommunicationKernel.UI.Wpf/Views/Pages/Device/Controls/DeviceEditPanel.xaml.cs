#nullable disable

// -----------------------------------------------------------------------------
// 文件: Views/Pages/Device/Controls/DeviceEditPanel.xaml.cs
// 层级: UI 层 — WPF Views
// 作用: 设备新增/编辑面板；按协议描述符切换 TCP/串口表单，不解析协议语义。
// -----------------------------------------------------------------------------

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
        /// <summary>请求收起面板，本次编辑不保存。</summary>
        public event Action CloseRequested;

        /// <summary>请求保存。面板本身不落盘，由页面取 <see cref="BuildDeviceInfo"/> 的结果去写。</summary>
        public event Action SaveRequested;

        /// <summary>请求删除当前编辑的设备。新增态下按钮不可用，不会触发。</summary>
        public event Action DeleteRequested;

        /// <summary>正在编辑的设备 Id；为空表示新增。决定 <see cref="IsNew"/> 与保存时是否保留 Id。</summary>
        private string _editingId;

        /// <summary>是否正处于标题栏拖动中。</summary>
        private bool _dragging;

        /// <summary>拖动起点（屏幕坐标）。</summary>
        private Point _dragStart;

        /// <summary>拖动开始时的 X 偏移，用于累加而非从零重算。</summary>
        private double _originX;

        /// <summary>拖动开始时的 Y 偏移。</summary>
        private double _originY;

        /// <summary>双轨设备标记。界面上是两个互斥按钮，没有对应的输入控件，故用字段暂存。</summary>
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
        /// 清单来自 <b>Hosting.App 所在的机器</b>。宿主跑在树莓派时，
        /// 操作员要选的是树莓派上的 /dev/ttyUSB0，而不是本机的 COM1。
        /// 为 null、宿主不可达或现场无串口时，下拉框留空但仍可手工输入。
        /// </remarks>
        public ISerialPortProvider SerialPortProvider { get; set; }

        /// <summary>构造：解析 XAML，构建视觉树。</summary>
        public DeviceEditPanel () {
            // 解析 XAML，构建视觉树
            InitializeComponent();
        }

        /// <summary>当前是否为「新增设备」，由页面决定标题与删除按钮的可用性。</summary>
        public bool IsNew => string.IsNullOrEmpty(_editingId);

        // ============================================================================
        // 串口清单
        // ============================================================================

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
                // 需回到 UI 线程操作控件
                ports = await SerialPortProvider
                    .GetPortsAsync(System.Threading.CancellationToken.None)
                    .ConfigureAwait(true);
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
        /// <param name="sender">事件源。</param>
        /// <param name="e">事件参数。</param>
        private void OnSerialPortSelected (object sender, SelectionChangedEventArgs e) {
            if (cmbSerialPort?.SelectedItem is ComboBoxItem item && item.Tag is string portName)
                cmbSerialPort.Text = portName;
        }

        // ============================================================================
        // 协议 / 介质
        // ============================================================================

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
        /// <param name="sender">事件源。</param>
        /// <param name="e">事件参数。</param>
        private void CmbProtocol_SelectionChanged (object sender, SelectionChangedEventArgs e) {
            ApplyProtocolLayout(GetSelectedDescriptor());
        }

        /// <summary>
        /// 填充「连接方式」下拉框。
        /// 协议只支持一种介质时隐藏该选择器，避免无意义的单选项。
        /// </summary>
        /// <param name="descriptor">当前协议描述符；为 null 时按仅支持 TCP 处理。</param>
        /// <param name="preferred">希望保留的介质标识，来自设备已存配置或切换协议前的选择。</param>
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
        /// <param name="kind">介质标识，如 Tcp / Serial。</param>
        /// <returns>可读文案；未知标识原样返回，不隐藏它。</returns>
        private static string DescribeTransport (string kind) {
            // 串口 / 以太网给出操作员可读文案，其它标识原样返回
            if (string.Equals(kind, "Serial", StringComparison.OrdinalIgnoreCase))
                return "串口直连（RS-232 / RS-485）";
            if (string.Equals(kind, "Tcp", StringComparison.OrdinalIgnoreCase))
                return "以太网（含 TCP 转串口透传装置）";
            return kind;
        }

        /// <summary>取当前选中的介质标识；无选择时回落到协议默认介质。</summary>
        /// <returns>介质标识，最终兜底为 <c>"Tcp"</c>，绝不返回空串——空串会让服务端解析枚举失败。</returns>
        private string GetSelectedTransport () {
            ComboBoxItem item = cmbTransport != null
                ? cmbTransport.SelectedItem as ComboBoxItem
                : null;

            string kind = item != null ? item.Tag as string : null;
            // 有明确选择就用它，否则回落到协议默认介质
            if (!string.IsNullOrWhiteSpace(kind))
                return kind;

            ProtocolDescriptorDto d = GetSelectedDescriptor();
            return d != null ? d.DefaultTransport : "Tcp";
        }

        /// <summary>连接方式变化：仅切换连接参数表单，不改变协议选择。</summary>
        /// <param name="sender">事件源。</param>
        /// <param name="e">事件参数。</param>
        private void CmbTransport_SelectionChanged (object sender, SelectionChangedEventArgs e) {
            // 重建下拉期间抑制，避免中间态刷新布局
            if (_suppressTransportEvent) return;
            ApplyTransportLayout(GetSelectedDescriptor(), GetSelectedTransport());
        }

        /// <summary>协议变化后重建介质列表并重排表单。</summary>
        /// <param name="descriptor">新选中的协议描述符，可为 null。</param>
        private void ApplyProtocolLayout (ProtocolDescriptorDto descriptor) {
            // 协议变化时重建介质列表，尽量保留操作员此前的选择
            LoadTransportList(descriptor, GetSelectedTransportTagOrNull());
            ApplyTransportLayout(descriptor, GetSelectedTransport());
        }

        /// <summary>读取当前介质选择，用于协议切换时尽量保留；无选择返回 null。</summary>
        /// <returns>介质标识，或 null。与 <see cref="GetSelectedTransport"/> 的区别是<b>不兜底</b>——
        /// 调用方需要区分「操作员真的选过」和「只是默认值」。</returns>
        private string GetSelectedTransportTagOrNull () {
            ComboBoxItem item = cmbTransport != null
                ? cmbTransport.SelectedItem as ComboBoxItem
                : null;
            return item != null ? item.Tag as string : null;
        }

        /// <summary>
        /// 按「所选介质」渲染连接参数表单，并按「所选协议」决定站号可见性。
        /// </summary>
        /// <remarks>
        /// descriptor 为 null（协议列表尚未就绪）时退化为「显示站号」的通用布局：
        /// 多显示一个字段远好过少显示——少了操作员根本不知道还需要填。
        /// </remarks>
        /// <param name="descriptor">当前协议描述符，可为 null。</param>
        /// <param name="transportKind">当前介质标识。</param>
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
        /// <returns>描述符，或 null。</returns>
        private ProtocolDescriptorDto GetSelectedDescriptor () {
            ComboBoxItem item = cmbProtocol != null
                ? cmbProtocol.SelectedItem as ComboBoxItem
                : null;
            return item != null ? item.Tag as ProtocolDescriptorDto : null;
        }

        // ============================================================================
        // 载入 / 构建
        // ============================================================================

        /// <summary>载入设备到表单。</summary>
        /// <param name="info">要编辑的设备；为 null 时按空白新增处理。</param>
        /// <param name="isNew">true 表示新增（忽略 info.Id），false 表示编辑既有设备。</param>
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
        /// <remarks>
        /// 只做「界面字段 → 数据字段」的搬运和缺省兜底，<b>不校验业务合法性</b>
        /// （名称是否为空、IP 是否可达都由页面判断）。也不解析地址或协议语义。
        /// </remarks>
        /// <returns>可直接落盘/注册路由的设备对象。编辑态保留原 Id，新增态由存储层分配。</returns>
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

        /// <summary>按状态给状态文字上色，颜色取自主题资源。</summary>
        /// <param name="type">设备状态。未列出的状态回落到次要文本色。</param>
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
            } catch {
                // 主题资源缺失时保持默认前景色
            }
        }

        /// <summary>
        /// 按 ProtocolId 选中下拉项（编辑既有设备时还原选择）。
        /// 匹配依据是 Tag 中的 ProtocolId，不是展示名。
        /// </summary>
        /// <param name="protocolId">要选中的协议标识；为空或未匹配时回落到第一项。</param>
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
        /// <returns>ProtocolId，或空串。</returns>
        private string GetSelectedProtocolId () {
            ProtocolDescriptorDto d = GetSelectedDescriptor();
            return d != null ? d.ProtocolId : "";
        }

        // ============================================================================
        // 轨道 / 按钮
        // ============================================================================

        /// <summary>
        /// 把「单轨 / 双轨」两个按钮刷成当前选择的样式。
        /// </summary>
        /// <remarks>
        /// 这两个按钮扮演的是单选组，但没有用 RadioButton——主题样式挂在 Button 上。
        /// 因此选中态只能靠手工换 Style 表达。
        /// </remarks>
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

        /// <summary>设置单轨/双轨并刷新按钮样式。</summary>
        /// <param name="dual">true 为双轨。</param>
        private void SetLane (bool dual) {
            _isDual = dual;
            UpdateLaneButtons();
        }

        /// <summary>「单轨」按钮。</summary>
        /// <param name="sender">事件源。</param>
        /// <param name="e">事件参数。</param>
        private void BtnLaneSingle_Click (object sender, RoutedEventArgs e) => SetLane(false);

        /// <summary>「双轨」按钮。</summary>
        /// <param name="sender">事件源。</param>
        /// <param name="e">事件参数。</param>
        private void BtnLaneDual_Click (object sender, RoutedEventArgs e) => SetLane(true);

        /// <summary>右上角关闭按钮。</summary>
        /// <param name="sender">事件源。</param>
        /// <param name="e">事件参数。</param>
        private void BtnClose_Click (object sender, RoutedEventArgs e) => CloseRequested?.Invoke();

        /// <summary>点击遮罩区域关闭。与上面同名但签名不同，供 XAML 的鼠标事件绑定。</summary>
        /// <param name="sender">事件源。</param>
        /// <param name="e">事件参数。</param>
        private void BtnClose_Click (object sender, MouseButtonEventArgs e) => CloseRequested?.Invoke();

        /// <summary>「保存」按钮。面板只发事件，落盘由页面完成。</summary>
        /// <param name="sender">事件源。</param>
        /// <param name="e">事件参数。</param>
        private void BtnSave_Click (object sender, RoutedEventArgs e) => SaveRequested?.Invoke();

        /// <summary>「删除」按钮。二次确认由页面弹，面板不自行判断。</summary>
        /// <param name="sender">事件源。</param>
        /// <param name="e">事件参数。</param>
        private void BtnDelete_Click (object sender, RoutedEventArgs e) => DeleteRequested?.Invoke();

        // ============================================================================
        // 拖动
        // ============================================================================

        /// <summary>打开弹窗时复位拖动偏移（回到遮罩居中）。</summary>
        public void ResetPosition () {
            if (panelTranslate != null) {
                panelTranslate.X = 0;
                panelTranslate.Y = 0;
            }
            _dragging = false;
        }

        /// <summary>标题栏按下：整条色块开始拖动（关闭按钮已被独立命中）。</summary>
        /// <remarks>
        /// 向上遍历视觉树排除按钮：不排除的话，按住关闭按钮轻微移动就会变成拖窗口，
        /// 松手时点击丢失，表现为「关不掉」。
        /// </remarks>
        /// <param name="sender">事件源。</param>
        /// <param name="e">事件参数。</param>
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

        /// <summary>拖动中：按位移更新平移量。</summary>
        /// <param name="sender">事件源。</param>
        /// <param name="e">事件参数。</param>
        private void TitleBar_MouseMove (object sender, MouseEventArgs e) {
            // 未进入拖动或已松开左键则忽略
            if (!_dragging || e.LeftButton != MouseButtonState.Pressed)
                return;
            Point p = e.GetPosition(null);
            if (panelTranslate != null) {
                panelTranslate.X = _originX + (p.X - _dragStart.X);
                panelTranslate.Y = _originY + (p.Y - _dragStart.Y);
            }
        }

        /// <summary>松开左键：结束拖动。</summary>
        /// <param name="sender">事件源。</param>
        /// <param name="e">事件参数。</param>
        private void TitleBar_MouseLeftButtonUp (object sender, MouseButtonEventArgs e) {
            StopDrag();
        }

        /// <summary>
        /// 鼠标捕获丢失：同样结束拖动。
        /// </summary>
        /// <remarks>
        /// 光标被拖出窗口或系统抢走捕获时不会触发 MouseUp，
        /// 只处理 MouseUp 的话面板会一直粘在光标上。
        /// </remarks>
        /// <param name="sender">事件源。</param>
        /// <param name="e">事件参数。</param>
        private void TitleBar_LostMouseCapture (object sender, MouseEventArgs e) {
            StopDrag();
        }

        /// <summary>结束拖动并释放鼠标捕获。可重复调用。</summary>
        private void StopDrag () {
            // 未在拖动中无需释放捕获
            if (!_dragging)
                return;
            _dragging = false;
            if (titleBar != null && titleBar.IsMouseCaptured)
                titleBar.ReleaseMouseCapture();
        }

    }
}
