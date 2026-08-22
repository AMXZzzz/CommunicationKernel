#nullable disable

// -----------------------------------------------------------------------------
// 文件: Core/Enums/VariableAccess.cs
// 层级: UI 层 — WPF 核心枚举
// 作用: 描述变量读写权限，供变量配置页和写值逻辑校验使用。
// -----------------------------------------------------------------------------

namespace CommunicationKernel.UI.Wpf.Core.Enums;

/// <summary>变量访问权限。</summary>
public enum VariableAccess {
    /// <summary>只读：轮询可读取，界面禁止写入。</summary>
    ReadOnly  = 0,

    /// <summary>只写：界面可下发，不参与轮询读取。</summary>
    WriteOnly = 1,

    /// <summary>读写：既可轮询读取，也可通过 gRPC WriteAsync 下发。</summary>
    ReadWrite = 2
}
