using System;
using System.IO;
using CommunicationKernel.Engine.Router;
using CommunicationKernel.Engine.Router.Abstractions;
using CommunicationKernel.EngineHost.Host;
using CommunicationKernel.EngineHost.Services;
using Microsoft.Extensions.Logging;

// -----------------------------------------------------------------------------
// 文件: AppMain.cs
// 层级: EngineHost / Entry
// 作用: 组合根 —— 装配依赖、映射 gRPC 端点、启动宿主。
// 说明:
// 1) Host 作为多 UI 统一中枢入口，仅暴露服务，不承载 UI 逻辑。
// 2) 这里是唯一知晓具体实现类型的位置：其余各层一律只依赖接口。
// 3) 并发控制在 Router 层与 RouteEntry 门控，本文件不涉及。
// 4) 监听地址固定为 http://localhost:5000，与 WPF 客户端默认地址一致；
//    Protocols 取 Http1AndHttp2 —— 纯 Http2 会让根路由的浏览器引导页不可访问。
// -----------------------------------------------------------------------------

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
    //! 串口最小 I/O 间隔（帧间静默窗口），单位毫秒
    int defaultSerialIntervalMs = int.TryParse(builder.Configuration["HostRuntime:DefaultSerialMinIoIntervalMs"], out int value)
        ? value
        : 15;

    // 分支1：相对路径按宿主基目录解析，避免不同启动目录导致插件目录漂移。
    string resolvedPluginDirectory = Path.IsPathRooted(pluginDirectorySetting)
        ? pluginDirectorySetting
        : Path.Combine(AppContext.BaseDirectory, pluginDirectorySetting);

    //! 日志记录：输出插件目录与默认间隔配置
    var loggerFactory = sp.GetRequiredService<ILoggerFactory>();

    //! 日志记录：输出插件目录与默认间隔配置
    return new PluginRouteAssemblyService(
        pluginDirectory: resolvedPluginDirectory,
        defaultSerialMinIoIntervalMs: defaultSerialIntervalMs,
        loggerFactory: loggerFactory);
});

// 组合根：路由引擎的子组件面向接口注册，可整体或逐个替换实现。
builder.Services.AddSingleton<IConnectionRouter>(sp =>
    new ConnectionRouter(sp.GetService<ILogger<ConnectionRouter>>()));
builder.Services.AddSingleton<IReadCoordinator, ReadCoordinator>();
builder.Services.AddSingleton<IRouterOrchestrator, RouterOrchestrator>();

// 组合根：注册 HostRuntime，仅依赖抽象服务与编排器。
builder.Services.AddSingleton<HostRuntime>();

//! 创建应用程序
var app = builder.Build();

// 映射 gRPC 服务端点（首批 Health + Diagnostics）。
app.MapGrpcService<EngineHostGrpcService>();

// 辅助根路由：便于浏览器直连时看到引导信息。
app.MapGet("/", () => "CommunicationKernel.EngineHost is running. Use a gRPC client to call endpoints./ [引导文]: CommunicationKernel.EngineHost 服务端初始化Done ");

//! 阻塞运行，直至进程收到停止信号
app.Run();
