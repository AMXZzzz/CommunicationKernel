#nullable disable

// 文件: Core/Models/DeviceInfo.cs
// 层级: UI层 — 核心模型
// 作用: 设备/路由的 UI 数据模型，实现 INotifyPropertyChanged 以支持 WPF 数据绑定。
//       属性值来自 gRPC RouteDto（Id/Protocol/Ip/Port/Station/TransportKind）
//       以及本地存储（Name/Model/IsDualLane 等不在 gRPC 中持久化的字段）。

using System;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using CommunicationKernel.UI.Wpf.Core.Enums;

namespace CommunicationKernel.UI.Wpf.Core.Models
{
    /// <summary>
    /// 设备/路由的 UI 数据模型。
    /// 对应 gRPC 的 RouteDto，同时扩展了本地显示字段。
    /// 实现 INotifyPropertyChanged 以便 WPF 绑定引擎感知属性变更。
    /// </summary>
    public sealed class DeviceInfo : INotifyPropertyChanged
    {
        // -------------------------------------------------------------------------
        // INotifyPropertyChanged 实现
        // -------------------------------------------------------------------------

        /// <summary>WPF 数据绑定引擎订阅此事件侦测属性变更。</summary>
        public event PropertyChangedEventHandler PropertyChanged;

        /// <summary>
        /// 属性 setter 辅助：仅在值实际改变时才触发通知，避免冗余刷新。
        /// </summary>
        /// <typeparam name="T">属性类型。</typeparam>
        /// <param name="field">后备字段引用。</param>
        /// <param name="value">新值。</param>
        /// <param name="name">属性名（编译器自动填充）。</param>
        /// <returns>true = 值已改变；false = 值未改变。</returns>
        private bool SetField<T>(ref T field, T value, [CallerMemberName] string name = null)
        {
            // 使用默认相等比较，值相同则跳过通知
            if (System.Collections.Generic.EqualityComparer<T>.Default.Equals(field, value))
                return false;

            // 更新字段并触发通知
            field = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
            return true;
        }

        // -------------------------------------------------------------------------
        // 后备字段
        // -------------------------------------------------------------------------

        private string _id             = string.Empty;
        private string _name           = string.Empty;
        private string _model          = string.Empty;
        private string _protocol       = string.Empty;
        private string _ip             = string.Empty;
        private int    _port;
        private string _station        = string.Empty;
        private int    _stationNo;
        private string _transportKind  = string.Empty;
        private string _serialPort     = string.Empty;
        private int    _baudRate;
        private string _extraSettingsJson = "{}";
        private bool   _isConnected;
        private DeviceStatusType _statusType = DeviceStatusType.Offline;
        private bool   _isDualLane;

        // -------------------------------------------------------------------------
        // 属性 — 来自 gRPC RouteDto
        // -------------------------------------------------------------------------

        /// <summary>
        /// 路由唯一标识，对应 RouteDto.RouteId。
        /// 在 EngineHost 内全局唯一，由 RegisterRoute 时指定或自动生成。
        /// </summary>
        public string Id
        {
            get => _id;
            set => SetField(ref _id, value);
        }

        /// <summary>
        /// 设备显示名称，本地存储，不通过 gRPC 传输。
        /// 用于在 UI 上显示用户友好的设备标签。
        /// </summary>
        public string Name
        {
            get => _name;
            set => SetField(ref _name, value);
        }

        /// <summary>
        /// 设备型号，本地存储，不通过 gRPC 传输。
        /// 例如 "S7-1200 CPU 1214C"。
        /// </summary>
        public string Model
        {
            get => _model;
            set => SetField(ref _model, value);
        }

        /// <summary>
        /// 协议插件标识，对应 RouteDto.ProtocolId。
        /// 例如 "modbus.tcp"、"siemens.s7"。
        /// </summary>
        public string Protocol
        {
            get => _protocol;
            set => SetField(ref _protocol, value);
        }

        /// <summary>
        /// 目标 IP 地址，对应 RouteDto.Address。
        /// 串口模式下为串口名称（如 "COM3"）。
        /// </summary>
        public string Ip
        {
            get => _ip;
            set => SetField(ref _ip, value);
        }

        /// <summary>
        /// TCP 端口号，对应 RouteDto.Port。
        /// 串口模式下为 0。
        /// </summary>
        public int Port
        {
            get => _port;
            set => SetField(ref _port, value);
        }

