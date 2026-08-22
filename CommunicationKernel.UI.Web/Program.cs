// -----------------------------------------------------------------------------
// 文件: Program.cs
// 层级: UI 层 — Blazor Server 应用程序入口
// 作用: 注册 gRPC 客户端、Blazor 组件，配置路由和中间件管道。
// 启动顺序:
//   WebApplication.CreateBuilder
//     → AddRazorComponents().AddInteractiveServerComponents()
//     → AddSingleton<HostClient>
//     → app.Run()
// -----------------------------------------------------------------------------

using CommunicationKernel.UI.Web.Components;
using CommunicationKernel.Host.Sdk;

WebApplicationBuilder builder = WebApplication.CreateBuilder(args);

// ============================================================================
// 服务注册
// ============================================================================

// Blazor Server 组件和交互支持
builder.Services.AddRazorComponents()
    .AddInteractiveServerComponents();

// gRPC Web 客户端（单例生命周期：整个应用共享一个 gRPC 连接池）
// gRPC 通道可安全被多个组件并发使用
builder.Services.AddSingleton<HostClient>(sp => {
    ILogger<HostClient> logger =
        sp.GetRequiredService<ILogger<HostClient>>();

    // 从配置读取 Host.App 地址（未配置时使用开发默认值）
    string address = builder.Configuration["Host.App:Address"] ?? "http://localhost:5000";

    return new HostClient(address, logger);
});

// ============================================================================
// 构建应用
// ============================================================================

WebApplication app = builder.Build();

// 生产环境：异常处理中间件（开发环境 Blazor 有内置错误 UI）
if (!app.Environment.IsDevelopment())
    app.UseExceptionHandler("/Error", createScopeForErrors: true);

// 静态文件（wwwroot 下的 CSS、图片等）
app.UseStaticFiles();

// 防 CSRF（Blazor 表单组件需要）
app.UseAntiforgery();

// Blazor 组件路由
app.MapRazorComponents<App>()
    .AddInteractiveServerRenderMode();

// 启动！
app.Run();
