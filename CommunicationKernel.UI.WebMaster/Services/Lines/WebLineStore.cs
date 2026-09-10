// -----------------------------------------------------------------------------
// 文件: Services/Lines/WebLineStore.cs
// 层级: UI 层 — Blazor Server
// 作用: 持久化产线编排：哪些设备组成一条线、按什么顺序、各自的点位选择与报警规则。
//
// 这是 MES 监控页缺的那一半：
//   设备表知道「有哪些设备、怎么连」，变量表知道「每台有哪些点位」，
//   但没有任何地方记录「谁在哪条线上、排第几站、哪个点位代表运行状态」。
//   本存储就是那份编排。它<b>只引用</b>设备与变量的标识，不复制它们的内容——
//   复制一份名称或地址进来，改设备时这边就会悄悄过期。
//
// 与通讯层的关系：无。本层只写 json，不发任何 I/O。
// 按这份配置去读写 PLC 是后续 MesDataSource 的事。
// -----------------------------------------------------------------------------

using System.Text.Json.Serialization;

using CommunicationKernel.Hosting.Sdk;

namespace CommunicationKernel.UI.WebMaster.Services;

/// <summary>一个点位选择：指向某台设备上的某条变量。</summary>
/// <remarks>
/// 存变量 Id 而不是地址：地址会被「批量改地址」整表改写，
/// 存地址的话改完之后这里指向的还是旧地址，而且不会报错。
/// </remarks>
public sealed class WebPointRef
{
    /// <summary>变量 Id。空串表示未选择。</summary>
    public string VariableId { get; set; } = string.Empty;

    /// <summary>
    /// 写入值。
    /// </summary>
    /// <remarks>
    /// 只对启动 / 停止 / 复位这类写点位有意义，状态点位是只读的。
    /// 存字符串而不是数值：点位可能是 Bool、Int16、也可能是 Hex 字，
    /// 用哪种数值类型都会在另外两种上失真。
    /// </remarks>
    public string WriteValue { get; set; } = "1";

    /// <summary>
    /// 脉冲时长（毫秒）。0 表示不脉冲，直接写入并保持。
    /// </summary>
    /// <remarks>
    /// 脉冲＝写入后过这么久自动写 0。PLC 侧多用上升沿触发，
    /// 一直保持 1 会让下一次触发丢失；而人手动写 0 又常常忘。
    /// </remarks>
    public int PulseMs { get; set; } = 300;

    /// <summary>是否已选择变量。</summary>
    /// <remarks>
    /// 派生量，不落盘。写进 json 会让人以为它是可编辑的开关——
    /// 手改成 true 却什么也不会发生，因为真正的依据是 VariableId 有没有值。
    /// </remarks>
    [JsonIgnore]
    public bool Bound => !string.IsNullOrWhiteSpace(VariableId);
}

/// <summary>把状态点位的读值翻译成 RUN / STOP / ALARM。</summary>
/// <remarks>
/// 各家 PLC 的状态字取值毫无共性，必须逐线（或逐站）配。
/// 存字符串是为了兼容 Bool（"true"）与数值（"2"）两种写法。
/// </remarks>
public sealed class WebStateMap
{
    /// <summary>判为运行的值。</summary>
    public string Run { get; set; } = "1";

    /// <summary>判为停止的值。</summary>
    public string Stop { get; set; } = "0";

    /// <summary>判为报警的值。</summary>
    public string Alarm { get; set; } = "2";

    /// <summary>深拷贝。工站「单独覆盖」时从整线复制一份，之后各改各的。</summary>
    public WebStateMap Clone() => new() { Run = Run, Stop = Stop, Alarm = Alarm };
}

