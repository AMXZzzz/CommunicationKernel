using CommunicationKernel.EngineHost.Host;
using CommunicationKernel.EngineHost.Services;

/// <summary>
/// -----------------------------------------------------------------------------
/// 文件: Program.cs
/// 层级: EngineHost / Entry
/// 作用: 启动高性能 gRPC Host，并注册首批服务端点。
/// 说明:
/// 1) Host 作为多 UI 统一中枢入口，仅暴露服务，不承载 UI 逻辑。
/// 2) 路由并发控制仍由 Router 层负责，Program 仅做装配与端点映射。
/// -----------------------------------------------------------------------------
/// </summary>
var builder = WebApplication.CreateBuilder(args);

// 注册 gRPC 基础设施（HTTP/2 + 高性能二进制协议）。
builder.Services.AddGrpc(options => {
    // 预留服务级拦截器/限流等策略扩展点；当前保持默认最小配置。
});

// 组合根：注册 HostRuntime，内部持有 Facade 与 Router 编排能力。
builder.Services.AddSingleton<HostRuntime>();

var app = builder.Build();

// 映射 gRPC 服务端点（首批 Health + Diagnostics）。
app.MapGrpcService<EngineHostGrpcService>();

// 辅助根路由：便于浏览器直连时看到引导信息。
app.MapGet("/", () => "CommunicationKernel.EngineHost is running. Use a gRPC client to call endpoints.");

app.Run();
