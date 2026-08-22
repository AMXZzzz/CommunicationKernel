using System;
using System.IO;
using System.Linq;
using CommunicationKernel.Engine.Router;
using CommunicationKernel.Engine.Router.Abstractions;
using CommunicationKernel.Engine;
using CommunicationKernel.Engine.Models;
using CommunicationKernel.EngineHost.Services;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.Logging;

// -----------------------------------------------------------------------------
// 文件: AppMain.cs
// 层级: EngineHost / Entry
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
//    代价是根路由的引导页浏览器打不开（浏览器不做明文 h2c），
//    但存活检查本就该用 systemctl / journalctl，不值得为此牺牲 gRPC。
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

// -----------------------------------------------------------------------------
// 监听地址的暴露面告警
// -----------------------------------------------------------------------------
// 本 gRPC 端点没有任何认证与授权：能建立连接就能注册路由、读写 PLC 寄存器。
// 绑定到非回环地址意味着同一网段的任何主机都可以直接操作现场设备。
//
// 跨机部署（宿主在树莓派、上位机在别的机器）确实需要这么做，所以不禁止；
// 但必须让运维在启动日志里明确看到自己打开了什么，而不是默认静默放行。
// 隔离手段由部署方负责：防火墙白名单、独立 VLAN，或前置一层带认证的反向代理。
//
// 在 ApplicationStarted 里读 IServerAddressesFeature 而非读配置：
// 命令行 --urls 与环境变量 ASPNETCORE_URLS 会覆盖 appsettings，
// 只有服务器实际绑定完成后拿到的地址才是真正生效的那一份。
app.Lifetime.ApplicationStarted.Register(() =>
{
    var logger = app.Services.GetRequiredService<ILoggerFactory>().CreateLogger("EngineHost.Endpoint");

    var addresses = app.Services.GetRequiredService<IServer>()
        .Features.Get<IServerAddressesFeature>()?.Addresses;

    // 分支1：拿不到地址特性（理论上只发生在被替换的服务器实现上），跳过告警。
    if (addresses is null || addresses.Count == 0)
    {
        logger.LogWarning("无法获取实际监听地址，跳过暴露面检查。");
        return;
    }

    foreach (string address in addresses)
    {
        // 分支2：回环地址——仅本机可达，无需告警。
        bool isLoopback =
            address.Contains("localhost", StringComparison.OrdinalIgnoreCase) ||
            address.Contains("127.0.0.1", StringComparison.Ordinal) ||
            address.Contains("[::1]", StringComparison.Ordinal);

        if (isLoopback)
        {
            logger.LogInformation("gRPC 监听 {Address}（仅本机可达）。", address);
            continue;
        }

        // 分支3：非回环——同网段可达，且服务端无认证。
        logger.LogWarning(
            "gRPC 监听 {Address}：该地址可被同网段主机访问，而本服务不做任何认证——" +
            "连上即可读写 PLC。请确认已通过防火墙、VLAN 或带认证的反向代理限制访问范围。",
            address);
    }
});

// -----------------------------------------------------------------------------
// 插件预热
// -----------------------------------------------------------------------------
// IRouteAssemblyService 是懒构造的单例：不主动解析，插件要等到第一次 gRPC 调用
// 才加载。而插件加载失败是静默的（共享契约泄漏、目录缺失、工厂实例化异常都只记日志），
// 于是无人值守部署会出现「服务好好跑着、直到有人操作才发现一个协议都没有」。
// 在这里提前解析，把协议清单写进启动日志——树莓派上只有 journalctl 可看。
{
    var logger = app.Services.GetRequiredService<ILoggerFactory>().CreateLogger("EngineHost.Startup");
    var assemblyService = app.Services.GetRequiredService<IRouteAssemblyService>();
    var protocols = assemblyService.GetAvailableProtocols();

    // 分支1：一个协议都没加载到——服务在这种状态下无法完成任何实际工作。
    if (protocols.Count == 0)
    {
        logger.LogError(
            "未加载到任何协议插件。请检查插件目录是否存在、其中是否有插件 DLL，" +
            "以及共享契约是否误被复制进插件目录（那会让所有工厂静默注册不上）。");
    }
    else
    {
        logger.LogInformation("已加载 {Count} 个协议：{Protocols}",
            protocols.Count, string.Join(", ", protocols.Select(p => p.ProtocolId)));
    }
}

//! 阻塞运行，直至进程收到停止信号
app.Run();