/// <summary>报警条件的比较方式。</summary>
/// <remarks>
/// 分三族，对应三类点位：
/// <list type="bullet">
///   <item><b>通断</b>——线圈 / Bool。它本身就是一个位，没有「第几位」可言。</item>
///   <item><b>位</b>——报警字、状态字这类把多个标志打包进一个寄存器的整数。</item>
///   <item><b>值</b>——气压、温度、计数这类整个读数有意义的量。</item>
/// </list>
/// 三族不通用：给线圈选「第 N 位 = 1」没有意义，给浮点选位比较更是错的。
/// 因此下拉里能选什么，由所选变量的数据类型决定（见 <see cref="WebAlarmOpInfo.For"/>）。
/// </remarks>
public enum WebAlarmOp
{
    /// <summary>该位为 1。报警字的常规用法。</summary>
    BitSet = 0,

    /// <summary>该位为 0。用于「就绪信号消失」这类反逻辑。</summary>
    BitClear,

    /// <summary>整个值等于。</summary>
    Equal,

    /// <summary>整个值不等于。</summary>
    NotEqual,

    /// <summary>整个值小于。</summary>
    Less,

    /// <summary>整个值大于。</summary>
    Greater,

    /// <summary>线圈接通（ON）。</summary>
    /// <remarks>
    /// 只用于 Bool。它不带操作数——线圈就是一位，没有「和谁比」。
    /// </remarks>
    IsOn,

    /// <summary>线圈断开（OFF）。</summary>
    IsOff,
}

/// <summary>报警条件的显示辅助。</summary>
public static class WebAlarmOpInfo
{
    /// <summary>全部取值，仅供遍历与迁移使用。</summary>
    /// <remarks>
    /// 界面上<b>不要</b>直接用这个：能选什么取决于变量类型，
    /// 全列出来会让人给线圈选「值 &lt;」这种永远不成立的条件。用 <see cref="For"/>。
    /// </remarks>
    public static readonly WebAlarmOp[] All =
    {
        WebAlarmOp.IsOn,
        WebAlarmOp.IsOff,
        WebAlarmOp.BitSet,
        WebAlarmOp.BitClear,
        WebAlarmOp.Equal,
        WebAlarmOp.NotEqual,
        WebAlarmOp.Less,
        WebAlarmOp.Greater,
    };

    /// <summary>线圈可用的比较方式。</summary>
    private static readonly WebAlarmOp[] BoolOps = { WebAlarmOp.IsOn, WebAlarmOp.IsOff };

    /// <summary>整数可用的比较方式：位与值都行。</summary>
    private static readonly WebAlarmOp[] IntegerOps =
    {
        WebAlarmOp.BitSet, WebAlarmOp.BitClear,
        WebAlarmOp.Equal, WebAlarmOp.NotEqual, WebAlarmOp.Less, WebAlarmOp.Greater,
    };

    /// <summary>浮点可用的比较方式：只有值。</summary>
    /// <remarks>
    /// 浮点没有「第几位」——它的二进制里那些位是尾数和指数，
    /// 按位取出来是一串与工程量毫无关系的数。
    /// </remarks>
    private static readonly WebAlarmOp[] FloatOps =
    {
        WebAlarmOp.Equal, WebAlarmOp.NotEqual, WebAlarmOp.Less, WebAlarmOp.Greater,
    };

    /// <summary>文本 / 原始字节可用的比较方式：只有相等与否。</summary>
    /// <remarks>
    /// 大小比较对字符串没有现场意义，留着只会让人误以为能按字典序报警。
    /// </remarks>
    private static readonly WebAlarmOp[] TextOps = { WebAlarmOp.Equal, WebAlarmOp.NotEqual };

    /// <summary>
    /// 某个数据类型能用哪些比较方式。
    /// </summary>
    /// <param name="dataType">变量的数据类型名；空串表示还没选变量。</param>
    /// <returns>可选的比较方式，顺序即常用程度。</returns>
    /// <remarks>
    /// 还没选变量时给整数那一套——它是最常见的，而且一旦选了变量，
    /// 页面会把不适用的方式纠正过来（见 LinesPage.FixOp）。
    /// </remarks>
    public static WebAlarmOp[] For(string dataType) => dataType switch
    {
        "Bool" => BoolOps,
        "Float" or "Double" => FloatOps,
        "String" or "Hex" => TextOps,
        _ => IntegerOps,
    };

