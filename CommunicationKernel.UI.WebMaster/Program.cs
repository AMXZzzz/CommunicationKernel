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
// 浏览器由谁打开:
//   由本文件的 OpenBrowser 负责，受配置项 Web:LaunchBrowser 控制（默认开）。
//   launchSettings.json 里的 launchBrowser 已相应改为 false——
//   两边都开会在 `dotnet run` 时弹出两个标签页。
//   launchSettings.json 是严格 JSON，**不能写注释**（写了会让整个 profile
//   静默失效，dotnet run 直接忽略其中的所有设置），所以这条说明只能放在这里。
// -----------------------------------------------------------------------------

using System.Diagnostics;
using System.Runtime.InteropServices;
using CommunicationKernel.UI.WebMaster.Components;
using CommunicationKernel.UI.WebMaster.Services;
using Microsoft.AspNetCore.Components.Server;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;

try
{

// EngineHost.App 固定听 :5000。ASP.NET Core 在没写监听地址时也默认 :5000，
// 而且 Visual Studio / 上次跑宿主时可能把 ASPNETCORE_URLS 留在环境里。
string? inheritedUrls = Environment.GetEnvironmentVariable("ASPNETCORE_URLS");
if (!string.IsNullOrWhiteSpace(inheritedUrls) && ContainsPort(inheritedUrls, WebSettingsStore.HostPort))
    Environment.SetEnvironmentVariable("ASPNETCORE_URLS", null);

// 读取 appsettings / 环境变量，准备 DI 与配置
WebApplicationBuilder builder = WebApplication.CreateBuilder(args);

// 端口来源：设置页 web-listen.json → 命令行 --urls → appsettings Web:ListenPort → 64000。
// 在代码里 UseUrls，不再靠 appsettings 的 Kestrel:Endpoints（那段会把这里的设置压掉）。
int listenPort = WebSettingsStore.ResolveListenPort(builder.Configuration, inheritedUrls);
builder.WebHost.UseUrls("http://0.0.0.0:" + listenPort);

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
builder.Services.Configure<CircuitOptions>(options =>
{
    options.DisconnectedCircuitRetentionPeriod = TimeSpan.FromMinutes(3);
    options.DetailedErrors = builder.Environment.IsDevelopment();
});

// 本地持久化（地址 / 设备 / 变量）
builder.Services.AddSingleton<WebSettingsStore>();
builder.Services.AddSingleton<WebDeviceStore>();
builder.Services.AddSingleton<WebVariableStore>();

// 会话：单例即 HostedService，避免两份 HostClient
builder.Services.AddSingleton<HostSession>();
builder.Services.AddHostedService(sp => sp.GetRequiredService<HostSession>());

// 设备操作：页面唯一的设备入口，页面不得再直接持有 HostClient 发 gRPC。
// 对应 WPF 端的 IDeviceService / GrpcDeviceService。
builder.Services.AddSingleton<IWebDeviceService, WebDeviceService>();

// 变量读写：页面与后台轮询器共用，保证字节序处理不再分叉。
builder.Services.AddSingleton<IWebVariableService, WebVariableService>();

// 变量轮询：Host 离线时跳过
builder.Services.AddHostedService<VariablePoller>();

// ============================================================================
// 构建应用
// ============================================================================

WebApplication app = builder.Build();

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
app.Lifetime.ApplicationStarted.Register(() =>
{
    LogListenAddresses(app);
    if (builder.Configuration.GetValue("Web:LaunchBrowser", defaultValue: true))
        OpenBrowser(app);
});

// 阻塞监听，直到进程收到停止信号
app.Run();

}
catch (Exception ex)
{
    // 无控制台时这是唯一的错误出口。最常见的两种：
    //   端口被占（上一次没退干净，或别的程序占了 5000/64000）
    //   appsettings.json 语法错误（改配置时漏了逗号或引号）
    ReportStartupFailure(ex);

    // 非 0 退出码便于脚本与看门狗识别启动失败
    Environment.ExitCode = 1;
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
static void LogListenAddresses(WebApplication app)
{
    var logger = app.Services.GetRequiredService<ILoggerFactory>().CreateLogger("Web.Endpoint");
    ICollection<string>? addresses = app.Services
        .GetService<IServer>()?
        .Features.Get<IServerAddressesFeature>()?
        .Addresses;

    if (addresses is null || addresses.Count == 0)
    {
        logger.LogWarning("无法获取实际监听地址。");
        return;
    }

    foreach (string address in addresses)
        logger.LogInformation("Web 监听 {Address}", address);

    int port = TryGetListenPort(addresses) ?? LanAccess.DefaultPort;
    foreach (string ip in LanAccess.EnumerateIPv4())
        logger.LogInformation("同网段设备访问：http://{Address}:{Port}", ip, port);
}

/// <summary>从绑定地址里取出端口；取不到返回 null。</summary>
static int? TryGetListenPort(IEnumerable<string> addresses)
{
    foreach (string address in addresses)
    {
        if (Uri.TryCreate(address.Replace("://0.0.0.0", "://127.0.0.1", StringComparison.OrdinalIgnoreCase)
                                 .Replace("://[::]", "://127.0.0.1", StringComparison.OrdinalIgnoreCase),
                          UriKind.Absolute, out Uri? uri)
            && uri.Port > 0)
            return uri.Port;
    }
    return null;
}

/// <summary>
/// 用系统默认浏览器打开本应用的地址。
/// </summary>
/// <remarks>
/// <para>
/// 优先取 http 地址：Blazor Server 的开发证书在很多机器上没被信任，
/// 默认开 https 会先撞上一个"连接不专用"警告页，操作员多半会直接关掉。
/// </para>
/// <para>
/// 全程吞异常。打不开浏览器只是少了个便利——服务本身已经起来了，
/// 用户手动输地址一样能用；为此让进程崩掉毫无道理。
/// </para>
/// </remarks>
static void OpenBrowser(WebApplication app)
{
    try
    {
        // 取实际绑定的地址集合，避免与配置里的端口不一致
        ICollection<string>? addresses = app.Services
            .GetService<IServer>()?
            .Features.Get<IServerAddressesFeature>()?
            .Addresses;

        if (addresses is null || addresses.Count == 0)
            return;

        // http 优先，没有再回落到第一个（通常是 https）
        string url = addresses.FirstOrDefault(
            a => a.StartsWith("http://", StringComparison.OrdinalIgnoreCase))
            ?? addresses.First();

        // 绑定在 0.0.0.0 / [::] 上时那不是可访问地址，换成回环
        url = url.Replace("://0.0.0.0", "://localhost", StringComparison.OrdinalIgnoreCase)
                 .Replace("://[::]", "://localhost", StringComparison.OrdinalIgnoreCase);

        // 5000 是 EngineHost.App。万一仍绑到了那里，绝不把浏览器带到 gRPC 口上
        if (ContainsPort(url, WebSettingsStore.HostPort))
            url = "http://localhost:" + LanAccess.DefaultPort;

        // UseShellExecute 是关键：不设它 .NET 会把 URL 当可执行文件去启动而失败
        Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
    }
    catch
    {
        // 无桌面会话（服务模式、树莓派无头运行）时必然失败，属正常情况
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
static void ReportStartupFailure(Exception ex)
{
    string extra = string.Empty;
    if (ex.Message.Contains(":5000", StringComparison.Ordinal)
        || ex.Message.Contains("address already in use", StringComparison.OrdinalIgnoreCase)
        || ex.Message.Contains("Address already in use", StringComparison.OrdinalIgnoreCase))
    {
        extra =
            "端口 5000 是 EngineHost.App 的，Web 上位机应使用 64000。\n" +
            "请确认 EngineHost.App 已单独在跑，然后重新启动本程序（不要用 --urls 指向 5000）。\n\n";
    }

    string message =
        "CommunicationKernel Web 上位机启动失败。\n\n" +
        extra +
        ex.Message + "\n\n" +
        "常见原因：\n" +
        "· 端口被占用——上一次没退干净，或误绑了 EngineHost.App 的 5000 端口\n" +
        "· appsettings.json 语法错误\n\n" +
        "详细信息见日志文件。";

    // 先落盘：弹框可能因为会话隔离等原因显示不出来，日志是更可靠的那一条路
    string logPath = string.Empty;
    try
    {
        logPath = Path.Combine(AppContext.BaseDirectory, "startup-error.log");
        File.WriteAllText(
            logPath,
            DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss") + Environment.NewLine +
            ex + Environment.NewLine);
    }
    catch
    {
        // 目录只读或磁盘满：还有弹框这条路，不因写日志失败而中断
    }

    try
    {
        if (OperatingSystem.IsWindows())
        {
            // 直接 P/Invoke user32，避免为一个消息框把 WinForms 拖进 Web 工程
            // 0x00000010 = MB_ICONERROR，0x00040000 = MB_TOPMOST（保证不被浏览器盖住）
            _ = MessageBoxW(
                IntPtr.Zero,
                message + (logPath.Length > 0 ? "\n" + logPath : string.Empty),
                "CommunicationKernel 启动失败",
                0x00000010 | 0x00040000);
        }
        else
        {
            // 非 Windows（树莓派等）多为无人值守，标准错误进 journald 即可
            Console.Error.WriteLine(message);
            Console.Error.WriteLine(ex);
        }
    }
    catch
    {
        // 无桌面会话（服务模式）时弹框会失败，此时日志文件已经写好了
    }
}

/// <summary>Win32 消息框，仅用于启动失败提示。</summary>
[DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
static extern int MessageBoxW(IntPtr hWnd, string text, string caption, uint type);

/// <summary>
/// 判断 URL 列表里是否含指定端口，避免把 :5000 误匹配成 :50000。
/// </summary>
static bool ContainsPort(string urls, int port)
{
    string token = ":" + port.ToString(System.Globalization.CultureInfo.InvariantCulture);
    int start = 0;
    while (start < urls.Length)
    {
        int i = urls.IndexOf(token, start, StringComparison.Ordinal);
        if (i < 0)
            return false;
        int after = i + token.Length;
        if (after >= urls.Length || !char.IsDigit(urls[after]))
            return true;
        start = after;
    }
    return false;
}
