namespace CommunicationDebuggingTools.Core.Enums {
    /// <summary>
    /// 寄存器内字节序（一个 16 位字里两个字节的顺序）
    /// </summary>
    public enum ByteOrder {
        /// <summary>高字节在前（大端，Modbus 常见）</summary>
        BigEndian = 0,

        /// <summary>低字节在前（小端）</summary>
        LittleEndian = 1
    }

    /// <summary>
    /// 多寄存器字序（float / int32 等跨多个字时的顺序）
    /// </summary>
    public enum WordOrder {
        /// <summary>高字在前</summary>
        HighWordFirst = 0,

        /// <summary>低字在前</summary>
        LowWordFirst = 1
    }
}