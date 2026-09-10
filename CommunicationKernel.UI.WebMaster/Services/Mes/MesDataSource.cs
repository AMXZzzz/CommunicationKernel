// -----------------------------------------------------------------------------
// 文件: Services/Mes/MesDataSource.cs
// 层级: UI 层 — MES 监控的真实数据来源
// 作用: 把「产线配置」这份编排，对着变量表里的实时读值，算成监控页要画的东西。
//
// 三份数据在这里汇合：
//   web-lines.json     谁在哪条线上、排第几站、哪个点位代表运行状态
//   web-variables.json 每个点位当前读到什么（由 VariablePoller 后台刷新）
//   宿主路由表          这条路由现在通不通
//
// 本类<b>不发任何 I/O</b>。读值来自变量表——那是 VariablePoller 填的，
// 一份数据一条采集路径。自己再去读一遍，同一个点位在监控页和变量页
// 就会显示不同的值，而两边都报成功。
//
// 与通讯层的关系：无。协议、字节序、小数位换算全在拿到这里之前就做完了。
// -----------------------------------------------------------------------------

using CommunicationKernel.Hosting.Sdk;

namespace CommunicationKernel.UI.WebMaster.Services;

/// <summary>按产线配置与实时读值构造监控快照。单例。</summary>
public sealed class MesDataSource : IMesDataSource
{
    /// <summary>产线编排。</summary>
    private readonly WebLineStore _lines;

    /// <summary>变量表，读值的唯一来源。</summary>
    private readonly WebVariableStore _vars;

    /// <summary>设备配置，用于取显示名与连接信息。</summary>
    private readonly WebDeviceStore _devices;

    /// <summary>会话，提供路由在线状态。</summary>
    private readonly EngineSession _session;

    /// <summary>保护 <see cref="_active"/>。快照可能被多个浏览器会话并发构造。</summary>
    private readonly object _lock = new();

    /// <summary>
    /// 当前成立的报警，按规则 Id 索引。
    /// </summary>
    /// <remarks>
    /// 报警本身是每次快照现算的，但「什么时候开始的」和「确认过没有」是状态，
    /// 必须跨快照留着——否则持续时长每次刷新都归零，确认过的下一秒又冒出来。
    /// 条件不再成立时整条移除：报警消失后再出现是<b>新的一次</b>，
    /// 不该继承上一次的确认。
    /// </remarks>
    private readonly Dictionary<string, ActiveAlarm> _active = new(StringComparer.Ordinal);

    /// <summary>
    /// 陈旧判据的下限（秒）。
    /// </summary>
    /// <remarks>
    /// 与变量页那一列保持一致。值本身正确但已是十几秒前的，
    /// 和读失败一样危险——而前者不会标红，只能靠这个判出来。
    /// <para>
    /// 只是<b>下限</b>：真正的判据还要看该点位自己的扫描周期，见 <see cref="IsStale"/>。
    /// </para>
    /// </remarks>
    private const int StaleFloorSeconds = 10;

    /// <summary>
    /// 允许连续漏掉几个扫描周期。
    /// </summary>
    /// <remarks>
    /// 一个周期太紧：轮询本来就有抖动，正好赶在下一次读之前取快照，
    /// 每一轮都会闪一下离线。三个周期是「确实没读上来」而不是「刚好错开」。
    /// </remarks>
    private const int StaleScanCycles = 3;

    /// <param name="lines">产线编排。</param>
    /// <param name="vars">变量表。</param>
    /// <param name="devices">设备配置。</param>
    /// <param name="session">会话。</param>
    public MesDataSource(
        WebLineStore lines, WebVariableStore vars, WebDeviceStore devices, EngineSession session)
    {
        _lines = lines;
        _vars = vars;
        _devices = devices;
        _session = session;
    }

    /// <summary>一条正在成立的报警的状态。</summary>
    /// <param name="RaisedAt">首次成立的时刻。</param>
    private sealed record ActiveAlarm(DateTime RaisedAt)
    {
        /// <summary>是否已确认。确认只表示「人看到并接手了」，不清除报警。</summary>
        public bool Acked { get; set; }
    }

    // =========================================================================
    // 快照
    // =========================================================================

