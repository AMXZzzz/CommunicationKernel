// -----------------------------------------------------------------------------
// 文件: Services/Mes/IMesDataSource.cs
// 层级: UI 层 — MES 监控的数据来源
// 作用: 把「页面长什么样」和「数字从哪来」隔开。
//
// 为什么要这个接口：
//   监控页的产线、工艺流、报警在设备表与变量表里没有对应概念，得先经过
//   「产线配置」那份编排才成形。这个接口把边界划在编排之后——
//   页面只认 MesSnapshot，换实现时页面一行不用改。
//
//   实现是 MesDataSource。它<b>只算不读</b>：值来自变量表（由 VariablePoller
//   填），自己不发任何 I/O。实现里再去读一遍的话，同一个点位在监控页和
//   变量页会显示不同的值，而两边都报成功。
// -----------------------------------------------------------------------------

namespace CommunicationKernel.UI.WebMaster.Services;

/// <summary>MES 监控页的数据来源。</summary>
/// <remarks>
/// 实现必须是<b>线程安全</b>的：页面可能被多个浏览器会话同时打开，
/// 而 Blazor Server 的每个电路各有自己的渲染线程。
/// </remarks>
public interface IMesDataSource
{
    /// <summary>取一份当前快照。</summary>
    /// <returns>本次渲染要用的全部数据。</returns>
    /// <remarks>
    /// 每次渲染调一次，页面把结果存进局部变量。
    /// 实现不应在此方法里做 I/O——它在渲染路径上，阻塞会卡住整个电路。
    /// </remarks>
    MesSnapshot Snapshot();

    /// <summary>确认一条报警。</summary>
    /// <param name="alarmId">报警标识。找不到时静默返回。</param>
    /// <remarks>
    /// 确认<b>不等于</b>复位：确认只是「人已经看到并接手了」，
    /// 报警条目仍留在列表里，直到设备真正复位。
    /// 两者合并会让操作员点一下就把报警清掉，事后无从追溯。
    /// </remarks>
    void Acknowledge(string alarmId);

    /// <summary>确认某条产线上的全部报警。</summary>
    /// <param name="lineName">产线名。为空时不做任何事。</param>
    void AcknowledgeLine(string lineName);
}
