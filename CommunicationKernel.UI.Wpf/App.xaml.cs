#nullable disable

// -----------------------------------------------------------------------------
// 文件: App.xaml.cs
// 层级: UI 层 — WPF 应用程序启动入口（组合根 Composition Root）
// 作用: 构建 IHost + DI 容器，注册所有服务、ViewModel、Page；
//       从 settings.json / 本项目 appsettings.json 读取 Host.App 地址；
//       启动主机后预加载设备列表并显示主窗口。
// 启动顺序:
//   Application_Startup
//     → WpfAppSettings.ReadAddress() 读 AppData / appsettings.json
//     → IHostBuilder.Build()
//       → DI 注册: HostClient（使用持久化地址）
//       → DI 注册: IDeviceService / IVariableService / IProtocolResolver / IAppLogger
//       → DI 注册: ViewModels（Device / Log / Variable / DataMonitor / Settings）
//       → DI 注册: Pages（DataMonitor / Device / Variable / Log / Settings）
//       → DI 注册: MainWindow（单例）
//     → host.StartAsync()
//     → IDeviceService.Load() 后台预加载路由列表
//     → 显示 MainWindow
// -----------------------------------------------------------------------------

using System;
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
using Microsoft.Extensions.Configuration;
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

    // =========================================================================
    // 启动
    // =========================================================================

    /// <summary>
    /// App.xaml 中 Startup="Application_Startup" 绑定的启动方法。
    /// 不使用 StartupUri，避免 XAML 解析器绕开 DI 调用无参构造函数。
    /// </summary>
    private async void Application_Startup(object sender, StartupEventArgs e) {
      try {
        // 0. 先挂全局异常兜底，确保启动阶段的异常也能被捕获并展示
        InstallGlobalExceptionHandlers();

        // 1. 构建主机（含 DI 容器）：注册服务、配置日志
        //
        // 必须写全 Microsoft.Extensions.Hosting.Host，不能只写 Host：
        // 本文件位于 CommunicationKernel.UI.Wpf 命名空间，编译器解析 Host
        // 时会先逐级向外找，于是命中 Host.Sdk / Host.App 引入的
        // CommunicationKernel.Host 命名空间，根本轮不到 using 里的那个类。
        // 工程改名成 Host.* 之后 WPF 就是因此编译不过的。
        _host = Microsoft.Extensions.Hosting.Host.CreateDefaultBuilder()
            .UseContentRoot(AppContext.BaseDirectory)
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

        // 3. 预加载设备列表：后台 QueryRoutes，完成后刷新 DevicePage / DataMonitorPage
        _host.Services.GetRequiredService<IDeviceService>().Load();

        // 4. 启动变量轮询：对 IsPollingEnabled=true 的变量按 ScanRateMs 周期 ReadAsync
        _host.Services.GetRequiredService<VariablePollingService>().Start();

        // 5. 启动 Host.App 健康轮询。
        //    刻意早于窗口创建：窗口构造时就能拿到已知状态，
        //    且轮询不再依附窗口生命周期（这正是它从 MainWindow 里搬出来的原因）。
        _host.Services.GetRequiredService<HostSessionService>().Start();

        // 6. 从 DI 取主窗口并显示（必须在 UI 线程）
        MainWindow mainWindow = _host.Services.GetRequiredService<MainWindow>();
        mainWindow.Show();
      } catch (Exception ex) {
        // Application_Startup 是 async void：首个 await 之后抛出的异常
        // 会被 post 回同步上下文成为未处理异常，直接闪退且不留任何提示。
        // 启动失败必须让用户看到原因（最常见的是 Host.App 地址不可达）。
        MessageBox.Show(
            "应用启动失败：\n\n" + ex.Message,
            "启动错误", MessageBoxButton.OK, MessageBoxImage.Error);
        // 非 0 退出码便于脚本/看门狗识别启动失败
        Shutdown(1);
      }
    }

    /// <summary>
    /// 安装全局未处理异常兜底。
    /// </summary>
    /// <remarks>
    /// 覆盖三个来源：UI 线程（<see cref="Application.DispatcherUnhandledException"/>）、
    /// 后台任务（<see cref="TaskScheduler.UnobservedTaskException"/>）、
    /// 以及其余 AppDomain 级异常。没有兜底时，后台轮询或状态流里的任何
    /// 意外异常都会让进程静默退出，现场无从排查。
    /// </remarks>
    private void InstallGlobalExceptionHandlers() {
        // UI 线程异常：弹框提示并标记已处理，避免单个界面异常拖垮整个进程
        DispatcherUnhandledException += (_, args) => {
            TryLog("UI", args.Exception);
            MessageBox.Show(
                "发生未处理的界面异常：\n\n" + args.Exception.Message,
                "错误", MessageBoxButton.OK, MessageBoxImage.Error);
            args.Handled = true;
        };

        // 后台 Task 未观测异常：标记已观测，防止进程因此终止
        TaskScheduler.UnobservedTaskException += (_, args) => {
            TryLog("Task", args.Exception);
            args.SetObserved();
        };

        // AppDomain 级致命异常：只能记日志，无法再恢复
        AppDomain.CurrentDomain.UnhandledException += (_, args) => {
            if (args.ExceptionObject is Exception ex)
                TryLog("AppDomain", ex);
        };
    }

    /// <summary>尽力把异常写入应用日志；日志器不可用时静默忽略。</summary>
    private void TryLog(string category, Exception ex) {
        try {
            // 启动早期 _host 可能尚未就绪，GetService 会返回 null
            _host?.Services.GetService<IAppLogger>()?.Error(category, "未处理异常: " + ex.Message, ex);
        } catch {
            // 兜底处理器自身绝不能再抛异常
        }
    }

    // =========================================================================
    // DI 服务注册
    // =========================================================================

    /// <summary>向 DI 容器注册所有服务。单例生命周期贯穿整个应用。</summary>
    private static void ConfigureServices(IServiceCollection services) {

        // =====================================================================
        // gRPC 客户端（单例）
        // =====================================================================

        // 从 AppData settings.json 读取 HostAddress；没有则用本项目 appsettings.json
        services.AddSingleton<HostClient>(sp => {
            ILogger<HostClient> logger =
                sp.GetRequiredService<ILogger<HostClient>>();
            string address = WpfAppSettings.ReadAddress(sp.GetRequiredService<IConfiguration>());
            return new HostClient(address, logger);
        });

        // =====================================================================
        // 应用日志（单例）
        // =====================================================================

        // MemoryAppLogger 维护内存中的日志缓冲，LogPage 订阅其 EntryAdded 事件
        services.AddSingleton<IAppLogger, MemoryAppLogger>();

        // =====================================================================
        // 业务服务（单例）
        // =====================================================================

        // 封装 gRPC RegisterRoute / QueryRoutes / WatchRouteStatus / RemoveRoute
        services.AddSingleton<IDeviceService>(sp =>
            new GrpcDeviceService(
                sp.GetRequiredService<HostClient>(),
                sp.GetRequiredService<IAppLogger>()));

        // 同一单例再按 IRouteReconciler 取出：两份实例会拆开在途表，合并与限流失效
        services.AddSingleton<IRouteReconciler>(sp =>
            (IRouteReconciler)sp.GetRequiredService<IDeviceService>());

        // 内存变量表；写入走 gRPC WriteAsync
        services.AddSingleton<IVariableService>(sp =>
            new LocalVariableStore(sp.GetRequiredService<HostClient>()));

        // 协议清单一律来自 Host.App 已加载的插件，UI 不内置协议名
        services.AddSingleton<IProtocolResolver>(sp =>
            new GrpcProtocolResolver(sp.GetRequiredService<HostClient>()));

        // 串口清单来自宿主所在机器（树莓派上是 /dev/ttyUSB0，不是本机 COM1）
        services.AddSingleton<ISerialPortProvider>(sp =>
            new GrpcSerialPortProvider(sp.GetRequiredService<HostClient>()));

        // Host.App 会话状态（在线与否、版本、路由数）。
        // 健康轮询曾经写在 MainWindow.xaml.cs 里，属于把连接生命周期放进了视图层；
        // 现独立成服务，与 Web 端的 HostSession 职责一致。
        services.AddSingleton<HostSessionService>(sp =>
            new HostSessionService(
                sp.GetRequiredService<HostClient>(),
                sp.GetRequiredService<IAppLogger>()));

        // =====================================================================
        // 页面级 ViewModel（单例）
        // =====================================================================

        // 设备列表、增删改查、连接/断开
        services.AddSingleton<DevicePageViewModel>(sp =>
            new DevicePageViewModel(
                sp.GetRequiredService<IDeviceService>(),
                sp.GetRequiredService<IAppLogger>()));

        // 日志过滤与清空
        services.AddSingleton<LogPageViewModel>(sp =>
            new LogPageViewModel(sp.GetRequiredService<IAppLogger>()));

        // 变量 CRUD 与协议写入
        services.AddSingleton<VariablePageViewModel>(sp =>
            new VariablePageViewModel(
                sp.GetRequiredService<IVariableService>(),
                sp.GetRequiredService<IDeviceService>(),
                sp.GetRequiredService<IAppLogger>()));

        // MES 监控卡片：从 IDeviceService.Devices 同步
        services.AddSingleton<DataMonitorViewModel>(sp =>
            new DataMonitorViewModel(sp.GetRequiredService<IDeviceService>()));

        // 地址配置、连接测试、设置持久化
        services.AddSingleton<SettingsViewModel>(sp =>
            new SettingsViewModel(
                sp.GetRequiredService<HostClient>(),
                sp.GetRequiredService<IConfiguration>()));

        // =====================================================================
        // 页面（Transient：每次导航新建，避免 Frame 缓存）
        // =====================================================================

        // MES 监控页：设备卡片由 ItemsControl 动态生成
        services.AddTransient<DataMonitorPage>(sp =>
            new DataMonitorPage(sp.GetRequiredService<DataMonitorViewModel>()));

        // 设备管理页：编辑面板需要协议清单与宿主侧串口清单
        services.AddTransient<DevicePage>(sp =>
            new DevicePage(
                sp.GetRequiredService<DevicePageViewModel>(),
                sp.GetRequiredService<IProtocolResolver>(),
                sp.GetRequiredService<ISerialPortProvider>()));

        // 变量配置页
        services.AddTransient<VariableConfigPage>(sp =>
            new VariableConfigPage(sp.GetRequiredService<VariablePageViewModel>()));

        // 运行日志页
        services.AddTransient<LogPage>(sp =>
            new LogPage(sp.GetRequiredService<LogPageViewModel>()));

        // 系统设置页
        services.AddTransient<SettingsPage>(sp =>
            new SettingsPage(sp.GetRequiredService<SettingsViewModel>()));

        // =====================================================================
        // 变量轮询 + 主窗口
        // =====================================================================

        // 对 IsPollingEnabled 的变量按 ScanRateMs 后台 ReadAsync
        services.AddSingleton<VariablePollingService>(sp =>
            new VariablePollingService(
                sp.GetRequiredService<IVariableService>(),
                sp.GetRequiredService<HostClient>(),
                sp.GetRequiredService<IRouteReconciler>()));

        // 主窗口注入 IServiceProvider，按导航懒解析页面
        services.AddSingleton<MainWindow>();
    }

    // =========================================================================
    // 退出
    // =========================================================================

    /// <summary>
    /// 应用退出：先停止变量轮询服务（取消所有 ReadAsync 循环），
    /// 再停止并异步释放 IHost（释放 gRPC 通道、DI 容器）。
    /// </summary>
    /// <remarks>
    /// 必须使用 <see cref="IAsyncDisposable.DisposeAsync"/> 而非同步 Dispose：
    /// HostClient 只实现了 IAsyncDisposable，
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
        // 交给 WPF 基类完成其余关闭流程
        base.OnExit(e);
    }
}