    /// <inheritdoc />
    public MesSnapshot Snapshot()
    {
        // 三份数据各取一次快照，之后全程用这一份：
        // 逐站去查会在同一帧里读到不同时刻的状态，画出来自相矛盾
        IReadOnlyList<WebLine> lines = _lines.GetAll();
        Dictionary<string, WebVariable> vars = _vars.GetAll()
            .GroupBy(v => v.Id, StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.First(), StringComparer.Ordinal);

        List<MesAlarm> alarms = new();
        List<MesLine> views = new(lines.Count);

        foreach (WebLine line in lines)
            views.Add(BuildLine(line, vars, alarms));

        return new MesSnapshot(
            views,
            alarms,
            PollText(vars, lines),
            DateTime.Now.ToString("HH:mm:ss"));
    }

    /// <inheritdoc />
    public void Acknowledge(string alarmId)
    {
        if (string.IsNullOrWhiteSpace(alarmId)) return;

        lock (_lock)
            if (_active.TryGetValue(alarmId, out ActiveAlarm? a)) a.Acked = true;
    }

    /// <inheritdoc />
    public void AcknowledgeLine(string lineName)
    {
        if (string.IsNullOrWhiteSpace(lineName)) return;

        // 规则 Id 里不带线名，只能先算一遍快照拿到归属再逐条确认。
        // 这个动作一天点不了几次，多算一遍无所谓
        foreach (MesAlarm a in Snapshot().Alarms.Where(x => x.LineName == lineName))
            Acknowledge(a.Id);
    }

    // =========================================================================
    // 产线
    // =========================================================================

    /// <summary>把一条编排算成一条可画的产线。</summary>
    private MesLine BuildLine(
        WebLine line, Dictionary<string, WebVariable> vars, List<MesAlarm> alarms)
    {
        string lineName = line.Name.Length > 0 ? line.Name : line.Id;
        DeviceView? ctl = DeviceOf(line.ControllerRouteId);

        // 产线级报警挂在控制器上
        CollectAlarms(line.Alarms, vars, lineName, ctl?.Name ?? "整线", ctl, alarms);

        List<MesStation> stations = new(line.Stations.Count);
        for (int i = 0; i < line.Stations.Count; i++)
        {
            WebStation st = line.Stations[i];
            DeviceView? dev = DeviceOf(st.RouteId);
            string stName = dev?.Name ?? st.RouteId;

            int before = alarms.Count;
            CollectAlarms(st.Alarms, vars, lineName, stName, dev, alarms, st.RelatedVariableIds);
            int mine = alarms.Count - before;

            stations.Add(new MesStation(
                Id: line.Id + ":" + i,
                Name: stName,
                Model: dev is null ? "设备已删除" : dev.ProtocolId,
                State: StationState(st, line, vars),
                SubText: SubText(st, vars),
                AlarmCount: mine));
        }

        return new MesLine(
            line.Id,
            lineName,
            ctl?.Name ?? "未选控制器",
            ctl?.Endpoint ?? "—",
            LineState(line, stations, vars),
            BuildKpi(line, vars),
            stations,
            line.Solo.Select(s => BuildSolo(s, vars)).ToList());
    }

    /// <summary>
    /// 工站状态。
    /// </summary>
    /// <remarks>
    /// 判断顺序即优先级，不能调换：
    /// <list type="number">
    ///   <item>路由不通 → OFFLINE。此刻状态点位上的值是上一次读到的，
    ///         拿它判 RUN 就是在用过期数据下结论。</item>
    ///   <item>没绑状态点位 → OFFLINE。装不出运行中，也不该装成停机。</item>
    ///   <item>读值陈旧 → OFFLINE，理由同第一条。</item>
    ///   <item>按状态映射翻译；映射不上的一律 OFFLINE，而不是猜一个。</item>
    /// </list>
    /// </remarks>
    private MesState StationState(WebStation st, WebLine line, Dictionary<string, WebVariable> vars)
    {
        if (!_session.IsRouteOnline(st.RouteId)) return MesState.Offline;
        if (!st.State.Bound) return MesState.Offline;

        WebVariable? v = Lookup(st.State.VariableId, vars);
        if (v is null || v.IsError || IsStale(v)) return MesState.Offline;

        WebStateMap map = st.StateMap ?? line.StateMap;
        return Translate(v.DisplayValue, map);
    }

