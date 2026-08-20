using System;
using System.IO;
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

// 创建Web 服务端
var builder = WebApplication.CreateBuilder(args);

// 注册 gRPC 基础设施（HTTP/2 + 高性能二进制协议）。
builder.Services.AddGrpc(options =>
{
    // 预留服务级拦截器/限流等策略扩展点；当前保持默认最小配置。
});

// 组合根：注册路由装配服务，隔离 HostRuntime 与具体协议/传输装配实现。
builder.Services.AddSingleton<IRouteAssemblyService>(sp =>
{
    // 读取Runtime配置
    string pluginDirectorySetting = builder.Configuration["HostRuntime:PluginDirectory"] ?? "plugins";
    //! 解析配置超时时间
    int defaultSerialIntervalMs = int.TryParse(builder.Configuration["HostRuntime:DefaultSerialMinIoIntervalMs"], out int value)
        ? value
        : 15;

    // 分支1：相对路径按宿主基目录解析，避免不同启动目录导致插件目录漂移。
    string resolvedPluginDirectory = Path.IsPathRooted(pluginDirectorySetting)
        ? pluginDirectorySetting
        : Path.Combine(AppContext.BaseDirectory, pluginDirectorySetting);

    //! 返回插件管理复位
    var loggerFactory = sp.GetRequiredService<ILoggerFactory>();
    return new PluginRouteAssemblyService(
        pluginDirectory: resolvedPluginDirectory,
        defaultSerialMinIoIntervalMs: defaultSerialIntervalMs,
        loggerFactory: loggerFactory);
});

// 组合根：注册 HostRuntime，仅依赖抽象服务与编排器。
builder.Services.AddSingleton<HostRuntime>();

//! 创建应用程序
var app = builder.Build();

// 映射 gRPC 服务端点（首批 Health + Diagnostics）。
app.MapGrpcService<EngineHostGrpcService>();

// 辅助根路由：便于浏览器直连时看到引导信息。
app.MapGet("/", () => "CommunicationKernel.EngineHost is running. Use a gRPC client to call endpoints./ [引导文]: CommunicationKernel.EngineHost 服务端初始化Done ");

//! 消息循环
app.Run();
