// -----------------------------------------------------------------------------
// 文件: Services/Mes/DemoMesDataSource.cs
// 层级: UI 层 — MES 监控的演示数据
// 作用: 在产线配置与点位绑定做完之前，先撑起监控页的框架。
//
// 这是<b>演示数据，不是现场读数</b>：
//   本文件里的每一个数字都是写死的。它存在的唯一目的，是让页面结构、
//   交互与配色能先定下来并被评审——真实数据接进来之后，
//   新增一个读设备表与变量表的实现，在 Main.cs 换掉注册即可，页面不用改。
//
//   IsDemo 恒为 true，页面据此显示一条醒目的横幅。这条横幅不可省：
//   一屏看起来很真的产量、良率、温度，被当成现场读数用来做判断，
//   后果比界面难看严重得多。
// -----------------------------------------------------------------------------

namespace CommunicationKernel.UI.WebMaster.Services;

/// <summary>演示用的 MES 数据源。全部数值写死，仅供搭建与评审页面。</summary>
public sealed class DemoMesDataSource : IMesDataSource
{
    /// <summary>保护 <see cref="_alarms"/> 的确认状态。</summary>
    /// <remarks>
    /// 页面可能被多个浏览器会话同时打开，各有自己的渲染线程；
    /// 确认动作会改这份共享列表。
    /// </remarks>
    private readonly object _lock = new();

    /// <summary>演示报警。确认状态可变，因此只构造一次并复用。</summary>
    private readonly List<MesAlarm> _alarms;

    /// <summary>构造演示数据。</summary>
    public DemoMesDataSource() => _alarms = BuildAlarms();

    /// <inheritdoc />
    public MesSnapshot Snapshot()
    {
        List<MesAlarm> alarms;
        lock (_lock)
            alarms = _alarms.ToList();

        return new MesSnapshot(
            BuildLines(),
            alarms,
            "500 ms",
            // 用当前时间而不是写死一个时刻：写死的话页面看起来像卡住了，
            // 评审时第一个反应会是「刷新坏了」而不是「这是假数据」
            DateTime.Now.ToString("HH:mm:ss"),
            IsDemo: true);
    }

    /// <inheritdoc />
    public void Acknowledge(string alarmId)
    {
        if (string.IsNullOrWhiteSpace(alarmId)) return;

        lock (_lock)
        {
            MesAlarm? a = _alarms.FirstOrDefault(x => x.Id == alarmId);
            if (a is not null) a.Acked = true;
        }
    }

    /// <inheritdoc />
    public void AcknowledgeLine(string lineName)
    {
        if (string.IsNullOrWhiteSpace(lineName)) return;

        lock (_lock)
        {
            foreach (MesAlarm a in _alarms.Where(x => x.LineName == lineName))
                a.Acked = true;
        }
    }

    /// <summary>构造两条演示产线。</summary>
    /// <remarks>
    /// 刻意做成一条报警、一条正常：只有全绿的演示看不出报警态长什么样，
    /// 而报警态恰恰是这个页面存在的理由。
    /// </remarks>
    private static List<MesLine> BuildLines() => new()
    {
        new MesLine(
            "smt-1", "SMT-1 线", "LineCtrl-1", "192.168.0.10",
            MesState.Alarm,
            new MesKpi(1284, 1500, "98.52 %", "28.4 s", "00:08:25"),
            new List<MesStation>
            {
                new("s1", "上板机",  "Loader-01",    MesState.Run,   "1 284 pcs"),
                new("s2", "印刷机",  "DEK-NeoH",     MesState.Run,   "1 284 pcs"),
                new("s3", "SPI",     "KY-8030",      MesState.Run,   "99.1 %"),
                new("s4", "贴片机",  "NPM-W2",       MesState.Stop,  "飞达缺料预警", 1),
                new("s5", "回流焊",  "Heller-1809",  MesState.Run,   "245 ℃"),
                new("s6", "AOI",     "KY-Zenith",    MesState.Alarm, "NG 待确认", 1),
                new("s7", "下板机",  "Unloader-01",  MesState.Offline, ""),
            },
            new List<MesSoloDevice>
            {
                new("d1", "空压机变频器", "modbus-rtu · COM4 · 站 3", "500 ms", false, new List<MesValue>
                {
                    new("输出频率", "42.5", "Hz"),
                    new("输出电流", "18.2", "A"),
                    new("母线电压", "538", "V"),
                    new("累计运行", "3 412", "h"),
                }),
                new("d2", "升降台伺服", "panasonic-mewtocol · 192.168.0.90", "200 ms", false, new List<MesValue>
                {
                    new("当前位置", "128 450", "pls"),
                    new("转速", "1 500", "rpm"),
                    new("转矩", "36", "%"),
                    new("报警码", "0"),
                }),
                new("d3", "车间温湿度", "modbus-tcp · 192.168.0.95 · 站 1", "2 s", false, new List<MesValue>
                {
                    new("温度", "26.3", "℃"),
                    new("湿度", "58", "%RH"),
                    new("露点", "17.4", "℃"),
                    new("大气压", "101.2", "kPa"),
                }),
            }),

        new MesLine(
            "smt-2", "SMT-2 线", "LineCtrl-2", "192.168.0.20",
            MesState.Run,
            new MesKpi(1402, 1500, "99.10 %", "26.9 s", "00:00:00"),
            new List<MesStation>
            {
                new("t1", "上板机", "Loader-02",     MesState.Run, "1 402 pcs"),
                new("t2", "印刷机", "GKG-G5",        MesState.Run, "1 402 pcs"),
                new("t3", "贴片机", "NXT-III",       MesState.Run, "0 预警"),
                new("t4", "回流焊", "BTU-Pyramax",   MesState.Run, "248 ℃"),
            },
            new List<MesSoloDevice>
            {
                // 陈旧样例：灯灰、值灰、周期标黄。没有这个样例就看不出
                // 「读不到」和「读到 0」在界面上怎么区分
                new("d4", "纯水机流量计", "modbus-rtu · COM6 · 站 2", "1 s · 陈旧", true, new List<MesValue>
                {
                    new("瞬时流量", "12.4", "L/min"),
                    new("累计", "8 731", "m³"),
                }),
            }),
    };