    /// <summary>
    /// 整线状态。
    /// </summary>
    /// <remarks>
    /// 优先用整线控制器上那个状态点位——那是 PLC 自己的判断，最权威。
    /// 没绑的话由工站推导：有一个报警就是报警，全部离线就是离线，
    /// 有人在跑就算运行，否则停止。推导出来的结论不如点位准，
    /// 但比什么都不显示强。
    /// </remarks>
    private MesState LineState(
        WebLine line, List<MesStation> stations, Dictionary<string, WebVariable> vars)
    {
        if (line.State.Bound && _session.IsRouteOnline(line.ControllerRouteId))
        {
            WebVariable? v = Lookup(line.State.VariableId, vars);
            if (v is not null && !v.IsError && !IsStale(v))
                return Translate(v.DisplayValue, line.StateMap);
        }

        if (stations.Count == 0) return MesState.Offline;
        if (stations.Any(s => s.State == MesState.Alarm)) return MesState.Alarm;
        if (stations.All(s => s.State == MesState.Offline)) return MesState.Offline;
        if (stations.Any(s => s.State == MesState.Run)) return MesState.Run;

        return MesState.Stop;
    }

    /// <summary>按状态映射把读值翻成 RUN / STOP / ALARM。</summary>
    /// <remarks>
    /// 三条都对不上时给 OFFLINE 而不是随便挑一个：
    /// 映射填错时就该在界面上显示成「读不出状态」，而不是装成正在运行。
    /// </remarks>
    private static MesState Translate(string value, WebStateMap map)
    {
        if (Same(value, map.Alarm)) return MesState.Alarm;
        if (Same(value, map.Run)) return MesState.Run;
        if (Same(value, map.Stop)) return MesState.Stop;

        return MesState.Offline;
    }

    /// <summary>状态值比对，复用报警那套数值/文本双路比较。</summary>
    private static bool Same(string value, string expected) =>
        AlarmOpInfo.Evaluate(AlarmOp.Equal, expected, value) == true;

    /// <summary>卡片状态下方那行数字。</summary>
    private string SubText(WebStation st, Dictionary<string, WebVariable> vars)
    {
        if (!st.State.Bound) return "未绑定状态点位";

        WebVariable? v = Lookup(st.SubValueVariableId, vars);
        if (v is null) return string.Empty;

        return WithUnit(v, v.Unit);
    }

    /// <summary>
    /// 把读值和单位拼成一格文本。
    /// </summary>
    /// <remarks>
    /// 读失败时这一格是错误码，不是读数，单位一律不加——
    /// 拼出来的「OFFLINE MPa」既不是错误也不是读数，只会让人多看两眼才反应过来。
    /// </remarks>
    private static string WithUnit(WebVariable v, string unit) =>
        v.IsError || unit.Length == 0 ? v.DisplayValue : v.DisplayValue + " " + unit;

    // =========================================================================
    // KPI 与离散设备
    // =========================================================================

    /// <summary>取四项 KPI。</summary>
    private MesKpi BuildKpi(WebLine line, Dictionary<string, WebVariable> vars) => new(
        Kpi(line.Output, vars),
        Kpi(line.Target, vars),
        Kpi(line.Yield, vars),
        Kpi(line.Cycle, vars),
        // 停机时长要靠状态变迁累计，本版还没有那份历史，先如实留空
        "—");

    /// <summary>一项 KPI 的显示文本。</summary>
    /// <remarks>
    /// 没绑点位也没填常量时给一条短横，不给 0——0 会被读成
    /// 「这条线今天一件没做」，而真相是这一项根本没配。
    /// </remarks>
    private string Kpi(WebKpiItem k, Dictionary<string, WebVariable> vars)
    {
        if (k.VariableId.Length > 0)
        {
            WebVariable? v = Lookup(k.VariableId, vars);
            if (v is null) return "—";

            // KPI 的单位配在 KPI 上而不是变量上：同一个计数点位
            // 在这条线上叫「件」，在别处可能是「箱」
            return WithUnit(v, k.Unit);
        }

        if (k.Constant.Length == 0) return "—";

        return k.Unit.Length > 0 ? k.Constant + " " + k.Unit : k.Constant;
    }

    /// <summary>把一台离散设备算成一张只读卡片。</summary>
    private MesSoloDevice BuildSolo(WebSoloDevice d, Dictionary<string, WebVariable> vars)
    {
        DeviceView? dev = DeviceOf(d.RouteId);

        List<WebVariable> picked = d.FieldVariableIds
            .Select(id => Lookup(id, vars))
            .Where(v => v is not null)
            .Select(v => v!)
            .ToList();

        // 只要有一格读不上来，整张卡就算陈旧：卡片是拿来扫一眼的，
        // 四个数里混着一个过期的而不标出来，比全部过期更容易误判
        bool stale = !_session.IsRouteOnline(d.RouteId)
            || picked.Count == 0
            || picked.Any(v => v.IsError || IsStale(v));

        return new MesSoloDevice(
            d.RouteId,
            dev?.Name ?? d.RouteId,
            dev is null ? "设备已删除" : dev.ProtocolId + " · " + dev.Endpoint,
            PollText(picked),
            stale,
            // 单位与值分两格给页面：卡片上单位是小一号的灰字，拼在一起就没法分开排版。
            // 读失败时不给单位，理由同 WithUnit
            picked.Select(v => new MesValue(v.Name, v.DisplayValue, v.IsError ? string.Empty : v.Unit)).ToList());
    }