    /// <summary>该比较方式对这个数据类型是否适用。</summary>
    public static bool Applies(WebAlarmOp op, string dataType) =>
        Array.IndexOf(For(dataType), op) >= 0;

    /// <summary>下拉框里的中文名。</summary>
    /// <remarks>
    /// 位比较写「第 N 位」，值比较写符号，线圈写通断：三者的操作数分别是
    /// 位号、数值、没有。名字里带上这个差别，右边那个框该填什么就不用再猜。
    /// </remarks>
    public static string Label(WebAlarmOp op) => op switch
    {
        WebAlarmOp.IsOn => "接通 ON",
        WebAlarmOp.IsOff => "断开 OFF",
        WebAlarmOp.BitSet => "第 N 位 = 1",
        WebAlarmOp.BitClear => "第 N 位 = 0",
        WebAlarmOp.Equal => "值 ==",
        WebAlarmOp.NotEqual => "值 !=",
        WebAlarmOp.Less => "值 <",
        WebAlarmOp.Greater => "值 >",
        _ => "第 N 位 = 1",
    };

    /// <summary>右侧输入框的占位提示。</summary>
    public static string Placeholder(WebAlarmOp op) => IsBit(op) ? "位号 0-15" : "对比值";

    /// <summary>是不是位比较。位比较的操作数是位号，不是数值。</summary>
    public static bool IsBit(WebAlarmOp op) => op is WebAlarmOp.BitSet or WebAlarmOp.BitClear;

    /// <summary>
    /// 这个比较方式要不要操作数。
    /// </summary>
    /// <remarks>
    /// 线圈的通断不需要：条件已经由方式本身表达完了。
    /// 还留一个输入框在那儿，会让人以为漏填了什么。
    /// </remarks>
    public static bool NeedsOperand(WebAlarmOp op) => op is not (WebAlarmOp.IsOn or WebAlarmOp.IsOff);

    /// <summary>拼成一句可读的条件，用于日志与只读展示。</summary>
    public static string Text(WebAlarmOp op, string operand)
    {
        string v = string.IsNullOrWhiteSpace(operand) ? "?" : operand.Trim();

        return op switch
        {
            WebAlarmOp.IsOn => "= ON",
            WebAlarmOp.IsOff => "= OFF",
            WebAlarmOp.BitSet => "bit " + v + " = 1",
            WebAlarmOp.BitClear => "bit " + v + " = 0",
            WebAlarmOp.Equal => "== " + v,
            WebAlarmOp.NotEqual => "!= " + v,
            WebAlarmOp.Less => "< " + v,
            WebAlarmOp.Greater => "> " + v,
            _ => v,
        };
    }
}

/// <summary>一条报警规则：某个变量满足条件时触发。</summary>
public sealed class WebAlarmRule
{
    /// <summary>规则 Id，供界面增删定位。</summary>
    public string Id { get; set; } = Guid.NewGuid().ToString("N");

    /// <summary>被判断的变量 Id。</summary>
    public string VariableId { get; set; } = string.Empty;

    /// <summary>
    /// 比较方式。
    /// </summary>
    /// <remarks>
    /// 与 <see cref="Operand"/> 拆成两个字段，而不是存一句 <c>bit 0</c> 这样的文本。
    /// 早先存的是文本，理由是「现场要能一眼看懂自己填了什么」——
    /// 但那意味着操作员得记住这套语法，写错 <c>bit0</c>（少个空格）或
    /// <c>=1</c>（少个等号）不会有任何提示，要到设备真报警时才发现规则从没生效过。
    /// 改成下拉 + 输入框之后，语法由界面保证，只剩一个操作数要填。
    /// </remarks>
    public WebAlarmOp Op { get; set; } = WebAlarmOp.BitSet;

