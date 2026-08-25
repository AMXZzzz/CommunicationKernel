// -----------------------------------------------------------------------------
// 文件: Program.cs
// 层级: UI 层 — Blazor Server 应用程序入口
// 作用: 注册会话、日志、设备/变量持久化，配置中间件与组件路由。
// -----------------------------------------------------------------------------

using CommunicationKernel.UI.Web.Components;
using CommunicationKernel.UI.Web.Services;
using Microsoft.AspNetCore.Components.Server;

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

// 阻塞监听，直到进程收到停止信号
app.Run();
