// -----------------------------------------------------------------------------
// 文件: Program.cs
// 层级: UI 层 — Blazor Server 应用程序入口
// 作用: 注册会话、日志、设备/变量持久化，配置中间件与组件路由。
//
// 无控制台运行:
//   本项目是 WinExe（见 csproj），直接运行 exe 不会弹控制台窗口。
//   代价是启动失败会变成"什么都不发生"——没有黑框也就没有任何错误输出。
//   因此整个启动流程被 try/catch 兜住，失败时写日志文件 + 弹消息框。
//   改动本文件时不要把那段兜底删掉，否则端口被占之类的问题会完全静默。
//
// 托盘驻留:
//   进程在右下角托盘。关闭浏览器不会退出；退出、打开界面、看日志都走托盘菜单。
//   再双击一次 exe 会通知已在跑的实例打开界面，而不是再起一份去抢端口。
//   从 VS / dotnet run 启动时会继承一个黑框，启动后立刻拆掉（HideAttachedConsole）。
//
// 浏览器由谁打开:
//   由 LanAccess.OpenBrowser 负责，受配置项 Web:LaunchBrowser 控制（默认开）。
//   launchSettings.json 里的 launchBrowser 已相应改为 false——
//   两边都开会在 `dotnet run` 时弹出两个标签页。
//   launchSettings.json 是严格 JSON，**不能写注释**（写了会让整个 profile
//   静默失效，dotnet run 直接忽略其中的所有设置），所以这条说明只能放在这里。
// -----------------------------------------------------------------------------

using System.Runtime.InteropServices;
using CommunicationKernel.Hosting.App;
using CommunicationKernel.Hosting.Sdk;
using CommunicationKernel.UI.WebMaster.Components;
using CommunicationKernel.UI.WebMaster.Services;
using Microsoft.AspNetCore.Components.Server;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Server.Kestrel.Core;

// 托盘程序不需要控制台。WinExe 双击本来没有；VS / dotnet run 会塞一个黑框过来。
HideAttachedConsole();

//! 互斥体
Mutex? instanceLock = null;
Mutex? hostingLock = null;
EventWaitHandle? showEvent = null;

