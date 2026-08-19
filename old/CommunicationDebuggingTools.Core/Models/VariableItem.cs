using System.ComponentModel;
using CommunicationDebuggingTools.Core.Enums;

namespace CommunicationDebuggingTools.Core.Models {
    /// <summary>
    /// 变量（点表）配置项。
    /// Address 为不透明字符串，仅由对应协议插件解析。
    /// </summary>
    public class VariableItem : INotifyPropertyChanged {
        public event PropertyChangedEventHandler PropertyChanged;

        private string _id;
        private string _deviceId;
        private string _name;
        private string _address;
        private VariableDataType _dataType;
        private VariableAccess _access;
        private int _length;
        private object _lastValue;
        private string _lastError;
        private DataQuality _quality;
        private string _unit;
        private string _category;
        private string _description;

        // ── 新增：轮询控制 ──────────────────────────────
        private int  _scanRateMs      = AppConfig.HeartbeatIntervalSeconds;
        private bool _isPollingEnabled = true;

        /// <summary>
        /// 轮询周期（毫秒）。最小 100 ms，默认 1000 ms。
        /// 轮询引擎按此值决定多久采集一次该变量。
        /// </summary>
        public int ScanRateMs {
            get => _scanRateMs;
            set {
                int v = value < 100 ? 100 : value;
                if (_scanRateMs == v) return;
                _scanRateMs = v;
                Raise(nameof(ScanRateMs));
            }
        }

        /// <summary>
        /// 是否参与周期轮询。false 时轮询引擎跳过该变量（仍可手动读写）。
        /// </summary>
        public bool IsPollingEnabled {
            get => _isPollingEnabled;
            set {
                if (_isPollingEnabled == value) return;
                _isPollingEnabled = value;
                Raise(nameof(IsPollingEnabled));
            }
        }

        // ── 现有属性（不变）────────────────────────────
        public string Id {
            get => _id;
            set { if (_id == value) return; _id = value; Raise(nameof(Id)); }
        }

        public string DeviceId {
            get => _deviceId;
            set { if (_deviceId == value) return; _deviceId = value; Raise(nameof(DeviceId)); }
        }

        public string Name {
            get => _name;
            set { if (_name == value) return; _name = value; Raise(nameof(Name)); }
        }

        public string Address {
            get => _address;
            set { if (_address == value) return; _address = value; Raise(nameof(Address)); }
        }

        public VariableDataType DataType {
            get => _dataType;
            set { if (_dataType == value) return; _dataType = value; Raise(nameof(DataType)); }
        }

        public VariableAccess Access {
            get => _access;
            set { if (_access == value) return; _access = value; Raise(nameof(Access)); }
        }

        public int Length {
            get => _length;
            set { if (_length == value) return; _length = value; Raise(nameof(Length)); }
        }

        public string Unit {
            get => _unit;
            set { if (_unit == value) return; _unit = value; Raise(nameof(Unit)); }
        }

        public string Category {
            get => _category;
            set { if (_category == value) return; _category = value; Raise(nameof(Category)); }
        }

        public string Description {
            get => _description;
            set { if (_description == value) return; _description = value; Raise(nameof(Description)); }
        }

        public object LastValue {
            get => _lastValue;
            set { if (Equals(_lastValue, value)) return; _lastValue = value; Raise(nameof(LastValue)); }
        }

        public string LastError {
            get => _lastError;
            set { if (_lastError == value) return; _lastError = value; Raise(nameof(LastError)); }
        }

        public DataQuality Quality {
            get => _quality;
            set { if (_quality == value) return; _quality = value; Raise(nameof(Quality)); }
        }

        public VariableItem () {
            _id = System.Guid.NewGuid().ToString("N");
            _deviceId = "";
            _name = "新变量";
            _address = "";
            _dataType = VariableDataType.Int16;
            _access = VariableAccess.ReadWrite;
            _length = 0;
            _unit = "";
            _category = "监控数据";
            _description = "";
            _lastError = "";
            _quality = DataQuality.Bad;
            _lastValue = null;
            _scanRateMs = 1000;
            _isPollingEnabled = true;
        }

        protected void Raise (string name) =>
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }
}