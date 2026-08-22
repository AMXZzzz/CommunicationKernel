namespace CommunicationKernel.Plugins.Protocol.Modbus.Core;

/// <summary>
/// Modbus 功能码常量。
/// </summary>
public static class ModbusFunctionCode {
    /// <summary>FC01 读线圈。</summary>
    public const byte ReadCoils = 0x01;

    /// <summary>FC02 读离散输入。</summary>
    public const byte ReadDiscreteInputs = 0x02;

    /// <summary>FC03 读保持寄存器。</summary>
    public const byte ReadHoldingRegisters = 0x03;

    /// <summary>FC04 读输入寄存器。</summary>
    public const byte ReadInputRegisters = 0x04;

    /// <summary>FC05 写单个线圈。</summary>
    public const byte WriteSingleCoil = 0x05;

    /// <summary>FC06 写单个保持寄存器。</summary>
    public const byte WriteSingleRegister = 0x06;

    /// <summary>FC0F 写多个线圈。</summary>
    public const byte WriteMultipleCoils = 0x0F;

    /// <summary>FC10 写多个保持寄存器。</summary>
    public const byte WriteMultipleRegisters = 0x10;

    /// <summary>异常响应标志位：响应功能码最高位置 1 表示异常。</summary>
    public const byte ExceptionMask = 0x80;
}

/// <summary>
/// Modbus 协议规定的数量与长度上限。
/// </summary>
/// <remarks>
/// 不做上限校验会构造出自相矛盾的畸形帧：
/// 例如 FC10 的字节数字段只有 1 字节，<c>(byte)260</c> 会静默截断成 4，
/// 而长度字段仍按 260 计算，设备收到后行为不可预测。
/// </remarks>
public static class ModbusLimits {
    /// <summary>FC01 / FC02 单次最多读取的位数量。</summary>
    public const int MaxReadBits = 2000;

    /// <summary>FC03 / FC04 单次最多读取的寄存器数量。</summary>
    public const int MaxReadRegisters = 125;

    /// <summary>FC10 单次最多写入的寄存器数量。</summary>
    public const int MaxWriteRegisters = 123;

    /// <summary>FC10 单次最多写入的字节数（<see cref="MaxWriteRegisters"/> × 2）。</summary>
    public const int MaxWriteBytes = MaxWriteRegisters * 2;

    /// <summary>FC0F 单次最多写入的线圈数量。</summary>
    public const int MaxWriteCoils = 1968;

    /// <summary>从站地址有效范围下限（0 为广播，本实现不支持）。</summary>
    public const byte MinUnitId = 1;

    /// <summary>从站地址有效范围上限（248-255 为保留值）。</summary>
    public const byte MaxUnitId = 247;
}