try {

    // ============================================================================
    // 单实例：已经在托盘里跑时，再双击只是把界面唤出来
    // ============================================================================
    try {
        //! 尝试创建一个互斥体, 创建成功返回true ,  创建失败,即存在一个互斥体,则返回false
        instanceLock = new Mutex(initiallyOwned: true, TrayHost.MutexName, out bool createdNew);
        if (!createdNew) {
            //! 唤起互斥体寄生进程
            NotifyRunningInstance();
            return;
        }
    } catch (AbandonedMutexException) {
        // 上一份崩溃后互斥量被遗弃，本进程已经接手，继续启动
    } catch {
        // 没命名互斥量权限：不因此放弃启动
    }

    //! 创建事件句柄
    showEvent = new EventWaitHandle(false, EventResetMode.AutoReset, TrayHost.ShowEventName);
    EventWaitHandle showSignal = showEvent;

    // 本进程同时持有 Hosting.App 互斥量：独立 exe 再开会抢同一份引擎和 :5000
    try {
        hostingLock = new Mutex(initiallyOwned: true, HostingComposition.InstanceMutexName, out bool hostingFree);
        if (!hostingFree) {
            //! 弹出提示
            ReportHostingAlreadyRunning();
            return;
        }
    } catch (AbandonedMutexException) {
        // 上一份 Hosting.App 崩溃后互斥量被遗弃，本进程已经接手
    } catch {
        // 没权限不因此放弃；后面绑 :5000 失败仍会接住
    }

    // ASPNETCORE_URLS 会压掉下面的双端口绑定
    string? inheritedUrls = Environment.GetEnvironmentVariable("ASPNETCORE_URLS");
    if (!string.IsNullOrWhiteSpace(inheritedUrls)) {
        Environment.SetEnvironmentVariable("ASPNETCORE_URLS", null);
    }

    //! 创建Web App工厂
    WebApplicationBuilder builder = WebApplication.CreateBuilder(args);

    //! 枚举端口
    int listenPort = WebSettingsStore.ResolveListenPort(builder.Configuration, inheritedUrls);
    int grpcPort = builder.Configuration.GetValue("Hosting:GrpcPort", HostingComposition.DefaultGrpcPort);
    if (grpcPort is < 1024 or > 65535 || grpcPort == listenPort)
        grpcPort = HostingComposition.DefaultGrpcPort == listenPort ? 5001 : HostingComposition.DefaultGrpcPort;

    //! 配置协议
    builder.WebHost.ConfigureKestrel(options => {
        options.ListenAnyIP(listenPort, listen => listen.Protocols = HttpProtocols.Http1AndHttp2);
        options.ListenAnyIP(grpcPort, HostingComposition.ListenGrpc);
    });

    // ============================================================================
    // 服务注册
    // ============================================================================

    // 操作员日志：先建 Store，再挂到 Logging，保证页面与 ILogger 共用一份缓冲
    AppLogStore logStore = new();
    builder.Services.AddSingleton(logStore);
    builder.Logging.AddProvider(new AppLogLoggerProvider(logStore));

    // Blazor Server 组件和交互支持
    builder.Services.AddRazorComponents()
        .AddInteractiveServerComponents();

    // 断线保留电路，避免操作员刷新前短暂掉线就把页面状态清掉
    builder.Services.Configure<CircuitOptions>(options => {
        options.DisconnectedCircuitRetentionPeriod = TimeSpan.FromMinutes(3);
        options.DetailedErrors = builder.Environment.IsDevelopment();
    });

    // 本地持久化（设备 / 变量 / Web 端口）
    builder.Services.AddSingleton<WebSettingsStore>();
    builder.Services.AddSingleton<WebDeviceStore>();
    builder.Services.AddSingleton<WebVariableStore>();
    builder.Services.AddSingleton<WebTemplateStore>();

    // 本进程带上 Hosting.App：同一份组合根，gRPC 对外，UI 经 HostingClient 走回环。
    HostingComposition.AddServices(builder.Services, builder.Configuration);
    string localGrpc = "http://127.0.0.1:" + grpcPort;
    builder.Services.AddSingleton<IHostingClient>(sp =>
        new HostingClient(localGrpc, sp.GetRequiredService<ILogger<HostingClient>>()));

    // 会话：单例即 HostedService
    builder.Services.AddSingleton<EngineSession>();
    builder.Services.AddHostedService(sp => sp.GetRequiredService<EngineSession>());

    // 设备操作：页面唯一的设备入口，页面不得再直接持有 IHostingClient。
    // 对应 WPF 端的 IDeviceService / GrpcDeviceService。
    builder.Services.AddSingleton<IWebDeviceService, WebDeviceService>();

    // 变量读写：页面与后台轮询器共用，保证字节序处理不再分叉。
    builder.Services.AddSingleton<IWebVariableService, WebVariableService>();

    // 变量轮询：Host 离线时跳过
    builder.Services.AddHostedService<VariablePoller>();

    // 托盘：关浏览器不等于退出
    builder.Services.AddSingleton(showSignal);
    builder.Services.AddHostedService<TrayHost>();

    // ============================================================================
    // 构建应用
    // ============================================================================

    WebApplication app = builder.Build();

    {
        var protocols = HostingComposition.Warmup(app);
        HostingComposition.MapEndpoints(app);
        if (protocols.Count == 0)
            logStore.Error("Engine", "未加载到任何协议插件，设备将无法选择协议");
        else
            logStore.Info("Engine", "已加载 " + protocols.Count + " 个协议；gRPC " + localGrpc);
    }

    // 生产环境：异常处理中间件（开发环境 Blazor 有内置错误 UI）
    if (!app.Environment.IsDevelopment())
        app.UseExceptionHandler("/Error", createScopeForErrors: true);

    // 静态文件（wwwroot 下的 CSS、图片、脚本）
    app.UseStaticFiles();

    // 防 CSRF（Blazor 表单组件需要）
    app.UseAntiforgery();

    // Blazor 组件路由（含 Interactive Server）
    app.MapRazorComponents<App>()
        .AddInteractiveServerRenderMode();

    // 监听就绪后再开浏览器、记下局域网地址。
    //
    // 必须挂在 ApplicationStarted 上而不是 Run 之前：端口尚未绑定时打开浏览器，
    // 用户看到的是"无法访问此网站"，然后手动刷新才好——那比不开还差。
    //
    // 地址从 IServerAddressesFeature 取实际绑定值，不写死端口：
    // appsettings 或命令行随时可能改端口，写死会打开一个空白页。
    app.Lifetime.ApplicationStarted.Register(() => {
        LogListenAddresses(app, listenPort, grpcPort);
        if (builder.Configuration.GetValue("Web:LaunchBrowser", defaultValue: true))
            LanAccess.OpenBrowser(app.Services.GetService<IServer>());
    });

    // 阻塞监听，直到进程收到停止信号（托盘「退出」或系统关机）
    app.Run();

} catch (Exception ex) {
    // 无控制台时这是唯一的错误出口。最常见的两种：
    //   端口被占（上一次没退干净，或别的程序占了 5000/64000）
    //   appsettings.json 语法错误（改配置时漏了逗号或引号）
    ReportStartupFailure(ex);

    // 非 0 退出码便于脚本与看门狗识别启动失败
    Environment.ExitCode = 1;
} finally {
    showEvent?.Dispose();
    hostingLock?.Dispose();
    instanceLock?.Dispose();
}

