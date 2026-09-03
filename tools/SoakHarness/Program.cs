// -----------------------------------------------------------------------------
// 文件: tools/SoakHarness/Program.cs
// 层级: 压测工具（不属于产品代码，不在解决方案里）
// 作用: 用上百台 PLC 同时满负荷工作的形态压测引擎。
//
// 覆盖的链路（全部是真的，没有替身）:
//   插件加载(AssemblyLoadContext) → 引擎路由 → 每路由独占门控 → 真实 TCP 传输
//   → Modbus 分帧与校验 → 每台 PLC 一个独立的回环从站
//
// 为什么每台 PLC 必须是独立端点:
//   若所有路由都打同一个端口，测的其实是"一台设备被 N 路并发访问"，
//   与现场完全不同。真实形态是：
//     · 每台 PLC 一条独立 TCP 连接、一个独立门控；
//     · 同一台 PLC 内部的读写<b>串行</b>（真实 PLC 无法处理交错请求）；
//     · 不同 PLC 之间<b>并行</b>。
//   这个「单机串行、多机并行」的形态才是引擎调度真正要扛的东西，
//   也是连接数、句柄数、线程调度压力的真正来源。
//
// 要找的是什么:
//   · 托管堆 / 工作集单调增长 → 内存泄漏
//   · 线程数 / 句柄数持续爬升 → 连接未释放或任务未回收
//   · 吞吐随时间衰减        → 集合无界增长，或锁竞争恶化
//   · 单台 PLC 上出现并发   → 门控失效（会导致请求与响应错配）
//
// 刻意不打真实隧道:
//   打满生产服务器的 frp 隧道会让瓶颈落在网络上，反而掩盖引擎自身的问题，
//   还会挤占那台机器上其它服务。本机回环能全速跑且不影响任何外部系统。
//
// 用法:
//   dotnet run -c Release --project tools/SoakHarness -- --minutes 10 --plcs 120
//   参数: --minutes <分钟> --plcs <PLC台数> --workers <并发数> --out <csv路径>
// -----------------------------------------------------------------------------

using System.Diagnostics;
using System.Globalization;
using CommunicationKernel.Core.Abstractions.Errors;
using CommunicationKernel.Core.Abstractions.Results;
using CommunicationKernel.Core.EngineRouter;
using CommunicationKernel.Core.EngineRuntime;
using CommunicationKernel.Core.EngineRuntime.Models;

namespace CommunicationKernel.SoakHarness;

internal static class Program
{
    private static long _ops;
    private static long _errors;
    private static long _churns;
    private static readonly Dictionary<KernelErrorCode, long> ErrorsByCode = new();
    private static readonly object ErrorLock = new();

    /// <summary>每种错误码留几条原文样本，光有错误码看不出根因。</summary>
    private const int SamplesPerCode = 5;

    /// <summary>错误原文样本，按错误码归档。</summary>
    private static readonly Dictionary<KernelErrorCode, List<string>> SampleMessages = new();

