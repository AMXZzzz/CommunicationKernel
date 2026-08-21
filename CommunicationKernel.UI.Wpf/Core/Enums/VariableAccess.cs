#nullable disable

// -----------------------------------------------------------------------------
// 文件: Core/Enums/VariableAccess.cs
// 层级: UI 层 — 变量访问权限枚举
// 作用: 描述变量支持的读写操作类型，供变量配置页和写值逻辑使用。
// -----------------------------------------------------------------------------

namespace CommunicationKernel.UI.Wpf.Core.Enums;

/// <summary>变量访问权限。</summary>
public enum VariableAccess {
    ReadOnly  = 0,
    WriteOnly = 1,
    ReadWrite = 2
}
