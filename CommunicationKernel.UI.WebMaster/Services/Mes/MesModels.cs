// -----------------------------------------------------------------------------
// 文件: Services/Mes/MesModels.cs
// 层级: UI 层 — MES 监控的视图模型
// 作用: 描述监控页要渲染的东西，与「这些值从哪来」彻底解耦。
//
// 为什么单独建一层模型：
//   监控页要展示的概念——产线、工艺流工站、离散设备、活动报警——在现有的
//   设备表和变量表里<b>一个都不存在</b>。设备表只知道路由、协议、端点，
//   变量表只知道地址和读值，没有任何地方记录「谁在哪条线上、排第几站、
//   哪个点位代表运行状态」。
//
//   所以先把页面要的形状定下来，由 IMesDataSource 提供数据。
//   当前实现是 DemoMesDataSource（演示数据），等产线配置和点位绑定做完，
//   换一个读真实设备与变量的实现即可，页面一行都不用改。
//
// 这些类型<b>只是形状</b>，不含任何业务规则，也不碰通讯层。
// -----------------------------------------------------------------------------

namespace CommunicationKernel.UI.WebMaster.Services;

/// <summary>产线或工站的运行状态。</summary>
/// <remarks>
/// 四档而不是「在线/离线」两档：现场真正要分开处置的是四种情况——
/// 在跑、被人停了、报警停机、根本连不上。合成两档会让「有人按了急停」
/// 和「网线掉了」显示成同一个样子。
/// </remarks>
public enum MesState
{
    /// <summary>运行中。</summary>
    Run,

    /// <summary>已停止。设备在线，但没有在生产——多为人工停机或等料。</summary>
    Stop,

    /// <summary>报警。需要处理并确认后才能复位。</summary>
    Alarm,

    /// <summary>离线。通讯不通，此时其余数值一律是陈旧的。</summary>
    Offline,
}

/// <summary>报警严重度。</summary>
public enum MesSeverity
{
    /// <summary>预警。不停线，但要有人去看。</summary>
    Warning,

    /// <summary>严重。已经影响生产，必须处理并确认。</summary>
    Critical,
}

/// <summary>状态与严重度的显示辅助。</summary>
/// <remarks>
/// 集中在一处，避免页面里到处写 switch。多处各写一份时，
/// 加一档状态只会改到其中几处，剩下的静默落进 default 分支。
/// </remarks>
public static class MesDisplay
{
    /// <summary>状态的英文短标，工站卡上用。</summary>
    /// <remarks>
    /// 用英文缩写而不是中文：这一格宽度固定，四个中文字会换行；
    /// 而 RUN / STOP / ALARM 是设备面板上本来就印着的词，操作员认得。
    /// </remarks>
    public static string Code(MesState s) => s switch
    {
        MesState.Run => "RUN",
        MesState.Stop => "STOP",
        MesState.Alarm => "ALARM",
        _ => "OFFLINE",
    };

    /// <summary>状态对应的 CSS 修饰类，与 theme.css 中 .st / .state 下的类名一致。</summary>
    public static string Css(MesState s) => s switch
    {
        MesState.Run => "run",
        MesState.Stop => "stop",
        MesState.Alarm => "alarm",
        _ => "off",
    };

    /// <summary>严重度的中文名。</summary>
    public static string SeverityText(MesSeverity s) =>
        s == MesSeverity.Critical ? "严重" : "预警";
}

/// <summary>一条产线的班次指标。</summary>
/// <param name="Output">班次产量。</param>
/// <param name="Target">班次目标。</param>
/// <param name="YieldText">良率，已带百分号。</param>
/// <param name="CycleText">节拍，已带单位。</param>
/// <param name="DowntimeText">累计停机，形如 <c>00:08:25</c>。</param>
/// <remarks>
/// 良率与节拍存成<b>已格式化的字符串</b>而不是 double：这些量的小数位数、
/// 单位、千分位由现场习惯决定（有的线报秒、有的报 UPH），
/// 让数据源自己定好格式，页面不再二次加工，省得两边各改一次。
/// </remarks>
public sealed record MesKpi(
    int Output,
    int Target,
    string YieldText,
    string CycleText,
    string DowntimeText);

