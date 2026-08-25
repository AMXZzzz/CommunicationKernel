using System;
using System.IO;
using System.Linq;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Threading;
using CommunicationKernel.Engine.Router;
using CommunicationKernel.Engine.Router.Abstractions;
using CommunicationKernel.Engine.Runtime;
using CommunicationKernel.Engine.Runtime.Models;
using CommunicationKernel.Host.App.Services;
using Microsoft.AspNetCore.Connections;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.Logging;

// -----------------------------------------------------------------------------
// 文件: AppMain.cs
// 层级: Host.App / Entry
// 作用: 组合根 —— 装配依赖、映射 gRPC 端点、启动宿主。
// 说明:
// 1) Host 作为多 UI 统一中枢入口，仅暴露服务，不承载 UI 逻辑。
// 2) 这里是唯一知晓具体实现类型的位置：其余各层一律只依赖接口。
// 3) 并发控制在 Router 层与 RouteEntry 门控，本文件不涉及。
// 4) 监听地址由配置决定（appsettings.json 的 Kestrel:Endpoints:Grpc:Url），
//    默认 http://localhost:5000 与 WPF 客户端默认地址一致。
//    跨机部署（如宿主跑在树莓派、上位机在另一台）需改为 http://0.0.0.0:5000，
//    暴露面风险见文件末尾的启动告警。
// 5) Protocols 必须是 Http2，不能是 Http1AndHttp2：
//    gRPC 只跑在 HTTP/2 上，而明文端点没有 TLS ALPN 可供协议协商——
//    Kestrel 在明文上同时配两种协议时无法区分，会退回纯 HTTP/1.1，
//    此时所有 gRPC 调用被服务端以 HTTP_1_1_REQUIRED 直接拒绝。
// 6) launchSettings.json 里 launchBrowser 必须为 false，且不要写 applicationUrl：
//    · 本进程只暴露 gRPC（HTTP/2-only 明文），浏览器不做明文 h2c，
//      弹出来只会显示 "An HTTP/1.x request was sent to an HTTP/2 only endpoint."；
//    · applicationUrl（即 ASPNETCORE_URLS）优先级低于上面第 4 条的
//      Kestrel:Endpoints，写了也不生效，只会与实际绑定不符、误导排查。
//    另注：launchSettings.json 不接受注释——加了整个 profile 会静默失效
//    （dotnet run 报 "'/' is an invalid start of a property name"），
//    所以相关说明只能写在这里。
// -----------------------------------------------------------------------------

// ============================================================================
// 单实例：双击 exe 时如果已经有一个宿主在跑，第二个会因 :5000 被占立刻退出。
// Explorer 下控制台随进程关掉，操作员只看到「一闪而过」，以为没启动。
// 互斥量在绑端口之前拦截，并弹框说明。拿不到互斥量时不阻断，后面的绑定失败仍会接住。
// ============================================================================
Mutex? instanceLock = null;
try
{
    instanceLock = new Mutex(initiallyOwned: true, @"Local\CommunicationKernel.Host.App", out bool createdNew);
    if (!createdNew)
    {
        ReportDuplicateInstance();
        return;
    }
}
catch (AbandonedMutexException)
{
    // 上一个进程崩溃后互斥量被遗弃，本进程已经接手，继续启动
}
catch
{
    // 无命名互斥量权限等：不因此放弃启动
}

