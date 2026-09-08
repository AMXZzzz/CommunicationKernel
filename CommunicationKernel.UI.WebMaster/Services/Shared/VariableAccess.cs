// -----------------------------------------------------------------------------
// 文件: Services/Shared/VariableAccess.cs
// 层级: UI 层 — Blazor Server（模板与变量共用）
// 作用: 一条功能／变量的读写方向。
//
// 为什么不是一个布尔：
//   最初只有「只读」一个开关，但现场有三类点位而不是两类——
//   脉冲型的启动/停止/复位是<b>只写</b>的：PLC 收到上升沿就动作并自行清零，
//   回读永远是 0。给它开轮询只会得到一列恒为 0 的假数据，
//   操作员反而会怀疑设备没响应。
//
//   方向写在模板上而不是逐台设备标：一台变频器哪些能写、哪些只能读，
//   是这类设备的固有属性，不因装在哪条线上而不同。
//
// 这是<b>界面护栏，不是安全边界</b>：真正的读写权限在 PLC 侧。
// 它防的是操作员手滑往测量值里写数、或对着只写点位纳闷为什么读不出东西。
// -----------------------------------------------------------------------------

namespace CommunicationKernel.UI.WebMaster.Services;

/// <summary>一条功能／变量允许的操作方向。</summary>
public enum VariableAccess
{
    /// <summary>可读写。绝大多数点位，也是默认值。</summary>
    /// <remarks>
    /// 显式定为 0：旧配置里没有这个字段时反序列化会落到 0，
    /// 而「可读写」正是最宽松、不会让既有功能突然点不动的那一个。
    /// </remarks>
    ReadWrite = 0,

    /// <summary>只读。测量值，例如输出频率、输出电流、报警码。</summary>
    ReadOnly = 1,

    /// <summary>只写。脉冲型指令，回读无意义，因此连轮询一起禁用。</summary>
    WriteOnly = 2,
}

/// <summary>访问方向的界面辅助。</summary>
public static class VariableAccessInfo
{
    /// <summary>下拉框与只读展示共用的中文名。</summary>
    public static string Label(VariableAccess a) => a switch
    {
        VariableAccess.ReadOnly => "只读",
        VariableAccess.WriteOnly => "只写",
        _ => "可读写",
    };

    /// <summary>是否允许读取（含轮询）。</summary>
    public static bool CanRead(VariableAccess a) => a != VariableAccess.WriteOnly;

    /// <summary>是否允许写入。</summary>
    public static bool CanWrite(VariableAccess a) => a != VariableAccess.ReadOnly;

    /// <summary>下拉框的全部取值，顺序即默认在前。</summary>
    public static readonly VariableAccess[] All =
    {
        VariableAccess.ReadWrite,
        VariableAccess.ReadOnly,
        VariableAccess.WriteOnly,
    };
}
