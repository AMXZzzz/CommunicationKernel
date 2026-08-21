// -----------------------------------------------------------------------------
// 文件: App.xaml.cs
// 层级: UI 层 — WPF 应用程序启动入口（组合根 Composition Root）
// 作用: 构建 IHost + DI 容器，注册所有服务、ViewModel、Page；
//       从 settings.json 读取 EngineHost 地址；
//       启动主机后预加载设备列表并显示主窗口。
// 启动顺序:
//   Application_Startup
//     → LoadSavedHostAddress() 读取持久化地址
//     → IHostBuilder.Build()
//       → DI 注册: EngineHostGrpcClient（使用持久化地址）
//       → DI 注册: IDeviceService / IVariableService / IProtocolResolver / IAppLogger
//       → DI 注册: ViewModels（Device / Log / Variable / DataMonitor / Settings）
//       → DI 注册: Pages（DataMonitor / Device / Variable / Log / Settings）
//       → DI 注册: MainWindow（单例）
//     → host.StartAsync()
//     → IDeviceService.Load() 后台预加载路由列表
//     → 显示 MainWindow
// -----------------------------------------------------------------------------

using System;
using System.IO;
using System.Text.Json;
using System.Windows;
using CommunicationKernel.UI.Wpf.Core.Interfaces;
using CommunicationKernel.UI.Wpf.Core.Logging;
using CommunicationKernel.UI.Wpf.Services;
using CommunicationKernel.UI.Wpf.ViewModels;
using CommunicationKernel.UI.Wpf.Views.Pages.Device;
using CommunicationKernel.UI.Wpf.Views.Pages.Log;
using CommunicationKernel.UI.Wpf.Views.Pages.MesMonitor;
using CommunicationKernel.UI.Wpf.Views.Pages.Settings;
using CommunicationKernel.UI.Wpf.Views.Pages.Variable;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace CommunicationKernel.UI.Wpf;

/// <summary>
/// WPF 应用程序类，持有 IHost 生命周期。
/// 唯一知晓所有具体类型的地方（组合根）。
/// </summary>
public partial class App : Application {

    // -------------------------------------------------------------------------
    // 私有字段
    // -------------------------------------------------------------------------

    /// <summary>.NET 泛型主机，持有 DI 容器和生命周期。</summary>
    private IHost _host;

    // -------------------------------------------------------------------------
    // 启动
    // -------------------------------------------------------------------------

    /// <summary>
    /// App.xaml 中 Startup="Application_Startup" 绑定的启动方法。
    /// 不使用 StartupUri，避免 XAML 解析器绕开 DI 调用无参构造函数。
    /// </summary>
    private async void Application_Startup(object sender, StartupEventArgs e) {

        // 1. 构建主机（含 DI 容器）
        _host = Host.CreateDefaultBuilder()
            .ConfigureServices(ConfigureServices)
            .ConfigureLogging(logging => {
                // 保留控制台和调试输出（开发期有用）
                logging.AddConsole();
                logging.AddDebug();
                // 最低级别：Debug（捕获所有层的日志）
                logging.SetMinimumLevel(LogLevel.Debug);
            })
            .Build();

        // 2. 启动主机（初始化 Hosted Services）
        await _host.StartAsync().ConfigureAwait(true);

        // 3. 预加载设备列表：在后台触发 QueryRoutes，加载完成后自动刷新 DevicePage 和 DataMonitorPage
        _host.Services.GetRequiredService<IDeviceService>().Load();

        // 4. 启动变量轮询服务：订阅 IVariableService.VariablesChanged，
        //    对 IsPollingEnabled=true 的变量按 ScanRateMs 周期调用 ReadAsync
        _host.Services.GetRequiredService<VariablePollingService>().Start();

        // 5. 从 DI 取主窗口并显示
        MainWindow mainWindow = _host.Services.GetRequiredService<MainWindow>();
        mainWindow.Show();
    }

    // -------------------------------------------------------------------------
    // DI 服务注册
    // -------------------------------------------------------------------------