// ============================================================================
// 启动后打开浏览器
// ============================================================================

/// <summary>
/// 把实际监听地址和手机可访问的局域网 URL 打到日志。
/// </summary>
/// <remarks>
/// 默认绑 <c>0.0.0.0</c> 之后，操作员最常问的就是「手机打开哪个地址」。
/// 启动时直接写出来，避免再去跑 ipconfig。
/// </remarks>
static void LogListenAddresses (WebApplication app, int listenPort, int grpcPort) {
    var logger = app.Services.GetRequiredService<ILoggerFactory>().CreateLogger("Web.Endpoint");
    ICollection<string>? addresses = app.Services
        .GetService<IServer>()?
        .Features.Get<IServerAddressesFeature>()?
        .Addresses;

    if (addresses is null || addresses.Count == 0) {
        logger.LogWarning("无法获取实际监听地址。");
        return;
    }

    foreach (string address in addresses)
        logger.LogInformation("Web 监听 {Address}", address);

    logger.LogInformation("gRPC 监听 0.0.0.0:{Port}（WPF / 本机 HostingClient 连 127.0.0.1:{Port}）",
        grpcPort, grpcPort);

    int port = listenPort;
    foreach (string ip in LanAccess.EnumerateIPv4())
        logger.LogInformation("同网段设备访问：http://{Address}:{Port}", ip, port);
}

/// <summary>
/// 独立 Hosting.App.exe 已经占着引擎时的提示。
/// </summary>
static void ReportHostingAlreadyRunning () {
    try {
        _ = MessageBoxW(
            IntPtr.Zero,
            "Hosting.App 已经在运行。\n\n" +
            "Web 上位机把宿主带在本进程里，不能再开一份独立 exe。\n" +
            "请先结束 CommunicationKernel.Hosting.App，再打开本程序。\n" +
            "树莓派现场网关才需要单独跑 Hosting.App。",
            "CommunicationKernel Web",
            0x00000040 | 0x00040000);
    } catch {
    }
}

/// <summary>
/// 已有实例在跑：唤出它的界面，本进程直接退出。
/// </summary>
static void NotifyRunningInstance () {
    try {
        using EventWaitHandle show = EventWaitHandle.OpenExisting(TrayHost.ShowEventName);
        show.Set();
        return;
    } catch {
        // 事件还没建好或没有权限：退回到弹框
    }

    try {
        _ = MessageBoxW(
            IntPtr.Zero,
            "CommunicationKernel Web 上位机已经在运行。\n请看右下角托盘图标，右键可打开界面或退出。",
            "CommunicationKernel Web",
            0x00000040 | 0x00040000);
    } catch {
        // 无桌面会话
    }
}