    /// <summary>操作数：位比较时是位号，值比较时是对比值。</summary>
    public string Operand { get; set; } = "0";

    /// <summary>
    /// 兼容上一版的整句条件文本，只用于读入旧配置。
    /// </summary>
    /// <remarks>
    /// 只有 setter：System.Text.Json 反序列化时会用它，序列化时因无 getter 而跳过，
    /// 于是旧文件读得进来、新文件不再写出这个字段，一次读写即完成迁移。
    /// 解析不出来时保持默认值——总比静默变成一条永远不触发的规则强。
    /// </remarks>
    public string Condition
    {
        set
        {
            if (string.IsNullOrWhiteSpace(value)) return;

            string s = value.Trim();

            if (s.StartsWith("bit", StringComparison.OrdinalIgnoreCase))
            {
                // 形如 "bit 0"、"bit 3 = 0"。必须按等号切开再看右边，
                // 不能只判「整串里有没有 0」——"bit 10" 的那个 0 来自位号，
                // 会被误读成「该位为 0」
                string rest = s[3..].Trim();
                int eq = rest.IndexOf('=');

                string bits = eq >= 0 ? rest[..eq] : rest;
                string wanted = eq >= 0 ? rest[(eq + 1)..].Trim() : "1";

                Op = wanted == "0" ? WebAlarmOp.BitClear : WebAlarmOp.BitSet;
                Operand = new string(bits.Where(char.IsDigit).ToArray());
                if (Operand.Length == 0) Operand = "0";
                return;
            }

            (string prefix, WebAlarmOp op)[] map =
            {
                ("==", WebAlarmOp.Equal),
                ("!=", WebAlarmOp.NotEqual),
                ("<", WebAlarmOp.Less),
                (">", WebAlarmOp.Greater),
            };

            foreach ((string prefix, WebAlarmOp op) in map)
            {
                if (!s.StartsWith(prefix, StringComparison.Ordinal)) continue;

                Op = op;
                Operand = s[prefix.Length..].Trim();
                return;
            }
        }
    }

    /// <summary>拼好的条件文本，供只读展示与日志。</summary>
    [JsonIgnore]
    public string ConditionText => WebAlarmOpInfo.Text(Op, Operand);

    /// <summary>报警名称，操作员一眼要看懂的那句话。</summary>
    public string Title { get; set; } = string.Empty;

    /// <summary>报警代码，用于查手册与统计。</summary>
    public string Code { get; set; } = string.Empty;

    /// <summary>是否为严重级别。false 为预警。</summary>
    public bool Critical { get; set; } = true;

    /// <summary>处理建议，出现在报警详情弹窗里。</summary>
    public string Advice { get; set; } = string.Empty;
}

/// <summary>工艺流上的一个工站。</summary>
public sealed class WebStation
{
    /// <summary>
    /// 该工站对应的设备路由 Id。
    /// </summary>
    /// <remarks>
    /// 名称、协议、连接一律不存——它们属于设备管理。
    /// 存一份副本的话，设备改名后产线里还是旧名字，且没有任何提示。
    /// </remarks>
    public string RouteId { get; set; } = string.Empty;

    /// <summary>状态点位（只读）。</summary>
    public WebPointRef State { get; set; } = new();

    /// <summary>启动点位。</summary>
    public WebPointRef Start { get; set; } = new();

    /// <summary>停止点位。</summary>
    public WebPointRef Stop { get; set; } = new();

    /// <summary>复位点位。</summary>
    public WebPointRef Reset { get; set; } = new();

    /// <summary>
    /// 本站独有的状态映射；null 表示继承整线。
    /// </summary>
    /// <remarks>
    /// 默认继承：一条线上的设备多数来自同一家、状态字约定相同，
    /// 逐站填一遍既啰嗦又容易填错一个。确实不同的那台再单独覆盖。
    /// </remarks>
    public WebStateMap? StateMap { get; set; }