    /// <summary>向 DI 容器注册所有服务。单例生命周期贯穿整个应用。</summary>
    private static void ConfigureServices(IServiceCollection services) {

        // ── gRPC 客户端（单例）──────────────────────────────────────────
        // 从 settings.json 读取 HostAddress；文件不存在或解析失败时使用默认地址
        services.AddSingleton<EngineHostGrpcClient>(sp => {
            ILogger<EngineHostGrpcClient> logger =
                sp.GetRequiredService<ILogger<EngineHostGrpcClient>>();
            // 读取持久化地址，避免在 UI 层硬编码服务器地址
            string address = LoadSavedHostAddress();
            return new EngineHostGrpcClient(address, logger);
        });

        // ── 应用日志（单例）──────────────────────────────────────────────
        // MemoryAppLogger 维护内存中的日志缓冲，LogPage 订阅其 EntryAdded 事件
        services.AddSingleton<IAppLogger, MemoryAppLogger>();

        // ── 业务服务（单例）──────────────────────────────────────────────
        // GrpcDeviceService 封装 gRPC RegisterRoute / QueryRoutes / WatchRouteStatus / RemoveRoute
        services.AddSingleton<IDeviceService>(sp =>
            new GrpcDeviceService(
                sp.GetRequiredService<EngineHostGrpcClient>(),
                sp.GetRequiredService<IAppLogger>()));
        // LocalVariableStore 维护内存中的变量定义，Write 通过 gRPC 下发
        services.AddSingleton<IVariableService>(sp =>
            new LocalVariableStore(sp.GetRequiredService<EngineHostGrpcClient>()));
        // GrpcProtocolResolver 返回支持的协议名称列表
        services.AddSingleton<IProtocolResolver>(sp =>
            new GrpcProtocolResolver(sp.GetRequiredService<EngineHostGrpcClient>()));

        // ── 页面级 ViewModel（单例）──────────────────────────────────────
        // DevicePageViewModel：设备列表、增删改查、连接/断开
        services.AddSingleton<DevicePageViewModel>(sp =>
            new DevicePageViewModel(
                sp.GetRequiredService<IDeviceService>(),
                sp.GetRequiredService<IAppLogger>()));
        // LogPageViewModel：日志过滤与清空
        services.AddSingleton<LogPageViewModel>(sp =>
            new LogPageViewModel(sp.GetRequiredService<IAppLogger>()));
        // VariablePageViewModel：变量 CRUD 与协议写入
        services.AddSingleton<VariablePageViewModel>(sp =>
            new VariablePageViewModel(
                sp.GetRequiredService<IVariableService>(),
                sp.GetRequiredService<IDeviceService>(),
                sp.GetRequiredService<IAppLogger>()));
        // DataMonitorViewModel：从 IDeviceService.Devices 同步实时设备卡片数据
        services.AddSingleton<DataMonitorViewModel>(sp =>
            new DataMonitorViewModel(sp.GetRequiredService<IDeviceService>()));
        // SettingsViewModel：地址配置、连接测试、设置持久化
        services.AddSingleton<SettingsViewModel>(sp =>
            new SettingsViewModel(sp.GetRequiredService<EngineHostGrpcClient>()));

        // ── 页面（Transient：每次导航新建，避免 Frame 缓存问题）──────────
        // DataMonitorPage：注入 DataMonitorViewModel，设备卡片由 ItemsControl 动态生成
        services.AddTransient<DataMonitorPage>(sp =>
            new DataMonitorPage(sp.GetRequiredService<DataMonitorViewModel>()));
        // DevicePage：注入 DevicePageViewModel 和 IProtocolResolver（供 DeviceEditPanel 使用）
        services.AddTransient<DevicePage>(sp =>
            new DevicePage(
                sp.GetRequiredService<DevicePageViewModel>(),
                sp.GetRequiredService<IProtocolResolver>()));
        // VariableConfigPage：注入 VariablePageViewModel
        services.AddTransient<VariableConfigPage>(sp =>
            new VariableConfigPage(sp.GetRequiredService<VariablePageViewModel>()));
        // LogPage：注入 LogPageViewModel
        services.AddTransient<LogPage>(sp =>
            new LogPage(sp.GetRequiredService<LogPageViewModel>()));
        // SettingsPage：注入 SettingsViewModel
        services.AddTransient<SettingsPage>(sp =>
            new SettingsPage(sp.GetRequiredService<SettingsViewModel>()));

        // ── 变量轮询服务（单例）──────────────────────────────────────────
        // VariablePollingService 管理 IsPollingEnabled=true 变量的后台 ReadAsync 循环
        // 订阅 IVariableService.VariablesChanged，在变量增删改时自动重建轮询任务集合
        services.AddSingleton<VariablePollingService>(sp =>
            new VariablePollingService(
                sp.GetRequiredService<IVariableService>(),
                sp.GetRequiredService<EngineHostGrpcClient>()));

        // ── 主窗口（单例）────────────────────────────────────────────────
        // MainWindow 构造注入 IServiceProvider 用于懒解析各页面
        services.AddSingleton<MainWindow>();
    }