    private static async Task<int> Main(string[] args)
    {
        double minutes = ArgDouble(args, "--minutes", 10);
        int plcs = ArgInt(args, "--plcs", 120);
        int workers = ArgInt(args, "--workers", plcs * 2);

        // 搅动间隔（秒）；0 表示完全不搅动，用于做"是不是搅动带来的泄漏"的对照
        int churnSeconds = ArgInt(args, "--churn-seconds", 5);
        string csvPath = ArgString(args, "--out",
            Path.Combine(AppContext.BaseDirectory, $"soak-{DateTime.Now:yyyyMMdd-HHmmss}.csv"));

        // 短跑要密采样才看得出趋势；长跑要疏采样以免 CSV 过大
        TimeSpan sampleInterval = minutes <= 30 ? TimeSpan.FromSeconds(15) : TimeSpan.FromSeconds(30);

        string pluginDir = ArgString(args, "--plugins", FindPluginDir());
        if (!Directory.Exists(pluginDir))
        {
            Console.Error.WriteLine($"找不到插件目录: {pluginDir}");
            Console.Error.WriteLine("请先 dotnet build CommunicationKernel.slnx -c Release，或用 --plugins 指定。");
            return 1;
        }

        Console.WriteLine($"插件目录 : {pluginDir}");
        Console.WriteLine($"时长     : {minutes} 分钟");
        Console.WriteLine($"PLC 台数 : {plcs}（每台一个独立 TCP 端点）");
        Console.WriteLine($"并发     : {workers}");
        Console.WriteLine($"搅动     : {(churnSeconds > 0 ? churnSeconds + " 秒一次" : "关闭（对照组）")}");
        Console.WriteLine($"采样输出 : {csvPath}");
        Console.WriteLine();

        // ── 起 N 台独立的回环 PLC ──
        var slaves = new List<ModbusTcpEchoServer>(plcs);
        for (int i = 0; i < plcs; i++)
        {
            var s = new ModbusTcpEchoServer();
            s.Start();
            slaves.Add(s);
        }
        Console.WriteLine($"已启动 {slaves.Count} 台回环 PLC，端口 {slaves[0].Port}…{slaves[^1].Port}");

        var assembly = new PluginRouteAssemblyService(pluginDir);
        await using var engine = new EngineRuntime(
            assembly, new RouterOrchestrator(new ConnectionRouter(), new ReadCoordinator()));

        // 一台 PLC 一条路由；记住端口，搅动后重建时要用同一个
        var portByRoute = new Dictionary<string, int>(plcs);
        var routeIds = new List<string>(plcs);
        for (int i = 0; i < plcs; i++)
        {
            string id = $"plc-{i:D3}";
            OperationResult<string> r = await engine.RegisterRouteAsync(
                NewCommand(id, slaves[i].Port), CancellationToken.None);
            if (!r.Success)
            {
                Console.Error.WriteLine($"注册 {id} 失败: {r.ErrorMessage}");
                return 2;
            }
            routeIds.Add(id);
            portByRoute[id] = slaves[i].Port;
        }
        Console.WriteLine($"已注册 {routeIds.Count} 条路由\n");

        using var stop = new CancellationTokenSource(TimeSpan.FromMinutes(minutes));
        Console.CancelKeyPress += (_, e) => { e.Cancel = true; stop.Cancel(); };

        var sw = Stopwatch.StartNew();
        Process self = Process.GetCurrentProcess();

        // ── 压测工作者：持续读，少量写，随机落到各台 PLC ──
        var tasks = new List<Task>();
        for (int w = 0; w < workers; w++)
        {
            int seed = w;
            tasks.Add(Task.Run(async () =>
            {
                var rnd = new Random(seed);
                while (!stop.IsCancellationRequested)
                {
                    string routeId = routeIds[rnd.Next(routeIds.Count)];
                    string addr = (rnd.Next(100) + 1).ToString(CultureInfo.InvariantCulture);
                    try
                    {
                        // 九成读一成写，贴近现场（轮询为主、偶尔下发）
                        if (rnd.Next(10) == 0)
                        {
                            OperationResult wr = await engine.WriteByRouteIdAsync(
                                routeId, addr, new byte[] { 0x12, 0x34 }, stop.Token);
                            Record(wr.Success, wr.ErrorCode, wr.ErrorMessage);
                        }
                        else
                        {
                            OperationResult<byte[]> rr = await engine.ReadByRouteIdAsync(
                                routeId, addr, 2, stop.Token);
                            Record(rr.Success, rr.ErrorCode, rr.ErrorMessage);
                        }
                    }
                    catch (OperationCanceledException) { return; }
                    catch (Exception ex)
                    {
                        // 异常冲出内核是硬故障，必须显眼
                        Console.Error.WriteLine($"[异常逃逸] {ex.GetType().Name}: {ex.Message}");
                        Interlocked.Increment(ref _errors);
                    }
                }
            }, stop.Token));
        }

        // ── 搅动者：周期性注销并重建某台 PLC 的路由，模拟现场改设备 ──
        if (churnSeconds > 0)
        tasks.Add(Task.Run(async () =>
        {
            var rnd = new Random(9999);
            while (!stop.IsCancellationRequested)
            {
                try
                {
                    await Task.Delay(TimeSpan.FromSeconds(churnSeconds), stop.Token);
                    string id = routeIds[rnd.Next(routeIds.Count)];
                    await engine.UnregisterRouteAsync(id, CancellationToken.None);
                    await engine.RegisterRouteAsync(
                        NewCommand(id, portByRoute[id]), CancellationToken.None);
                    Interlocked.Increment(ref _churns);
                }
                catch (OperationCanceledException) { return; }
                catch (Exception ex)
                {
                    Console.Error.WriteLine($"[搅动异常] {ex.GetType().Name}: {ex.Message}");
                }
            }
        }, stop.Token));

        // ── 采样：写 CSV 并打一行到控制台 ──
        await using (var csv = new StreamWriter(csvPath, append: false))
        {
            await csv.WriteLineAsync(
                "elapsed_s,ops,ops_per_s,errors,churns,plc_served,heap_mb,workingset_mb,threads,handles,gc0,gc1,gc2");
            await csv.FlushAsync();

            long lastOps = 0;
            var lastAt = TimeSpan.Zero;

            while (!stop.IsCancellationRequested)
            {
                try { await Task.Delay(sampleInterval, stop.Token); }
                catch (OperationCanceledException) { break; }

                self.Refresh();
                TimeSpan now = sw.Elapsed;
                long ops = Interlocked.Read(ref _ops);
                double perSec = (ops - lastOps) / Math.Max(0.001, (now - lastAt).TotalSeconds);
                lastOps = ops; lastAt = now;

                long served = 0;
                foreach (ModbusTcpEchoServer s in slaves) served += s.Served;

                double heapMb = GC.GetTotalMemory(forceFullCollection: false) / 1024.0 / 1024.0;
                double wsMb = self.WorkingSet64 / 1024.0 / 1024.0;

                await csv.WriteLineAsync(string.Join(',',
                    now.TotalSeconds.ToString("F0", CultureInfo.InvariantCulture),
                    ops, perSec.ToString("F0", CultureInfo.InvariantCulture),
                    Interlocked.Read(ref _errors), Interlocked.Read(ref _churns), served,
                    heapMb.ToString("F1", CultureInfo.InvariantCulture),
                    wsMb.ToString("F1", CultureInfo.InvariantCulture),
                    self.Threads.Count, self.HandleCount,
                    GC.CollectionCount(0), GC.CollectionCount(1), GC.CollectionCount(2)));
                await csv.FlushAsync();   // 立刻落盘：进程被杀也能保住已采数据

                Console.WriteLine(
                    $"[{now:hh\\:mm\\:ss}] {perSec,7:F0} op/s  累计 {ops,12:N0}  " +
                    $"错误 {Interlocked.Read(ref _errors),7:N0}  " +
                    $"堆 {heapMb,7:F1}MB  工作集 {wsMb,7:F1}MB  线程 {self.Threads.Count,4}  句柄 {self.HandleCount,6}");
            }
        }

        stop.Cancel();
        try { await Task.WhenAll(tasks).WaitAsync(TimeSpan.FromSeconds(10)); } catch { }

        long totalServed = 0;
        foreach (ModbusTcpEchoServer s in slaves) totalServed += s.Served;
        PrintSummary(sw.Elapsed, totalServed, plcs, csvPath);

        foreach (ModbusTcpEchoServer s in slaves) await s.DisposeAsync();
        return 0;
    }