try
{

// ============================================================================
// 创建宿主
// ============================================================================

// 读取 appsettings / 环境变量，准备 DI 与 Kestrel 配置
var builder = WebApplication.CreateBuilder(args);

// ============================================================================
// 服务注册
// ============================================================================

// 注册 gRPC 基础设施（HTTP/2 + 高性能二进制协议）
builder.Services.AddGrpc(options =>
{
    // 预留服务级拦截器/限流等策略扩展点；当前保持默认最小配置
});

// 组合根：注册路由装配服务，隔离 EngineRuntime 与具体协议/传输装配实现
builder.Services.AddSingleton<IRouteAssemblyService>(sp =>
{
    // 插件目录：未配置时默认输出目录下的 plugins
    string pluginDirectorySetting = builder.Configuration["EngineRuntime:PluginDirectory"] ?? "plugins";

    // 串口最小 I/O 间隔（帧间静默窗口），单位毫秒；解析失败回落 15
    int defaultSerialIntervalMs = int.TryParse(builder.Configuration["EngineRuntime:DefaultSerialMinIoIntervalMs"], out int value)
        ? value
        : 15;

    // 相对路径按宿主基目录解析，避免不同启动目录导致插件目录漂移
    string resolvedPluginDirectory = Path.IsPathRooted(pluginDirectorySetting)
        ? pluginDirectorySetting
        : Path.Combine(AppContext.BaseDirectory, pluginDirectorySetting);

    // 装配服务需要日志工厂，以便插件加载失败时写启动日志
    var loggerFactory = sp.GetRequiredService<ILoggerFactory>();

    // 插件装配：扫描目录、实例化协议/传输工厂，供 EngineRuntime 按需组装路由
    return new PluginRouteAssemblyService(
        pluginDirectory: resolvedPluginDirectory,
        defaultSerialMinIoIntervalMs: defaultSerialIntervalMs,
        loggerFactory: loggerFactory);
});

// 路由表：跨路由并行、同路由串行；日志可为空（ConnectionRouter 内部容忍 null）
builder.Services.AddSingleton<IConnectionRouter>(sp =>
    new ConnectionRouter(sp.GetService<ILogger<ConnectionRouter>>()));

// 读合并：相同地址的并发读合成一次 I/O
builder.Services.AddSingleton<IReadCoordinator, ReadCoordinator>();

// 编排器：把路由表与读合并绑在一起
builder.Services.AddSingleton<IRouterOrchestrator, RouterOrchestrator>();

// 通讯内核：只依赖上面的抽象，不直接 new 协议/传输
builder.Services.AddSingleton<EngineRuntime>();

// ============================================================================
// 构建应用
// ============================================================================

// 冻结服务容器，生成可运行的 WebApplication
var app = builder.Build();

// 映射 gRPC 服务端点（Health / 路由 / 读写均走 HostGrpcService）
app.MapGrpcService<HostGrpcService>();

// 辅助根路由：浏览器直连时给出引导，避免空白 404
app.MapGet("/", () => "CommunicationKernel.Host.App is running. Use a gRPC client to call endpoints./ [引导文]: CommunicationKernel.Host.App 服务端初始化Done ");

// ============================================================================
// 监听地址的暴露面告警
// ============================================================================
// 本 gRPC 端点没有任何认证与授权：能建立连接就能注册路由、读写 PLC 寄存器。
// 绑定到非回环地址意味着同一网段的任何主机都可以直接操作现场设备。
//
// 跨机部署（宿主在树莓派、上位机在别的机器）确实需要这么做，所以不禁止；
// 但必须让运维在启动日志里明确看到自己打开了什么，而不是默认静默放行。
//
// 在 ApplicationStarted 里读 IServerAddressesFeature 而非读配置：
// 命令行 --urls 与环境变量 ASPNETCORE_URLS 会覆盖 appsettings，
// 只有服务器实际绑定完成后拿到的地址才是真正生效的那一份。
app.Lifetime.ApplicationStarted.Register(() =>
{
    // 独立分类名，journalctl 可按 Host.App.Endpoint 过滤
    var logger = app.Services.GetRequiredService<ILoggerFactory>().CreateLogger("Host.App.Endpoint");

    // 实际绑定地址（含 --urls / ASPNETCORE_URLS 覆盖后的结果）
    var addresses = app.Services.GetRequiredService<IServer>()
        .Features.Get<IServerAddressesFeature>()?.Addresses;

    // 拿不到地址特性（理论上只发生在被替换的服务器实现上），跳过告警
    if (addresses is null || addresses.Count == 0)
    {
        // 非标准服务器实现，无法判断暴露面，只记一条警告
        logger.LogWarning("无法获取实际监听地址，跳过暴露面检查。");
        return;
    }

    // 逐地址检查：回环仅本机可达，非回环则警告无认证暴露
    foreach (string address in addresses)
    {
        // 回环地址——仅本机可达，无需告警
        bool isLoopback =
            address.Contains("localhost", StringComparison.OrdinalIgnoreCase) ||
            address.Contains("127.0.0.1", StringComparison.Ordinal) ||
            address.Contains("[::1]", StringComparison.Ordinal);

        // 回环地址不告警，只记 Information 方便确认绑定成功
        if (isLoopback)
        {
            logger.LogInformation("gRPC 监听 {Address}（仅本机可达）。", address);
            continue;
        }

        // 非回环——同网段可达，且服务端无认证
        logger.LogWarning(
            "gRPC 监听 {Address}：该地址可被同网段主机访问，而本服务不做任何认证——" +
            "连上即可读写 PLC。请确认已通过防火墙、VLAN 或带认证的反向代理限制访问范围。",
            address);
    }
});

// ============================================================================
// 插件预热
// ============================================================================
// IRouteAssemblyService 是懒构造的单例：不主动解析，插件要等到第一次 gRPC 调用
// 才加载。而插件加载失败是静默的（共享契约泄漏、目录缺失、工厂实例化异常都只记日志），
// 于是无人值守部署会出现「服务好好跑着、直到有人操作才发现一个协议都没有」。
// 在这里提前解析，把协议清单写进启动日志——树莓派上只有 journalctl 可看。
{
    // 启动诊断分类名，journalctl 可按 Host.App.Startup 过滤
    var logger = app.Services.GetRequiredService<ILoggerFactory>().CreateLogger("Host.App.Startup");

    // 触发单例构造，从而扫描 plugins 目录
    var assemblyService = app.Services.GetRequiredService<IRouteAssemblyService>();

    // 已成功实例化的协议工厂清单
    var protocols = assemblyService.GetAvailableProtocols();

    // 一个协议都没加载到——服务在这种状态下无法完成任何实际工作
    if (protocols.Count == 0)
    {
        logger.LogError(
            "未加载到任何协议插件。请检查插件目录是否存在、其中是否有插件 DLL，" +
            "以及共享契约是否误被复制进插件目录（那会让所有工厂静默注册不上）。");
    }
    else
    {
        // 至少有一个协议，把 ID 清单打到启动日志供运维核对
        logger.LogInformation("已加载 {Count} 个协议：{Protocols}",
            protocols.Count, string.Join(", ", protocols.Select(p => p.ProtocolId)));
    }
}

// ============================================================================
// 运行
// ============================================================================
// 阻塞运行，直至进程收到停止信号。
//
// 单独接住绑定失败：端口被占用时 Kestrel 抛 IOException，
// 不接的话调试器直接断在 app.Run()，命令行下则是一整屏堆栈，
// 而真正有用的信息只有一句「端口被占了」。
// 树莓派上更要紧——systemd 配了 Restart=always，
// 一个没说清楚的绑定失败会变成无限重启，日志里全是同一份堆栈。
try
{
    app.Run();
}
catch (Exception ex) when (IsAddressInUse(ex))
{
    // 这里<b>不能</b>再用 app.Services 取 ILogger：
    // app.Run() 抛出时宿主已经把 DI 容器释放了，
    // GetRequiredService 会再抛一个 ObjectDisposedException，
    // 把真正有用的「端口被占了」盖成一句莫名其妙的「Cannot access a disposed object」。
    ReportStartupFailure(
        "监听地址已被占用，宿主无法启动。\n\n" +
        "多半是已经有一个 Host.App 在跑了（任务管理器里看 CommunicationKernel.Host.App）。\n" +
        "关掉网页不会停宿主；要停掉请关这个黑窗口，或结束该进程。\n\n" +
        "原始错误：" + ex.Message);
    Environment.Exit(1);
}
catch (Exception ex)
{
    ReportStartupFailure("宿主启动失败。\n\n" + ex.Message);
    Environment.Exit(1);
}

GC.KeepAlive(instanceLock);

}
catch (Exception ex)
{
    // CreateBuilder / 插件预热阶段的失败同样会让双击「一闪而过」
    ReportStartupFailure("宿主启动失败。\n\n" + ex.Message);
    Environment.Exit(1);
}