    /// <summary>本站的报警规则。</summary>
    public List<WebAlarmRule> Alarms { get; set; } = new();

    /// <summary>报警详情弹窗里「关联实时数据」要列出的变量 Id。</summary>
    public List<string> RelatedVariableIds { get; set; } = new();

    /// <summary>MES 卡片状态下方那行数字取自哪条变量。空串表示不显示。</summary>
    public string SubValueVariableId { get; set; } = string.Empty;

    /// <summary>四个点位里已选择的个数，供界面显示 <c>2/4</c> 这样的进度。</summary>
    /// <remarks>派生量，不落盘。</remarks>
    [JsonIgnore]
    public int BoundCount =>
        (State.Bound ? 1 : 0) + (Start.Bound ? 1 : 0) + (Stop.Bound ? 1 : 0) + (Reset.Bound ? 1 : 0);
}

/// <summary>线内离散设备：只监视，不参与工艺流与控制。</summary>
public sealed class WebSoloDevice
{
    /// <summary>设备路由 Id。</summary>
    public string RouteId { get; set; } = string.Empty;

    /// <summary>
    /// 卡片上显示的变量 Id，最多四个。
    /// </summary>
    /// <remarks>
    /// 上限四个是卡片版式定的：两行两列。再多就得缩字号或加高卡片，
    /// 而这类卡片是用来扫一眼的，不是用来读表的——要看全部变量去变量配置页。
    /// </remarks>
    public List<string> FieldVariableIds { get; set; } = new();
}

/// <summary>产线 KPI 的一项：取自变量，或直接是个常量。</summary>
public sealed class WebKpiItem
{
    /// <summary>变量 Id。空串表示用 <see cref="Constant"/>。</summary>
    public string VariableId { get; set; } = string.Empty;

    /// <summary>常量值，用于「目标产量」这类不来自 PLC 的指标。</summary>
    public string Constant { get; set; } = string.Empty;

    /// <summary>单位，直接显示在数值后面。</summary>
    public string Unit { get; set; } = string.Empty;
}

/// <summary>一条产线的完整编排。</summary>
public sealed class WebLine
{
    /// <summary>产线 Id。</summary>
    public string Id { get; set; } = Guid.NewGuid().ToString("N")[..8];

    /// <summary>产线名，显示在 MES 页与本页左栏。</summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>整线控制器的设备路由 Id。空串表示还没选。</summary>
    public string ControllerRouteId { get; set; } = string.Empty;

    /// <summary>整线状态点位（只读）。</summary>
    public WebPointRef State { get; set; } = new();

    /// <summary>整线启动点位。</summary>
    public WebPointRef Start { get; set; } = new();

    /// <summary>整线停止点位。</summary>
    public WebPointRef Stop { get; set; } = new();

    /// <summary>整线复位点位。</summary>
    public WebPointRef Reset { get; set; } = new();

    /// <summary>整线状态映射，工站默认继承这一份。</summary>
    public WebStateMap StateMap { get; set; } = new();

    /// <summary>产线级报警规则（安全门、急停、气压等）。</summary>
    public List<WebAlarmRule> Alarms { get; set; } = new();

    /// <summary>工艺流工站，<b>列表顺序即工艺顺序</b>。</summary>
    /// <remarks>
    /// 不另设 Order 字段：两份顺序信息迟早会打架，而列表顺序本身
    /// 就是 json 里可读、可手改、可拖动重排的唯一真相。
    /// </remarks>
    public List<WebStation> Stations { get; set; } = new();

    /// <summary>线内离散设备。</summary>
    public List<WebSoloDevice> Solo { get; set; } = new();

    /// <summary>班次产量。</summary>
    public WebKpiItem Output { get; set; } = new() { Unit = "pcs" };

    /// <summary>目标产量，通常是常量。</summary>
    public WebKpiItem Target { get; set; } = new() { Constant = "1500" };