// ============================================================================
// 启动失败的可见化
// ============================================================================

/// <summary>
/// 把启动失败写进日志文件，并在 Windows 上弹一个消息框。
/// </summary>
/// <remarks>
/// 本方法自身绝不能抛异常——它是最后一道出口，
/// 在这里再抛一次会让进程带着一个更没头绪的异常退出。
/// </remarks>
static void ReportStartupFailure (Exception ex) {
    string extra = string.Empty;
    if (ex.Message.Contains(":5000", StringComparison.Ordinal)
        || ex.Message.Contains("address already in use", StringComparison.OrdinalIgnoreCase)
        || ex.Message.Contains("Address already in use", StringComparison.OrdinalIgnoreCase)) {
        extra =
            "端口被占用。本程序同时听 Web 口（默认 64000）和 gRPC 口（默认 5000）。\n" +
            "关掉独立的 Hosting.App.exe，或结束上一份没退干净的 WebMaster。\n\n";
    }

    string message =
        "CommunicationKernel Web 上位机启动失败。\n\n" +
        extra +
        ex.Message + "\n\n" +
        "常见原因：\n" +
        "· 端口被占用——上一次没退干净，或独立 Hosting.App.exe 还在听 5000\n" +
        "· appsettings.json 语法错误\n\n" +
        "详细信息见日志文件。";

    // 先落盘：弹框可能因为会话隔离等原因显示不出来，日志是更可靠的那一条路
    string logPath = string.Empty;
    try {
        logPath = Path.Combine(AppContext.BaseDirectory, "startup-error.log");
        File.WriteAllText(
            logPath,
            DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss") + Environment.NewLine +
            ex + Environment.NewLine);
    } catch {
        // 目录只读或磁盘满：还有弹框这条路，不因写日志失败而中断
    }

    try {
        if (OperatingSystem.IsWindows()) {
            // 直接 P/Invoke user32，避免启动失败路径再依赖 WinForms 消息循环
            // 0x00000010 = MB_ICONERROR，0x00040000 = MB_TOPMOST（保证不被浏览器盖住）
            _ = MessageBoxW(
                IntPtr.Zero,
                message + (logPath.Length == 0 ? string.Empty : "\n" + logPath),
                "CommunicationKernel 启动失败",
                0x00000010 | 0x00040000);
        } else {
            Console.Error.WriteLine(message);
            Console.Error.WriteLine(ex);
        }
    } catch {
        // 无桌面会话（服务模式）时弹框会失败，此时日志文件已经写好了
    }
}

/// <summary>Win32 消息框，仅用于启动失败 / 重复启动提示。</summary>
[DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
static extern int MessageBoxW (IntPtr hWnd, string text, string caption, uint type);

/// <summary>本进程自己的控制台窗口句柄（Windows Terminal / conhost）。</summary>
[DllImport("kernel32.dll")]
static extern IntPtr GetConsoleWindow ();

/// <summary>拆掉本进程的控制台窗口。</summary>
[DllImport("kernel32.dll")]
static extern bool FreeConsole ();

[DllImport("user32.dll")]
static extern bool ShowWindow (IntPtr hWnd, int nCmdShow);

/// <summary>
/// 隐藏本进程挂着的控制台。WinExe 双击本来没有；
/// 若仍被 VS / 旧 Web SDK 塞了一个黑框，先藏再拆。
/// </summary>
static void HideAttachedConsole () {
    if (!OperatingSystem.IsWindows())
        return;
    try {
        IntPtr hwnd = GetConsoleWindow();
        if (hwnd != IntPtr.Zero)
            ShowWindow(hwnd, 0);
        FreeConsole();
    } catch {
        // 没有控制台时失败是正常的
    }
}
