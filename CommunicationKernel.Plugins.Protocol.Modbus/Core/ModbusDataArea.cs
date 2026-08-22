namespace CommunicationKernel.Plugins.Protocol.Modbus.Core;

/// <summary>
/// Modbus 四个标准数据区。
/// </summary>
/// <remarks>
/// 数据区<b>只</b>由地址决定，不受读取长度影响。
/// 历史实现曾用 <c>length == 1</c> 推断线圈，导致 <c>40001</c> 被当作线圈 0 号读取，
/// 返回完全不相干数据区的值且不报错。
/// </remarks>
public enum ModbusDataArea {
    /// <summary>0xxxx —— 线圈，位可读可写（FC01 读 / FC05 写）。</summary>
    Coil = 0,

    /// <summary>1xxxx —— 离散输入，位只读（FC02 读）。</summary>
    DiscreteInput = 1,

    /// <summary>4xxxx —— 保持寄存器，字可读可写（FC03 读 / FC06、FC10 写）。</summary>
    HoldingRegister = 2,

    /// <summary>3xxxx —— 输入寄存器，字只读（FC04 读）。</summary>
    InputRegister = 3
}

/// <summary>
/// <see cref="ModbusDataArea"/> 的语义查询扩展。
/// </summary>
public static class ModbusDataAreaExtensions {

    /// <summary>该数据区是否按位寻址（线圈与离散输入），否则按 16 位寄存器寻址。</summary>
    public static bool IsBitArea(this ModbusDataArea area)
        => area is ModbusDataArea.Coil or ModbusDataArea.DiscreteInput;

    /// <summary>该数据区是否可写。离散输入与输入寄存器为只读区。</summary>
    public static bool IsWritable(this ModbusDataArea area)
        => area is ModbusDataArea.Coil or ModbusDataArea.HoldingRegister;

    /// <summary>该数据区对应的读功能码。</summary>
    public static byte ReadFunctionCode(this ModbusDataArea area) => area switch {
        ModbusDataArea.Coil            => ModbusFunctionCode.ReadCoils,
        ModbusDataArea.DiscreteInput   => ModbusFunctionCode.ReadDiscreteInputs,
        ModbusDataArea.HoldingRegister => ModbusFunctionCode.ReadHoldingRegisters,
        ModbusDataArea.InputRegister   => ModbusFunctionCode.ReadInputRegisters,
        _                              => ModbusFunctionCode.ReadHoldingRegisters
    };

    /// <summary>该数据区的中文名称，用于错误消息。</summary>
    public static string DisplayName(this ModbusDataArea area) => area switch {
        ModbusDataArea.Coil            => "线圈 (0xxxx)",
        ModbusDataArea.DiscreteInput   => "离散输入 (1xxxx)",
        ModbusDataArea.HoldingRegister => "保持寄存器 (4xxxx)",
        ModbusDataArea.InputRegister   => "输入寄存器 (3xxxx)",
        _                              => area.ToString()
    };
}