/// <summary>工艺流上的一个工站。</summary>
/// <param name="Id">工站标识，用于 @key 与报警关联。</param>
/// <param name="Name">工站名，例如「回流焊」。</param>
/// <param name="Model">设备型号或路由标识，显示在名字下方一行。</param>
/// <param name="State">当前状态。</param>
/// <param name="SubText">主显值，例如产量、温度、良率。空串时该行留空占位。</param>
/// <param name="AlarmCount">该站未确认的报警条数，0 表示不显示角标。</param>
public sealed record MesStation(
    string Id,
    string Name,
    string Model,
    MesState State,
    string SubText,
    int AlarmCount = 0);

/// <summary>离散设备卡上的一个读数。</summary>
/// <param name="Label">名称，例如「输出频率」。</param>
/// <param name="Text">数值本身，已格式化。</param>
/// <param name="Unit">单位，可为空。</param>
public sealed record MesValue(string Label, string Text, string Unit = "");

/// <summary>
/// 归属某条线、但不在工艺流上的设备。
/// </summary>
/// <param name="Id">设备标识。</param>
/// <param name="Name">显示名。</param>
/// <param name="Meta">协议与连接信息，例如 <c>modbus-rtu · COM4 · 站 3</c>。</param>
/// <param name="PollText">轮询周期文案，例如 <c>500 ms</c>。</param>
/// <param name="Stale">读值是否已陈旧（最近一次读取失败或超时未更新）。</param>
/// <param name="Values">卡片上要显示的读数。</param>
/// <remarks>
/// 这类设备<b>只监视、不控制</b>：变频器、伺服、温湿度计这些挂在线上但不参与
/// 节拍的东西，给它们配启停按钮既没有意义，也是误触的来源。
/// 因此这个类型里没有 State——只有「读得到」和「读不到」，由 <paramref name="Stale"/> 表达。
/// </remarks>
public sealed record MesSoloDevice(
    string Id,
    string Name,
    string Meta,
    string PollText,
    bool Stale,
    IReadOnlyList<MesValue> Values);

/// <summary>报警详情里的一条时间线事件。</summary>
/// <param name="Time">时刻，形如 <c>02:42:10</c>。</param>
/// <param name="Text">发生了什么。</param>
public sealed record MesTimelineItem(string Time, string Text);

/// <summary>一条活动报警。</summary>
/// <param name="Id">报警标识。</param>
/// <param name="Title">标题，操作员一眼要看懂的那句话。</param>
/// <param name="Severity">严重度。</param>
/// <param name="LineName">所属产线名。</param>
/// <param name="StationName">触发设备名。</param>
/// <param name="RaisedAt">发生时刻，形如 <c>02:42:10</c>。</param>
/// <param name="DurationText">已持续时长，形如 <c>00:08:25</c>。</param>
/// <param name="Code">报警代码，排查时用来查手册。</param>
/// <param name="Description">报警描述。</param>
/// <param name="Facts">详情弹窗上方的事实格，键值对。</param>
/// <param name="Related">关联的实时数据行：名称 / 地址 / 当前值 / 单位。</param>
/// <param name="Advice">处理建议，按顺序执行。</param>
/// <param name="Timeline">事件时间线，第一条是触发点。</param>
/// <param name="Acked">是否已确认。已确认的仍留在列表里，直到复位。</param>
/// <remarks>
/// <c>Acked</c> 是可变的（<c>set</c>），这是本文件里唯一的例外：
/// 确认是操作员在页面上做的动作，要立刻反映在同一份对象上，
/// 否则抽屉里点了确认、详情弹窗里还显示「未确认」。
/// </remarks>
public sealed record MesAlarm(
    string Id,
    string Title,
    MesSeverity Severity,
    string LineName,
    string StationName,
    string RaisedAt,
    string DurationText,
    string Code,
    string Description,
    IReadOnlyList<MesValue> Facts,
    IReadOnlyList<MesRelatedValue> Related,
    IReadOnlyList<string> Advice,
    IReadOnlyList<MesTimelineItem> Timeline)
{
    /// <summary>是否已确认。</summary>
    public bool Acked { get; set; }
}