// ============================================================================
// 启动失败时让操作员看得见
// ============================================================================

/// <summary>已经有一个宿主进程时的提示（双击第二份 exe）。</summary>
static void ReportDuplicateInstance()
{
    const string message =
        "CommunicationKernel.Host.App 已经在运行，不必再开一份。\n\n" +
        "关掉浏览器不会停宿主。本机 Web 打开 http://localhost:64000 即可。\n" +
        "真要重启：任务管理器结束 CommunicationKernel.Host.App，再双击本程序。";
    Console.Error.WriteLine();
    Console.Error.WriteLine("[启动失败] " + message);
    ShowStartupDialog(message);
    HoldForOperator();
}

/// <summary>把启动失败写到 stderr；Windows 双击时再弹框，避免窗口一闪而过。</summary>
static void ReportStartupFailure(string message)
{
    Console.Error.WriteLine();
    Console.Error.WriteLine("[启动失败] " + message);
    Console.Error.WriteLine();
    ShowStartupDialog(message);
    HoldForOperator();
}

/// <summary>Windows 桌面会话弹消息框；无桌面 / Linux 只靠 stderr（systemd 进 journal）。</summary>
static void ShowStartupDialog(string message)
{
    if (!OperatingSystem.IsWindows())
        return;
    try
    {
        // 0x00000010 = MB_ICONERROR，0x00040000 = MB_TOPMOST
        _ = MessageBoxW(
            IntPtr.Zero,
            message,
            "CommunicationKernel 宿主启动失败",
            0x00000010 | 0x00040000);
    }
    catch
    {
        // 服务模式、无桌面：弹框失败是正常的
    }
}

/// <summary>无消息框可弹时（Linux 终端、重定向失败）停一下，让人看清 stderr。</summary>
static void HoldForOperator()
{
    if (OperatingSystem.IsWindows())
        return;
    if (!Environment.UserInteractive || Console.IsInputRedirected)
        return;
    Console.Error.WriteLine("按任意键关闭...");
    try { Console.ReadKey(intercept: true); } catch { }
}

/// <summary>
/// 端口占用。Windows 中文系统的 SocketException 文案不含 "address already in use"，
/// 只认英文会接不住，双击仍然一闪而过。
/// </summary>
static bool IsAddressInUse(Exception ex)
{
    for (Exception? e = ex; e is not null; e = e.InnerException)
    {
        if (e is AddressInUseException)
            return true;
        if (e is SocketException se && se.SocketErrorCode == SocketError.AddressAlreadyInUse)
            return true;
        if (e.Message.Contains("address already in use", StringComparison.OrdinalIgnoreCase))
            return true;
        if (e.Message.Contains("只允许使用一次", StringComparison.Ordinal))
            return true;
    }
    return false;
}

/// <summary>Win32 消息框，仅用于启动失败提示。</summary>
[DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
static extern int MessageBoxW(IntPtr hWnd, string text, string caption, uint type);
