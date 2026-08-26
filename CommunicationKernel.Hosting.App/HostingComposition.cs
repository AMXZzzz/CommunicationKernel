using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using CommunicationKernel.Core.EngineRouter;
using CommunicationKernel.Core.EngineRouter.Abstractions;
using CommunicationKernel.Core.EngineRuntime;
using CommunicationKernel.Hosting.App.Services;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Server.Kestrel.Core;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

// -----------------------------------------------------------------------------
// 文件: HostingComposition.cs
// 层级: Hosting.App / 组合根
// 作用: 把引擎 + gRPC 的装配抽成一份，Hosting.App.exe 与 UI.WebMaster 共用。
//
// WebMaster 把本组合根带进自己的进程：UI 仍走 Hosting.Sdk（HostingClient），
// 连的是本进程 :5000 的 gRPC。以后要拆成独立宿主，UI 代码不用改，
// 只把 HostingClient 的地址从 127.0.0.1 换成现场机器。
// -----------------------------------------------------------------------------

namespace CommunicationKernel.Hosting.App;

/// <summary>引擎 + gRPC 的共享装配。两份宿主入口都只调这里，禁止各写一套 DI。</summary>
public static class HostingComposition
{
    /// <summary>进程互斥量。WebMaster 内嵌宿主时也要拿，避免再开一份 Hosting.App.exe。</summary>
    public const string InstanceMutexName = @"Local\CommunicationKernel.Hosting.App";

    /// <summary>gRPC 默认端口。WPF 出厂地址与此一致。</summary>
    public const int DefaultGrpcPort = 5000;

    /// <summary>注册路由装配、引擎、gRPC。调用方再 MapEndpoints。</summary>
    public static void AddServices(IServiceCollection services, IConfiguration configuration)
    {
        services.AddGrpc();

        services.AddSingleton<IRouteAssemblyService>(sp =>
        {
            string pluginDirectorySetting = configuration["EngineRuntime:PluginDirectory"] ?? "plugins";
            int defaultSerialIntervalMs = int.TryParse(
                    configuration["EngineRuntime:DefaultSerialMinIoIntervalMs"], out int value)
                ? value
                : 15;
            string resolvedPluginDirectory = Path.IsPathRooted(pluginDirectorySetting)
                ? pluginDirectorySetting
                : Path.Combine(AppContext.BaseDirectory, pluginDirectorySetting);
            return new PluginRouteAssemblyService(
                pluginDirectory: resolvedPluginDirectory,
                defaultSerialMinIoIntervalMs: defaultSerialIntervalMs,
                loggerFactory: sp.GetRequiredService<ILoggerFactory>());
        });

        services.AddSingleton<IConnectionRouter>(sp =>
            new ConnectionRouter(sp.GetService<ILogger<ConnectionRouter>>()));
        services.AddSingleton<IReadCoordinator, ReadCoordinator>();
        services.AddSingleton<IRouterOrchestrator, RouterOrchestrator>();
        services.AddSingleton<EngineRuntime>();
    }

    /// <summary>把 gRPC 口绑成明文 HTTP/2。不能 Http1AndHttp2，否则 gRPC 全被拒。</summary>
    public static void ListenGrpc(ListenOptions options) =>
        options.Protocols = HttpProtocols.Http2;

    /// <summary>映射 HostingApi。WebMaster 的 / 仍归 Blazor，不要在这里 MapGet。</summary>
    public static void MapEndpoints(WebApplication app) =>
        app.MapGrpcService<HostingGrpcService>();

    /// <summary>
    /// 立刻构造引擎并预热插件。返回已加载的协议 ID。
    /// 不预热的话，插件要等到第一笔 gRPC 才加载，无人值守现场会「服务在跑、一个协议都没有」。
    /// </summary>
    public static IReadOnlyList<string> Warmup(WebApplication app)
    {
        _ = app.Services.GetRequiredService<EngineRuntime>();
        var logger = app.Services.GetRequiredService<ILoggerFactory>().CreateLogger("Hosting.App.Startup");
        var assemblyService = app.Services.GetRequiredService<IRouteAssemblyService>();
        var protocols = assemblyService.GetAvailableProtocols();
        if (protocols.Count == 0)
        {
            logger.LogError(
                "未加载到任何协议插件。请检查插件目录是否存在、其中是否有插件 DLL，" +
                "以及共享契约是否误被复制进插件目录（那会让所有工厂静默注册不上）。");
            return Array.Empty<string>();
        }

        string[] ids = protocols.Select(p => p.ProtocolId).ToArray();
        logger.LogInformation("已加载 {Count} 个协议：{Protocols}",
            ids.Length, string.Join(", ", ids));
        return ids;
    }
}
