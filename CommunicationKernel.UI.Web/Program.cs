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
using CommunicationKernel.UI.Web.Components;
using CommunicationKernel.UI.Web.Services;
using Microsoft.AspNetCore.Components.Server;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;

try
{

// 读取 appsettings / 环境变量，准备 DI 与配置
WebApplicationBuilder builder = WebApplication.CreateBuilder(args);

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

// 监听就绪后再开浏览器。
//
// 必须挂在 ApplicationStarted 上而不是 Run 之前：端口尚未绑定时打开浏览器，
// 用户看到的是"无法访问此网站"，然后手动刷新才好——那比不开还差。
//
// 地址从 IServerAddressesFeature 取实际绑定值，不写死端口：
// appsettings 或命令行随时可能改端口，写死会打开一个空白页。
if (builder.Configuration.GetValue("Web:LaunchBrowser", defaultValue: true))
{
    app.Lifetime.ApplicationStarted.Register(() => OpenBrowser(app));
}

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
    string message =
        "CommunicationKernel Web 上位机启动失败。\n\n" +
        ex.Message + "\n\n" +
        "常见原因：\n" +
        "· 端口被占用——上一次没退干净，或别的程序占了同一端口\n" +
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