    /// <summary>记一次操作结果。</summary>
    private static void Record(bool success, KernelErrorCode code, string? message = null)
    {
        Interlocked.Increment(ref _ops);
        if (success) return;

        Interlocked.Increment(ref _errors);
        lock (ErrorLock)
        {
            ErrorsByCode[code] = ErrorsByCode.GetValueOrDefault(code) + 1;

            if (string.IsNullOrEmpty(message)) return;
            List<string> bucket = SampleMessages.TryGetValue(code, out List<string>? b) ? b : SampleMessages[code] = new();
            if (bucket.Count < SamplesPerCode && !bucket.Contains(message))
                bucket.Add(message);
        }
    }

    /// <summary>收尾汇总。</summary>
    private static void PrintSummary(TimeSpan elapsed, long served, int plcs, string csvPath)
    {
        long ops = Interlocked.Read(ref _ops);
        long errors = Interlocked.Read(ref _errors);
        Console.WriteLine();
        Console.WriteLine("──────── 压测结束 ────────");
        Console.WriteLine($"时长        : {elapsed:d\\.hh\\:mm\\:ss}");
        Console.WriteLine($"PLC 台数    : {plcs}");
        Console.WriteLine($"总操作      : {ops:N0}（平均 {ops / Math.Max(1, elapsed.TotalSeconds):F0} op/s，" +
                          $"折合每台 PLC {ops / Math.Max(1, elapsed.TotalSeconds) / Math.Max(1, plcs):F0} op/s）");
        Console.WriteLine($"错误        : {errors:N0}（{100.0 * errors / Math.Max(1, ops):F4}%）");
        Console.WriteLine($"路由搅动    : {Interlocked.Read(ref _churns):N0} 次");
        Console.WriteLine($"PLC 已服务  : {served:N0} 个请求");

        lock (ErrorLock)
        {
            if (ErrorsByCode.Count > 0)
            {
                Console.WriteLine("错误分布    :");
                foreach ((KernelErrorCode code, long n) in ErrorsByCode.OrderByDescending(kv => kv.Value))
                {
                    Console.WriteLine($"    {code,-24} {n:N0}");
                    if (SampleMessages.TryGetValue(code, out List<string>? msgs))
                        foreach (string m in msgs)
                            Console.WriteLine($"        · {m}");
                }
            }
        }
        Console.WriteLine($"采样 CSV    : {csvPath}");
    }

