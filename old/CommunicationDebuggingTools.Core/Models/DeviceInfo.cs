using System.ComponentModel;
using CommunicationDebuggingTools.Core.Enums;

namespace CommunicationDebuggingTools.Core.Models {

    /// <summary>
    /// 设备信息模型（UI / 业务 / 持久化共用）。
    /// <para>
    /// 架构约定：
    /// 1. 连接共性字段：Ip、Port、<see cref="StationNo"/>（站号，界面与持久化统一用此属性）；
    /// 2. <see cref="ExtraSettingsJson"/> 仅作协议扩展透传（如 S7 rack/slot），Core/UI/Business 不解析内容；
    /// 3. 禁止再使用 ProtocolSettingsJson / unitId / station 等协议私有键名承载站号；
    /// 4. 字节序、字序、字符串编码为设备级默认，变量读写时可拷贝到 ProtocolDataMessage。
    /// </para>
    /// </summary>
    public class DeviceInfo : INotifyPropertyChanged {

        public event PropertyChangedEventHandler PropertyChanged;

        private string _id;
        private string _name;
        private string _model;
        private string _protocol;
        private string _ip;
        private int _port;
        private int _stationNo;
        private string _extraSettingsJson;
        private LaneType _lane;
        private DeviceStatusType _statusType;
        private bool _isConnected;
        private ByteOrder _byteOrder;
        private WordOrder _wordOrder;
        private StringEncodingKind _stringEncoding;

        /// <summary>唯一标识。新建时生成，持久化后不变。</summary>
        public string Id {
            get { return _id; }
            set {
                if (_id == value) return;
                _id = value;
                Raise(nameof(Id));
            }
        }

        /// <summary>设备显示名称（如产线位号、工位名）。</summary>
        public string Name {
            get { return _name; }
            set {
                if (_name == value) return;
                _name = value;
                Raise(nameof(Name));
            }
        }

        /// <summary>品牌 / 型号（纯展示，不参与协议逻辑）。</summary>
        public string Model {
            get { return _model; }
            set {
                if (_model == value) return;
                _model = value;
                Raise(nameof(Model));
            }
        }

        /// <summary>
        /// 协议显示名，须与插件 <c>[ProtocolName]</c> 标注名、解析器注册名完全一致
        /// （如 "Modbus TCP"、"Panasonic MEWTOCOL"）。
        /// </summary>
        public string Protocol {
            get { return _protocol; }
            set {
                if (_protocol == value) return;
                _protocol = value;
                Raise(nameof(Protocol));
            }
        }

        /// <summary>设备 IP 或主机名。</summary>
        public string Ip {
            get { return _ip; }
            set {
                if (_ip == value) return;
                _ip = value;
                Raise(nameof(Ip));
            }
        }

        /// <summary>通信端口。0 表示未配置；插件可在连接时使用协议默认端口。</summary>
        public int Port {
            get { return _port; }
            set {
                if (_port == value) return;
                _port = value;
                Raise(nameof(Port));
            }
        }

        /// <summary>
        /// 站号（架构级共性字段）。
        /// UI 标签统一为「站号」；连接时拷贝到 <see cref="ProtocolConnectionContext.StationNo"/>。
        /// 站号（Modbus 从站、松下站号等）；仅插件解释语义。默认 1。
        /// </summary>
        public int StationNo {
            get { return _stationNo; }
            set {
                if (_stationNo == value) return;
                _stationNo = value;
                Raise(nameof(StationNo));
            }
        }

        /// <summary>
        /// 协议扩展参数 JSON（透传）。
        /// 一期 UI 不编辑，默认 "{}"。仅插件在需要时解析（如 S7 的 rack/slot）。
        /// 站号不得写入本字段。
        /// </summary>
        public string ExtraSettingsJson {
            get { return _extraSettingsJson; }
            set {
                string v = value ?? "{}";
                if (_extraSettingsJson == v) return;
                _extraSettingsJson = v;
                Raise(nameof(ExtraSettingsJson));
            }
        }

        /// <summary>单轨 / 双轨（产线业务属性，与通信协议无关）。</summary>
        public LaneType Lane {
            get { return _lane; }
            set {
                if (_lane == value) return;
                _lane = value;
                Raise(nameof(Lane));
                Raise(nameof(IsDualLane));
            }
        }

        /// <summary>运行状态枚举；变更时同步通知 <see cref="StatusText"/>。</summary>
        public DeviceStatusType StatusType {
            get { return _statusType; }
            set {
                if (_statusType == value) return;
                _statusType = value;
                Raise(nameof(StatusType));
                Raise(nameof(StatusText));
            }
        }

        /// <summary>是否已建立协议会话（与 StatusType 配合，由 DeviceService 维护）。</summary>
        public bool IsConnected {
            get { return _isConnected; }
            set {
                if (_isConnected == value) return;
                _isConnected = value;
                Raise(nameof(IsConnected));
            }
        }

        /// <summary>设备默认字节序（变量一期继承）。</summary>
        public ByteOrder ByteOrder {
            get { return _byteOrder; }
            set {
                if (_byteOrder == value) return;
                _byteOrder = value;
                Raise(nameof(ByteOrder));
            }
        }

        /// <summary>设备默认字序（多寄存器组合时的高低字顺序）。</summary>
        public WordOrder WordOrder {
            get { return _wordOrder; }
            set {
                if (_wordOrder == value) return;
                _wordOrder = value;
                Raise(nameof(WordOrder));
            }
        }

        /// <summary>设备默认字符串编码。</summary>
        public StringEncodingKind StringEncoding {
            get { return _stringEncoding; }
            set {
                if (_stringEncoding == value) return;
                _stringEncoding = value;
                Raise(nameof(StringEncoding));
            }
        }

        /// <summary>状态展示文案（绑定用，由 StatusType 推导）。</summary>
        public string StatusText {
            get {
                switch (StatusType) {
                    case DeviceStatusType.Success: return "RUN";
                    case DeviceStatusType.Connecting: return "连接中...";
                    case DeviceStatusType.Warning: return "警告";
                    case DeviceStatusType.Error: return "ALARM";
                    default: return "离线";
                }
            }
        }

        /// <summary>是否双轨（与 <see cref="Lane"/> 同步，便于绑定）。</summary>
        public bool IsDualLane {
            get { return Lane == LaneType.Dual; }
            set { Lane = value ? LaneType.Dual : LaneType.Single; }
        }

        /// <summary>
        /// 新建设备默认值：站号 1，扩展 JSON 为空对象，协议名占位为 Modbus TCP，
        /// 状态离线，序与编码取常用默认。
        /// </summary>
        public DeviceInfo () {
            _id = System.Guid.NewGuid().ToString("N");
            _name = "新设备";
            _model = "";
            _protocol = "Modbus TCP";
            _ip = "192.168.0.1";
            _port = 502;
            _stationNo = 1;
            _extraSettingsJson = "{}";
            _lane = LaneType.Single;
            _statusType = DeviceStatusType.Offline;
            _isConnected = false;
            _byteOrder = ByteOrder.BigEndian;
            _wordOrder = WordOrder.HighWordFirst;
            _stringEncoding = StringEncodingKind.Utf8;
        }

        /// <summary>触发属性变更通知；UI 绑定依赖此路径刷新。</summary>
        protected void Raise (string name) {
            PropertyChangedEventHandler h = PropertyChanged;
            if (h != null)
                h(this, new PropertyChangedEventArgs(name));
        }
    }
}