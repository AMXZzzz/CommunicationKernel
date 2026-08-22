#nullable disable

// -----------------------------------------------------------------------------
// 文件: Core/Enums/VariableDataType.cs
// 层级: UI 层 — WPF 核心枚举
// 作用: 描述变量数据类型，供配置页、ValueParser 与写值序列化使用。
// -----------------------------------------------------------------------------

namespace CommunicationKernel.UI.Wpf.Core.Enums;

/// <summary>
/// 变量数据类型（协议无关）。
/// 插件通过 Capabilities 声明支持子集；UI 层仅展示与读写，不做协议转换。
/// </summary>
public enum VariableDataType {
    /// <summary>布尔，通常占 1 字节。</summary>
    Bool   = 0,

    /// <summary>有符号 16 位整数，默认读取长度 2 字节。</summary>
    Int16  = 1,

    /// <summary>无符号 16 位整数。</summary>
    UInt16 = 2,

    /// <summary>有符号 32 位整数。</summary>
    Int32  = 3,

    /// <summary>无符号 32 位整数。</summary>
    UInt32 = 4,

    /// <summary>有符号 64 位整数。</summary>
    Int64  = 5,

    /// <summary>无符号 64 位整数。</summary>
    UInt64 = 6,

    /// <summary>单精度浮点（IEEE 754）。</summary>
    Float  = 7,

    /// <summary>双精度浮点（IEEE 754）。</summary>
    Double = 8,

    /// <summary>UTF-8 字符串，长度由变量 Length 决定。</summary>
    String = 9
}