    /// <summary>构造演示报警。</summary>
    private static List<MesAlarm> BuildAlarms() => new()
    {
        new MesAlarm(
            "a1", "AOI NG报警 · 待确认", MesSeverity.Critical,
            "SMT-1 线", "AOI", "02:42:10", "00:08:25", "AOI-NG-001",
            "AOI 检测站发现 PCB 焊点缺陷（NG），当前板件已停在检测位等待人工确认。",
            new List<MesValue>
            {
                new("设备名称", "AOI"),
                new("设备型号", "Koh Young Zenith"),
                new("连接", "192.168.0.60:502"),
                new("所属产线", "SMT-1"),
                new("报警代码", "AOI-NG-001"),
                new("持续时间", "00:08:25"),
            },
            new List<MesRelatedValue>
            {
                new("NG 计数", "D400", "56", "pcs", Abnormal: true),
                new("良率", "D402", "98.52", "%"),
                new("报警字", "D410", "0x0001", "—"),
                new("检测位有板", "R100", "ON", "—"),
            },
            new List<string>
            {
                "检查 AOI 相机与光源是否正常",
                "确认当前 NG 板件，决定放行或取出",
                "检查设备网络连接（192.168.0.60）",
                "确认后点击「确认报警」，再执行复位",
            },
            new List<MesTimelineItem>
            {
                new("02:42:10", "报警触发 · AOI NG 检测"),
                new("02:42:12", "整线状态切换为「报警中」"),
                new("02:42:12", "贴片机收到停线信号，状态 STOP"),
            }),

        new MesAlarm(
            "a2", "贴片机飞达缺料预警", MesSeverity.Warning,
            "SMT-1 线", "贴片机", "02:38:22", "00:12:13", "SMT-FDR-014",
            "贴片机 3 号飞达余料低于阈值，尚未断料。继续生产约可维持 12 分钟。",
            new List<MesValue>
            {
                new("设备名称", "贴片机"),
                new("设备型号", "Panasonic NPM-W2"),
                new("连接", "192.168.0.40:502"),
                new("所属产线", "SMT-1"),
                new("报警代码", "SMT-FDR-014"),
                new("持续时间", "00:12:13"),
            },
            new List<MesRelatedValue>
            {
                new("飞达号", "D200", "3", "—"),
                new("余料数", "D202", "180", "pcs", Abnormal: true),
                new("阈值", "D204", "200", "pcs"),
                new("预计可用", "D206", "12.4", "min"),
            },
            new List<string>
            {
                "备好对应料盘，确认物料型号与批次",
                "在贴片机上执行接料，或等待自动切换到备用飞达",
                "接料完成后确认本条预警",
            },
            new List<MesTimelineItem>
            {
                new("02:38:22", "余料低于阈值 200 pcs"),
                new("02:40:05", "预计可用时间跌破 15 min"),
            }),
    };
}
