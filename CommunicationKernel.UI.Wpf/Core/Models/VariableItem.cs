#nullable disable

// -----------------------------------------------------------------------------
// 文件: Core/Models/VariableItem.cs
// 层级: UI 层 — WPF 核心模型
// 作用: 本地变量定义，绑定 DataGrid / VariableTable；含地址、类型、轮询与最近读值。
// -----------------------------------------------------------------------------

using System;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using CommunicationKernel.UI.Wpf.Core.Enums;

namespace CommunicationKernel.UI.Wpf.Core.Models;

/// <summary>
/// 本地 PLC 变量定义，实现 INotifyPropertyChanged 以支持 UI 实时绑定。
/// 每个变量对应一个 gRPC Read/Write 操作的目标地址。
/// </summary>
public sealed class VariableItem : INotifyPropertyChanged {

    // WPF 数据绑定引擎订阅此事件以侦测属性变更
    public event PropertyChangedEventHandler PropertyChanged;

    // 属性变更时通知 DataGrid / 轮询结果列刷新
    private void Notify([CallerMemberName] string name = null)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));

    // ============================================================================
    // 标识
    // ============================================================================

    private string _id = Guid.NewGuid().ToString();
    /// <summary>变量唯一标识，使用 Guid 字符串。在应用生命周期内全局唯一。</summary>
    public string Id { get => _id; set { _id = value; Notify(); } }

    private string _deviceId = string.Empty;
    /// <summary>所属设备的路由 ID，对应 DeviceInfo.Id。读写时通过此字段确定目标路由。</summary>
    public string DeviceId { get => _deviceId; set { _deviceId = value; Notify(); } }

    // ============================================================================
    // 配置
    // ============================================================================

    private string _name = string.Empty;
    /// <summary>变量显示名称，例如 "传送带速度"。仅用于 UI 展示，不影响通信逻辑。</summary>
    public string Name { get => _name; set { _name = value; Notify(); } }

    private string _address = string.Empty;
    /// <summary>
    /// gRPC 数据地址字符串，传递给 HostClient.ReadAsync/WriteAsync。
    /// 格式取决于协议，例如 Modbus 为 "40001"，S7 为 "DB10.DBW0"。
    /// </summary>
    public string Address { get => _address; set { _address = value; Notify(); } }

    private VariableDataType _dataType = VariableDataType.Int16;
    /// <summary>数据类型。影响写入时的字节序列化方式。</summary>
    public VariableDataType DataType { get => _dataType; set { _dataType = value; Notify(); } }

    private VariableAccess _access = VariableAccess.ReadWrite;
    /// <summary>访问权限：只读、只写或读写均支持。</summary>
    public VariableAccess Access { get => _access; set { _access = value; Notify(); } }

    private int _length = 2;
    /// <summary>读取字节数，传递给 gRPC ReadRequest.Length。默认 2 字节（对应 int16）。</summary>
    public int Length { get => _length; set { _length = value; Notify(); } }

    private string _unit = string.Empty;
    /// <summary>工程单位，仅用于 UI 展示，例如 "mm/s", "°C"。</summary>
    public string Unit { get => _unit; set { _unit = value; Notify(); } }

    private string _category = string.Empty;
    /// <summary>分类标签，用于变量列表的分组或过滤，例如 "运动控制", "温度"。</summary>
    public string Category { get => _category; set { _category = value; Notify(); } }

    private string _description = string.Empty;
    /// <summary>变量描述，详细说明此变量的用途，供操作员参考。</summary>
    public string Description { get => _description; set { _description = value; Notify(); } }

    // ============================================================================
    // 运行时状态
    // ============================================================================

    private string _lastValue = string.Empty;
    /// <summary>最近一次读取到的值（字符串形式）。</summary>
    public string LastValue { get => _lastValue; set { _lastValue = value; Notify(); } }

    private string _lastError = string.Empty;
    /// <summary>最近一次读取的错误信息。成功时为空字符串。</summary>
    public string LastError { get => _lastError; set { _lastError = value; Notify(); } }

    // ============================================================================
    // 轮询配置
    // ============================================================================

    private bool _isPollingEnabled = false;
    /// <summary>是否启用轮询读取。true = 由轮询服务按 ScanRateMs 周期自动读取。</summary>
    public bool IsPollingEnabled { get => _isPollingEnabled; set { _isPollingEnabled = value; Notify(); } }

    private int _scanRateMs = 1000;
    /// <summary>轮询间隔（毫秒），仅在 IsPollingEnabled 为 true 时生效。默认 1000ms。</summary>
    public int ScanRateMs { get => _scanRateMs; set { _scanRateMs = value; Notify(); } }
}