/// <summary>报警详情里的一行关联实时数据。</summary>
/// <param name="Name">变量名。</param>
/// <param name="Address">地址。</param>
/// <param name="Value">当前值。</param>
/// <param name="Unit">单位。</param>
/// <param name="Abnormal">是否为异常值，决定这一格是否标红。</param>
public sealed record MesRelatedValue(
    string Name,
    string Address,
    string Value,
    string Unit,
    bool Abnormal = false);

/// <summary>一条产线。</summary>
/// <param name="Id">产线标识。</param>
/// <param name="Name">产线名。</param>
/// <param name="Controller">线控制器名。</param>
/// <param name="ControllerEndpoint">线控制器连接地址。</param>
/// <param name="State">整线状态。</param>
/// <param name="Kpi">班次指标。</param>
/// <param name="Stations">工艺流工站，<b>按工艺顺序</b>排列。</param>
/// <param name="Solo">线内离散设备。</param>
/// <remarks>
/// <paramref name="Stations"/> 的顺序就是画面上从左到右的顺序，
/// 这个顺序是工艺信息本身——上板机在回流焊后面是错的。
/// 因此排序责任在数据源，页面不得重排。
/// </remarks>
public sealed record MesLine(
    string Id,
    string Name,
    string Controller,
    string ControllerEndpoint,
    MesState State,
    MesKpi Kpi,
    IReadOnlyList<MesStation> Stations,
    IReadOnlyList<MesSoloDevice> Solo);

/// <summary>监控页一次渲染要用到的全部数据。</summary>
/// <param name="Lines">全部产线。</param>
/// <param name="Alarms">全部活动报警，跨产线。</param>
/// <param name="PollText">采集周期文案。</param>
/// <param name="LastUpdate">上次更新时刻，形如 <c>08:30:59</c>。</param>
/// <param name="IsDemo">
/// 这份数据是不是演示数据。为真时页面必须显著提示，
/// 绝不能让一屏编出来的数字被当成现场读数。
/// </param>
public sealed record MesSnapshot(
    IReadOnlyList<MesLine> Lines,
    IReadOnlyList<MesAlarm> Alarms,
    string PollText,
    string LastUpdate,
    bool IsDemo)
{
    /// <summary>产线条数。</summary>
    public int LineCount => Lines.Count;

    /// <summary>离散设备台数，跨产线合计。</summary>
    public int SoloCount => Lines.Sum(l => l.Solo.Count);

    /// <summary>设备合计：工艺流工站 + 离散设备。</summary>
    public int DeviceCount => Lines.Sum(l => l.Stations.Count + l.Solo.Count);

    /// <summary>运行中的工站数。离散设备不计——它们没有运行状态，只有读得到读不到。</summary>
    public int RunningCount => Lines.Sum(l => l.Stations.Count(s => s.State == MesState.Run));

    /// <summary>离线数：离线工站 + 读值陈旧的离散设备。</summary>
    /// <remarks>
    /// 两者合并统计是有意的：对操作员来说它们是同一件事——
    /// 「这台的数据现在不可信」。分开报两个数字反而要他自己去加。
    /// </remarks>
    public int OfflineCount =>
        Lines.Sum(l => l.Stations.Count(s => s.State == MesState.Offline) + l.Solo.Count(d => d.Stale));

    /// <summary>未确认的报警条数。已确认但未复位的不计入——那些不再需要人去看。</summary>
    public int ActiveAlarmCount => Alarms.Count(a => !a.Acked);
}