        /// <summary>
        /// 站号字符串，对应 RouteDto.Station。
        /// Modbus 从站 ID / S7 机架槽位 / Mewtocol 站号等，原始字符串形式。
        /// </summary>
        public string Station
        {
            get => _station;
            set
            {
                // 更新字符串字段
                SetField(ref _station, value);
                // 同步解析为 int，供数字类型使用
                if (int.TryParse(value, out int n))
                    StationNo = n;
            }
        }

        /// <summary>
        /// 站号的整数形式，由 Station 属性 setter 自动解析。
        /// 供需要数字站号的协议使用。
        /// </summary>
        public int StationNo
        {
            get => _stationNo;
            set => SetField(ref _stationNo, value);
        }

        /// <summary>
        /// 传输介质类型，对应 RouteDto.TransportKind。
        /// 例如 "TCP"、"SerialPort"。
        /// </summary>
        public string TransportKind
        {
            get => _transportKind;
            set => SetField(ref _transportKind, value);
        }

        /// <summary>
        /// 串口名称（如 "COM3"），对应 RouteDto.SerialPort。
        /// 仅串口类协议（modbus-rtu / modbus-ascii）使用；TCP 路由为空字符串。
        /// </summary>
        public string SerialPort
        {
            get => _serialPort;
            set => SetField(ref _serialPort, value);
        }

        /// <summary>
        /// 串口波特率，对应 RouteDto.BaudRate。
        /// 仅串口类协议使用；TCP 路由为 0。
        /// </summary>
        public int BaudRate
        {
            get => _baudRate;
            set => SetField(ref _baudRate, value);
        }

        /// <summary>
        /// 扩展设置 JSON，本地存储，不通过 gRPC 传输。
        /// 用于保存协议特定的额外参数，默认为空对象 "{}"。
        /// </summary>
        public string ExtraSettingsJson
        {
            get => _extraSettingsJson;
            set => SetField(ref _extraSettingsJson, value ?? "{}");
        }

        // -------------------------------------------------------------------------
        // 属性 — 运行时状态（通知属性，由 WatchRouteStatus 流更新）
        // -------------------------------------------------------------------------

        /// <summary>
        /// 是否已连接（通信正常）。
        /// 由 WatchRouteStatus 流的 Online 字段驱动。
        /// </summary>
        public bool IsConnected
        {
            get => _isConnected;
            set
            {
                // 更新连接标志
                SetField(ref _isConnected, value);
                // 连接状态改变时同步触发 StatusText 的 UI 更新
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(StatusText)));
            }
        }

        /// <summary>
        /// 设备状态类型枚举。
        /// 影响状态指示灯颜色和 StatusText 文字。
        /// </summary>
        public DeviceStatusType StatusType
        {
            get => _statusType;
            set
            {
                // 更新状态枚举值
                SetField(ref _statusType, value);
                // 状态改变时同步触发 StatusText 的 UI 更新
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(StatusText)));
            }
        }

        /// <summary>
        /// 状态显示文字，派生自 StatusType，无后备字段。
        /// 绑定到状态标签控件，无需手动设置。
        /// </summary>
        public string StatusText
        {
            get
            {
                // 根据状态枚举返回对应的中文显示文字
                switch (_statusType)
                {
                    case DeviceStatusType.Success:
                        return "已连接";
                    case DeviceStatusType.Connecting:
                        return "连接中";
                    case DeviceStatusType.Warning:
                        return "警告";
                    case DeviceStatusType.Error:
                        return "错误";
                    default:
                        // Offline 及未知状态均显示离线
                        return "离线";
                }
            }
        }

        // -------------------------------------------------------------------------
        // 属性 — 本地标志（不在 gRPC 中）
        // -------------------------------------------------------------------------

        /// <summary>
        /// 是否为双轨设备，本地标志，不通过 gRPC 传输。
        /// 影响 Lane 显示文字。
        /// </summary>
        public bool IsDualLane
        {
            get => _isDualLane;
            set
            {
                // 更新双轨标志
                SetField(ref _isDualLane, value);
                // 同步触发 Lane 属性的 UI 更新
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Lane)));
            }
        }

        /// <summary>
        /// 轨道类型显示文字，派生自 IsDualLane，无后备字段。
        /// "双轨" 或 "单轨"。
        /// </summary>
        public string Lane
        {
            get
            {
                // 双轨返回"双轨"，单轨返回"单轨"
                return _isDualLane ? "双轨" : "单轨";
            }
        }
    }
}