    /// <summary>良率。</summary>
    public WebKpiItem Yield { get; set; } = new() { Unit = "%" };

    /// <summary>节拍。</summary>
    public WebKpiItem Cycle { get; set; } = new() { Unit = "s" };

    /// <summary>还有几处点位没选（整线四个 + 各站四个）。</summary>
    /// <remarks>
    /// 供左栏显示「1 未选择 / 完整」。没选完不算错——设备可能确实没有复位点位；
    /// 但要让人看得见，免得到了 MES 页才发现按钮是灰的却不知道为什么。
    /// </remarks>
    [JsonIgnore]
    public int UnboundCount
    {
        get
        {
            int n = 4 - ((State.Bound ? 1 : 0) + (Start.Bound ? 1 : 0) + (Stop.Bound ? 1 : 0) + (Reset.Bound ? 1 : 0));
            foreach (WebStation s in Stations)
                n += 4 - s.BoundCount;
            return n;
        }
    }

    /// <summary>报警规则总数（产线级 + 各工站）。</summary>
    /// <remarks>派生量，不落盘。</remarks>
    [JsonIgnore]
    public int AlarmCount => Alarms.Count + Stations.Sum(s => s.Alarms.Count);
}

/// <summary>产线编排的持久化存储。单例。</summary>
/// <remarks>
/// 与 <see cref="WebDeviceStore"/>、<see cref="WebVariableStore"/> 同一套写法：
/// 内存态加锁，落盘走 <c>JsonFileStore</c> 的原子替换，变更后广播 <see cref="Changed"/>。
/// </remarks>
public sealed class WebLineStore
{
    /// <summary>保护 <see cref="_lines"/> 的锁。</summary>
    private readonly object _lock = new();

    /// <summary>全部产线，顺序即左栏显示顺序。</summary>
    private readonly List<WebLine> _lines = new();

    /// <summary>框架日志器，用于报落盘失败。</summary>
    private readonly ILogger<WebLineStore> _logger;

    /// <summary>配置变化时触发，供页面重绘。</summary>
    public event Action? Changed;

    /// <param name="logger">框架日志器。</param>
    public WebLineStore(ILogger<WebLineStore> logger)
    {
        _logger = logger;
        Load();
    }

    /// <summary>配置文件的完整路径，显示在界面上供排查。</summary>
    public static string FilePath => WebPaths.LinesFile;

    /// <summary>取全部产线的快照。</summary>
    /// <remarks>
    /// 返回的是<b>内部对象本身</b>而非深拷贝：编辑页要直接改这些对象再调 <see cref="Save"/>，
    /// 拷来拷去会让「改了但没生效」这种问题层出不穷。
    /// 代价是调用方必须自觉——本存储只服务编辑页一个使用者。
    /// </remarks>
    public IReadOnlyList<WebLine> GetAll()
    {
        lock (_lock)
            return _lines.ToList();
    }