    // =========================================================================
    // 报警
    // =========================================================================

    /// <summary>
    /// 逐条判定规则，把成立的那些追加进 <paramref name="into"/>。
    /// </summary>
    /// <remarks>
    /// 判不了的（读不到值、位号填错、类型对不上）<b>不算成立</b>，但也不是「正常」——
    /// 它们会在产线配置页显示成未选择或类型不符。这里的取舍是：
    /// 宁可漏报也不误报。一条凭空冒出来的报警会让人去查一台好设备，
    /// 而真出事时状态点位本身也会跟着变。
    /// </remarks>
    private void CollectAlarms(
        List<WebAlarmRule> rules,
        Dictionary<string, WebVariable> vars,
        string lineName,
        string stationName,
        DeviceView? dev,
        List<MesAlarm> into,
        List<string>? relatedIds = null)
    {
        foreach (WebAlarmRule r in rules)
        {
            WebVariable? v = Lookup(r.VariableId, vars);
            if (v is null) continue;

            bool? hit = AlarmOpInfo.Evaluate(r.Op, r.Operand, v.DisplayValue);
            if (hit != true)
            {
                // 不成立就把状态清掉：下次再成立算新的一次，不继承上回的确认
                lock (_lock) _active.Remove(r.Id);
                continue;
            }

            ActiveAlarm state;
            lock (_lock)
            {
                if (!_active.TryGetValue(r.Id, out ActiveAlarm? existing))
                {
                    existing = new ActiveAlarm(DateTime.Now);
                    _active[r.Id] = existing;
                }
                state = existing;
            }

            TimeSpan dur = DateTime.Now - state.RaisedAt;

            into.Add(new MesAlarm(
                r.Id,
                r.Title.Length > 0 ? r.Title : stationName + " " + AlarmOpInfo.Text(r.Op, r.Operand),
                r.Critical ? MesSeverity.Critical : MesSeverity.Warning,
                lineName,
                stationName,
                state.RaisedAt.ToString("HH:mm:ss"),
                Duration(dur),
                r.Code,
                r.Advice.Length > 0 ? r.Advice : "配置里没有填写描述。到「产线配置」补上，现场排查时会用得上。",
                Facts(r, v, lineName, stationName, dev, dur),
                Related(relatedIds, vars, v),
                Advice(r.Advice),
                new List<MesTimelineItem>
                {
                    new(state.RaisedAt.ToString("HH:mm:ss"),
                        "条件成立 · " + v.Name + " " + AlarmOpInfo.Text(r.Op, r.Operand)),
                })
            {
                Acked = state.Acked,
            });
        }
    }

    /// <summary>详情弹窗上方那六个事实格。</summary>
    private static List<MesValue> Facts(
        WebAlarmRule r, WebVariable v, string lineName, string stationName,
        DeviceView? dev, TimeSpan dur) => new()
    {
        new("设备名称", stationName),
        new("协议", dev?.ProtocolId ?? "—"),
        new("连接", dev?.Endpoint ?? "—"),
        new("所属产线", lineName),
        new("触发条件", v.Name + " " + AlarmOpInfo.Text(r.Op, r.Operand)),
        new("持续时间", Duration(dur)),
    };

    /// <summary>
    /// 详情弹窗里的「关联实时数据」表。
    /// </summary>
    /// <remarks>
    /// 触发这条报警的那个变量始终排第一并标红，哪怕它不在关联清单里——
    /// 打开详情第一眼要看的就是「到底是哪个值不对」。
    /// </remarks>
    private static List<MesRelatedValue> Related(
        List<string>? ids, Dictionary<string, WebVariable> vars, WebVariable trigger)
    {
        List<MesRelatedValue> rows = new()
        {
            new(trigger.Name, trigger.Address, trigger.DisplayValue, trigger.Unit, Abnormal: true),
        };

        foreach (string id in ids ?? new List<string>())
        {
            if (string.Equals(id, trigger.Id, StringComparison.Ordinal)) continue;

            WebVariable? v = Lookup(id, vars);
            if (v is null) continue;

            rows.Add(new MesRelatedValue(v.Name, v.Address, v.DisplayValue, v.Unit));
        }

        return rows;
    }

