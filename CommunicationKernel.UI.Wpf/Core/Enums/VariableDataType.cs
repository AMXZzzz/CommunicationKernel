// -----------------------------------------------------------------------------
// 文件: Core/Enums/VariableDataType.cs
// 层级: UI 层 — 变量数据类型枚举
// 作用: 描述变量的数据类型，供变量配置页、VariableItem 模型和写值逻辑使用。
// -----------------------------------------------------------------------------

namespace CommunicationKernel.UI.Wpf.Core.Enums;

/// <summary>
/// 变量数据类型（协议无关）。
/// 插件通过 Capabilities 声明支持子集；UI 层仅展示与读写，不做协议转换。
/// </summary>
public enum VariableDataType {
    Bool   = 0,
    Int16  = 1,
    UInt16 = 2,
    Int32  = 3,
    UInt32 = 4,
    Int64  = 5,
    UInt64 = 6,
    Float  = 7,
    Double = 8,
    String = 9
}