    /// <summary>造一条指向某台回环 PLC 的 Modbus TCP 注册命令。</summary>
    /// <remarks>
    /// 站号固定 1：每台 PLC 是独立端点，靠 IP:端口 区分，不需要靠站号。
    /// 这也贴近以太网现场——站号是串口多点总线才需要的东西。
    /// </remarks>
    private static RegisterRouteCommand NewCommand(string routeId, int port) => new()
    {
        RouteId = routeId,
        ProtocolId = "modbus-tcp",
        TransportKind = "Tcp",
        Address = "127.0.0.1",
        Port = port,
        Station = "1",
    };

    /// <summary>在仓库里找已构建的 plugins 目录。</summary>
    private static string FindPluginDir()
    {
        DirectoryInfo? dir = new(AppContext.BaseDirectory);
        while (dir is not null)
        {
            string candidate = Path.Combine(
                dir.FullName, "CommunicationKernel.Hosting.App", "bin", "Release", "net8.0", "plugins");
            if (Directory.Exists(candidate)) return candidate;
            dir = dir.Parent;
        }
        return string.Empty;
    }

    private static string ArgString(string[] a, string k, string d)
    {
        int i = Array.IndexOf(a, k);
        return i >= 0 && i + 1 < a.Length ? a[i + 1] : d;
    }

    private static int ArgInt(string[] a, string k, int d) =>
        int.TryParse(ArgString(a, k, d.ToString(CultureInfo.InvariantCulture)),
            NumberStyles.Integer, CultureInfo.InvariantCulture, out int v) ? v : d;

    private static double ArgDouble(string[] a, string k, double d) =>
        double.TryParse(ArgString(a, k, d.ToString(CultureInfo.InvariantCulture)),
            NumberStyles.Float, CultureInfo.InvariantCulture, out double v) ? v : d;
}