    /// <summary>把处理建议拆成分步的列表。</summary>
    /// <remarks>
    /// 配置里是一个输入框，现场习惯用换行或分号分步写。拆开之后弹窗里是
    /// 带序号的清单，照着做一条划一条；不拆就是一坨连在一起的长句。
    /// </remarks>
    private static List<string> Advice(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return new List<string> { "配置里没有填写处理建议。到「产线配置」补上。" };

        List<string> parts = text
            .Split(new[] { '\n', '\r', ';', '；' }, StringSplitOptions.RemoveEmptyEntries)
            .Select(s => s.Trim())
            .Where(s => s.Length > 0)
            .ToList();

        return parts.Count > 0 ? parts : new List<string> { text.Trim() };
    }

    // =========================================================================
    // 小工具
    // =========================================================================

    /// <summary>按 Id 找变量；Id 为空或已删除时返回 null。</summary>
    private static WebVariable? Lookup(string id, Dictionary<string, WebVariable> vars) =>
        string.IsNullOrWhiteSpace(id) ? null : vars.GetValueOrDefault(id);

    /// <summary>
    /// 读值是不是已经陈旧。
    /// </summary>
    /// <remarks>
    /// 判据随点位自己的扫描周期走，不是一个固定秒数。
    /// <para>
    /// 固定 10 秒会把慢点位判残：状态字若配成 20 秒一读，
    /// 每个周期里有一半时间「距上次读取已超过 10 秒」，
    /// 设备明明好好的，卡片却在 RUN 和 OFFLINE 之间来回跳。
    /// 现场看到的是一台时好时坏的设备，而实际什么事都没有。
    /// </para>
    /// <para>
    /// 没开轮询一律算陈旧。这种点位的值只可能来自某次手动读取，
    /// 之后再不会更新；放行的话，一台早就断了的设备会凭那一次读数
    /// 一直显示成正在运行。页脚那句「点位没开轮询……该站会显示成离线」
    /// 说的就是这一条。
    /// </para>
    /// 从没读过同样算陈旧——值还停在 "--"，那不是状态。
    /// </remarks>
    private static bool IsStale(WebVariable v)
    {
        if (!v.Polling) return true;
        if (v.LastReadAt is null) return true;

        // 下限保证快点位不会因为一两次抖动就判离线，
        // 倍数保证慢点位不会在自己的周期里被判死
        double limit = Math.Max(StaleFloorSeconds, v.ScanRateMs / 1000.0 * StaleScanCycles);

        return (DateTime.Now - v.LastReadAt.Value).TotalSeconds > limit;
    }

    /// <summary>合并宿主路由与本地配置，取一台设备的显示信息。</summary>
    private DeviceView? DeviceOf(string routeId)
    {
        if (string.IsNullOrWhiteSpace(routeId)) return null;

        return DeviceViewBuilder.Build(_session.Routes, _devices.GetAll())
            .FirstOrDefault(d => string.Equals(d.RouteId, routeId, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>整页顶部那个采集周期：全部被引用点位里最快的那个。</summary>
    /// <remarks>
    /// 页面刷新的快慢由最快的那条决定——各点位周期不一，
    /// 但人看到的是「这一屏多久动一次」。
    /// </remarks>
    private string PollText(Dictionary<string, WebVariable> vars, IReadOnlyList<WebLine> lines)
    {
        List<WebVariable> used = new();
        foreach (WebLine l in lines)
        {
            Add(l.State.VariableId);
            foreach (WebStation s in l.Stations) { Add(s.State.VariableId); Add(s.SubValueVariableId); }
            foreach (WebSoloDevice d in l.Solo) foreach (string id in d.FieldVariableIds) Add(id);
        }

        return PollText(used);

        void Add(string id)
        {
            WebVariable? v = Lookup(id, vars);
            if (v is not null && v.Polling) used.Add(v);
        }
    }

    /// <summary>一组变量里最快的扫描周期。</summary>
    private static string PollText(List<WebVariable> vars)
    {
        List<WebVariable> polling = vars.Where(v => v.Polling).ToList();
        if (polling.Count == 0) return "未轮询";

        int ms = polling.Min(v => v.ScanRateMs);
        return ms >= 1000 ? (ms / 1000.0).ToString("0.#") + " s" : ms + " ms";
    }

    /// <summary>把时长格式化成 <c>00:08:25</c>。</summary>
    private static string Duration(TimeSpan t) =>
        t < TimeSpan.Zero ? "00:00:00" : t.ToString(@"hh\:mm\:ss");
}