    // -------------------------------------------------------------------------
    // 辅助方法
    // -------------------------------------------------------------------------

    /// <summary>
    /// 从 AppData/CommunicationKernel/settings.json 读取已保存的 HostAddress。
    /// 文件不存在、解析失败或地址为空时，返回默认地址 "http://localhost:5000"。
    /// 与 SettingsViewModel 使用相同路径，保证读写一致。
    /// </summary>
    private static string LoadSavedHostAddress() {
        const string defaultAddress = "http://localhost:5000";
        try {
            // 与 SettingsViewModel 使用相同的文件路径
            string path = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                "CommunicationKernel", "settings.json");

            if (!File.Exists(path))
                return defaultAddress;

            // 读取 JSON，取 HostAddress 字段
            string json = File.ReadAllText(path);
            using JsonDocument doc = JsonDocument.Parse(json);
            if (doc.RootElement.TryGetProperty("HostAddress", out JsonElement el)) {
                string addr = el.GetString();
                // 非空时使用持久化的地址
                if (!string.IsNullOrWhiteSpace(addr))
                    return addr;
            }
        } catch {
            // 解析失败时静默回退默认值，不中断启动流程
        }
        return defaultAddress;
    }

    // -------------------------------------------------------------------------
    // 退出
    // -------------------------------------------------------------------------

    /// <summary>
    /// 应用退出：先停止变量轮询服务（取消所有 ReadAsync 循环），
    /// 再停止并异步释放 IHost（释放 gRPC 通道、DI 容器）。
    /// </summary>
    /// <remarks>
    /// 必须使用 <see cref="IAsyncDisposable.DisposeAsync"/> 而非同步 Dispose：
    /// EngineHostGrpcClient 只实现了 IAsyncDisposable，
    /// 对这类单例调用 ServiceProvider.Dispose() 会抛
    /// InvalidOperationException（"type only implements IAsyncDisposable"），
    /// 在 async void 中即表现为退出时的未处理异常崩溃。
    /// </remarks>
    protected override async void OnExit(ExitEventArgs e) {
        if (_host is not null) {
            // 先 Dispose 轮询服务，取消所有后台 ReadAsync 任务
            // 防止 gRPC 通道关闭后仍有任务尝试发起 RPC
            try {
                _host.Services.GetService<VariablePollingService>()?.Dispose();
            } catch {
                // Dispose 阶段静默处理异常，不阻塞退出流程
            }

            try {
                // 给 Hosted Services 最多 5 秒优雅停止时间
                await _host.StopAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false);

                // 优先异步释放，兼容仅实现 IAsyncDisposable 的单例服务
                if (_host is IAsyncDisposable asyncDisposable)
                    await asyncDisposable.DisposeAsync().ConfigureAwait(false);
                else
                    _host.Dispose();
            } catch {
                // 退出阶段的异常不应弹出崩溃对话框，静默吞掉
            }
        }
        base.OnExit(e);
    }
}