    /// <summary>按 Id 取一条产线，找不到返回 null。</summary>
    public WebLine? Get(string id)
    {
        if (string.IsNullOrWhiteSpace(id)) return null;

        lock (_lock)
            return _lines.FirstOrDefault(l => string.Equals(l.Id, id, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>新建一条产线并返回它。</summary>
    /// <param name="name">产线名。留空时自动编号。</param>
    public WebLine Add(string name)
    {
        WebLine line;
        lock (_lock)
        {
            line = new WebLine
            {
                Name = string.IsNullOrWhiteSpace(name) ? "新产线 " + (_lines.Count + 1) : name.Trim(),
            };
            _lines.Add(line);
            Persist_NoLock();
        }

        Changed?.Invoke();
        return line;
    }

    /// <summary>
    /// 复制一条产线。
    /// </summary>
    /// <param name="id">被复制的产线 Id。</param>
    /// <returns>新产线；源不存在时返回 null。</returns>
    /// <remarks>
    /// 同型号的第二条线绝大部分配置是一样的，复制再改比从头选一遍快得多。
    /// 复制的是<b>编排</b>——工站仍指向同一批设备，需要手动改成第二条线的设备。
    /// 不自动改是有意的：猜错设备比留空更难发现。
    /// </remarks>
    public WebLine? Duplicate(string id)
    {
        WebLine copy;
        lock (_lock)
        {
            WebLine? src = _lines.FirstOrDefault(l => l.Id == id);
            if (src is null) return null;

            // 经 json 往返做深拷贝：手写拷贝在加字段时必然漏，而漏掉的那个
            // 字段会在两条线之间意外共享同一个对象引用
            copy = JsonClone(src);
            copy.Id = Guid.NewGuid().ToString("N")[..8];
            copy.Name = src.Name + " 副本";
            ReassignIds(copy);

            _lines.Add(copy);
            Persist_NoLock();
        }

        Changed?.Invoke();
        return copy;
    }

    /// <summary>删除一条产线。</summary>
    public void Remove(string id)
    {
        lock (_lock)
        {
            if (_lines.RemoveAll(l => l.Id == id) == 0) return;
            Persist_NoLock();
        }

        Changed?.Invoke();
    }

    /// <summary>把内存中的改动落盘并通知订阅方。</summary>
    /// <remarks>
    /// 编辑页直接改 <see cref="GetAll"/> 拿到的对象，改完调这里。
    /// 每改一个字段就落一次盘代价太高（整份 json 重写），
    /// 因此由页面在「保存」时统一调用。
    /// </remarks>
    public void Save()
    {
        lock (_lock)
            Persist_NoLock();

        Changed?.Invoke();
    }

    /// <summary>丢弃内存改动，从磁盘重新读一遍。</summary>
    /// <remarks>
    /// 编辑页直接改的是 <see cref="GetAll"/> 返回的对象本身，
    /// 所以「还原」必须真的重读文件——只把界面刷新一遍没有用，
    /// 内存里已经是改过的样子了。
    /// </remarks>
    public void Reload()
    {
        Load();
        Changed?.Invoke();
    }

    /// <summary>从磁盘装载。</summary>
    private void Load()
    {
        List<WebLine> list = JsonFileStore.Load<WebLine>(WebPaths.LinesFile, out string error);

        // 成功时 JsonFileStore 把 error 置为 null 而不是空串——直接取 .Length
        // 会在首次启动（还没有这个配置文件）时抛空引用
        if (!string.IsNullOrEmpty(error))
            _logger.LogWarning("产线配置读取失败: {Error}", error);

        lock (_lock)
        {
            _lines.Clear();
            _lines.AddRange(list);
        }
    }

    /// <summary>写盘。调用方必须已持有锁。</summary>
    private void Persist_NoLock()
    {
        if (!JsonFileStore.Save(WebPaths.LinesFile, _lines, out string error))
            _logger.LogError("产线配置写入失败: {Error}", error);
    }

    /// <summary>经 json 往返做深拷贝。</summary>
    private static WebLine JsonClone(WebLine src)
    {
        string json = System.Text.Json.JsonSerializer.Serialize(src);
        return System.Text.Json.JsonSerializer.Deserialize<WebLine>(json) ?? new WebLine();
    }

    /// <summary>给复制出来的产线里所有报警规则换新 Id。</summary>
    /// <remarks>
    /// 不换的话两条线上的规则共用同一个 Id，界面按 Id 删除时会命中错的那条，
    /// 而且完全不报错。
    /// </remarks>
    private static void ReassignIds(WebLine line)
    {
        foreach (WebAlarmRule r in line.Alarms)
            r.Id = Guid.NewGuid().ToString("N");

        foreach (WebStation s in line.Stations)
            foreach (WebAlarmRule r in s.Alarms)
                r.Id = Guid.NewGuid().ToString("N");
    }
}
