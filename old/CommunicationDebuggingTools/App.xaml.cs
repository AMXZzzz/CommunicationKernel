using CommunicationDebuggingTools.Client;
using CommunicationDebuggingTools.Services;
using CommunicationDebuggingTools.Core;
using CommunicationDebuggingTools.Core.Interfaces;
using CommunicationDebuggingTools.Core.Logging;
using CommunicationDebuggingTools.ViewModels;
using CommunicationDebuggingTools.Views.Pages.Device;
using CommunicationDebuggingTools.Views.Pages.Log;
using CommunicationDebuggingTools.Views.Pages.Monitor;
using CommunicationDebuggingTools.Views.Pages.Settings;
using CommunicationDebuggingTools.Views.VariableConfigPage;
using Microsoft.Extensions.DependencyInjection;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using System.IO;
using System.Windows;
using System.Windows.Threading;

namespace CommunicationDebuggingTools {

    /// <summary>
    /// 应用组合根（Composition Root）。
    /// 唯一知晓所有具体类型的地方；其他层只见接口。
    /// </summary>
    public partial class App : Application {

        /// <summary>根容器；退出 Dispose 后置 null，禁止再解析。</summary>
        public static IServiceProvider Services { get; private set; }

        private IAppLogger _log;
        private CancellationTokenSource _remoteProbeCts;
        private bool _remoteWatchStarted;
        private bool _lastRemoteConnected;
        private bool _shouldStartRemoteWatch;
        private bool _canAutoManageEngineHost;
        private DateTimeOffset? _remoteOfflineSince;
        private DateTimeOffset _lastHostStartAttemptAt = DateTimeOffset.MinValue;
        private readonly Queue<DateTimeOffset> _engineHostStartAttempts = new Queue<DateTimeOffset>();
        private readonly object _engineHostStartSync = new object();
        private Process _engineHostProcess;
        private EngineClient _engineClient;
        private bool _engineHostStartedByApp;

        private TimeSpan _engineHostRestartAfter = TimeSpan.FromSeconds(AppConfig.EngineHostRestartAfterSeconds);
        private TimeSpan _engineHostStartRetryInterval = TimeSpan.FromSeconds(AppConfig.EngineHostStartRetryIntervalSeconds);
        private TimeSpan _engineHostStartWindow = TimeSpan.FromSeconds(AppConfig.EngineHostStartWindowSeconds);
        private int _engineHostStartMaxAttempts = AppConfig.EngineHostStartMaxAttempts;
        private int _remoteProbeIntervalMs = AppConfig.RemoteProbeIntervalMs;

        public static event Action<bool> RemoteConnectionChanged;

        public static bool IsRemoteConnected { get; private set; }

        // ── 启动 ─────────────────────────────────────
        protected override void OnStartup (StartupEventArgs e) {
            base.OnStartup(e);

            // 全局 UI 异常：记日志并阻止进程被直接干掉（便于继续排查）
            DispatcherUnhandledException += App_DispatcherUnhandledException;

            // ① 注册
            Services = BuildServiceProvider();

            _log = Services.GetRequiredService<IAppLogger>();
            _log.Info("App", "服务容器就绪");

            // ② 先显示主窗口，不等待远端状态。
            var mainWindow = Services.GetRequiredService<MainWindow>();
            mainWindow.Show();

            // ③ 初始化（Load 与构造分离）；缓存单例，退出/心跳不再走已释放的 Services
            var settings = AppSettings.Load();
            _shouldStartRemoteWatch = true;
            _canAutoManageEngineHost = CanAutoManageEngineHost(settings.HostAddress);
            _engineHostRestartAfter = TimeSpan.FromSeconds(settings.EngineHostRestartAfterSeconds);
            _engineHostStartRetryInterval = TimeSpan.FromSeconds(settings.EngineHostStartRetryIntervalSeconds);
            _engineHostStartWindow = TimeSpan.FromSeconds(settings.EngineHostStartWindowSeconds);
            _engineHostStartMaxAttempts = settings.EngineHostStartMaxAttempts;
            _remoteProbeIntervalMs = settings.RemoteProbeIntervalMs;

            // ④ 后台探测 EngineHost 在线状态并发布到 UI；连通后启动 Watch 流。
            _engineClient = Services.GetService(typeof(EngineClient)) as EngineClient;
            if (_engineClient != null) {
                _lastRemoteConnected = false;
                _remoteOfflineSince = DateTimeOffset.UtcNow;
                NotifyRemoteConnectionChanged(false);

                // WPF 启动时先尝试拉起本机 EngineHost（仅本机地址场景）。
                if (_canAutoManageEngineHost) {
                    TryStartEngineHostProcess();
                }

                _remoteProbeCts = new CancellationTokenSource();
                StartRemoteWatchInBackground(_engineClient, _remoteProbeCts.Token);
            }

            _log.Info("App", "应用已启动");
        }

        private void App_DispatcherUnhandledException (
            object sender,
            DispatcherUnhandledExceptionEventArgs args) {
            try {
                _log?.Error("App", "UI 未处理异常", args.Exception);
            } catch { }

            try {
                MessageBox.Show(
                    args.Exception?.Message ?? "未知错误",
                    "程序异常",
                    MessageBoxButton.OK,
                    MessageBoxImage.Error);
            } catch { }

            args.Handled = true;
        }

        // ── 退出 ─────────────────────────────────────
        /// <summary>
        /// 顺序必须为：停心跳并摘回调 → 停轮询/断开 → 写日志 → Dispose 容器。
        /// 禁止在 Dispose 之后再 Services.GetService。
        /// </summary>
        protected override void OnExit (ExitEventArgs e) {
            // ① 停远端探测
            DisposeRemoteProbeCts();
            _remoteWatchStarted = false;
            _lastRemoteConnected = false;
            _remoteOfflineSince = null;

            StopManagedEngineHostProcess();

            // 停止 Watch 流；设备会话生命周期归 EngineHost 管理，客户端退出不主动断开
            try { _engineClient?.StopWatch(); } catch (Exception ex) { LogWarnSafe("停止远端 Watch 失败", ex); }

            // ② 日志必须在容器 Dispose 之前
            try { _log?.Info("App", "应用已退出"); } catch { }

            // ④ 释放根容器
            try {
                (Services as IDisposable)?.Dispose();
            } catch (Exception ex) {
                LogWarnSafe("释放服务容器失败", ex);
            }

            Services = null;
            _engineClient = null;
            _log = null;

            base.OnExit(e);
        }

        // ── 服务注册（纯远程：EngineHost 是唯一通讯宿主）─────────
        /// <summary>
        /// 统一容器：WPF 仅作为 EngineHost 的远程客户端。
        /// 所有设备/变量操作经 EngineClient（gRPC）转发，本地不加载协议插件、不采集。
        /// </summary>
        private static IServiceProvider BuildServiceProvider () {
            var settings = AppSettings.Load();
            var sc = new ServiceCollection();

            sc.AddSingleton<IAppLogger>(_ => new MemoryAppLogger(AppConfig.LogCapacity));

            // 远端客户端（唯一数据通路）
            sc.AddSingleton(_ => EngineClient.Connect(settings.HostAddress));

            sc.AddSingleton<IDeviceService>(sp => sp.GetRequiredService<EngineClient>().Devices);
            sc.AddSingleton<IVariableService>(sp => sp.GetRequiredService<EngineClient>().Variables);
            sc.AddSingleton<IPollingEngine>(_ => new NullPollingEngine());

            // 协议列表来自 EngineHost（连通后刷新缓存）
            sc.AddSingleton<RemoteProtocolCatalog>(sp => new RemoteProtocolCatalog(
                sp.GetRequiredService<EngineClient>(),
                sp.GetRequiredService<IAppLogger>()));
            sc.AddSingleton<IProtocolResolver>(sp => sp.GetRequiredService<RemoteProtocolCatalog>());

            // UI 业务层（由当前 UI 项目自行维护）
            sc.AddSingleton(sp => new UiBusiness.VariableBatchService(
                sp.GetRequiredService<IVariableService>(),
                sp.GetRequiredService<IAppLogger>()));
            sc.AddSingleton(sp => new UiBusiness.BulkWriteService(
                sp.GetRequiredService<IDeviceService>(),
                sp.GetRequiredService<IVariableService>(),
                sp.GetRequiredService<IAppLogger>()));

            RegisterPages(sc);
            return sc.BuildServiceProvider();
        }

        private void StartRemoteWatchInBackground (EngineClient client, CancellationToken ct) {
            _ = Task.Run(async () => {
                while (!ct.IsCancellationRequested) {
                    bool connected = false;
                    try {
                        connected = await client.PingAsync(ct).ConfigureAwait(false);
                    } catch (OperationCanceledException) {
                        return;
                    } catch (Exception ex) {
                        connected = false;
                        LogWarnSafe("远端连通性探测失败", ex);
                    }

                    if (connected) {
                        _remoteOfflineSince = null;
                        lock (_engineHostStartSync) {
                            _engineHostStartAttempts.Clear();
                        }
                        if (!_lastRemoteConnected) {
                            _lastRemoteConnected = true;
                            NotifyRemoteConnectionChanged(true);
                            try {
                                var catalog = Services?.GetService(typeof(RemoteProtocolCatalog)) as RemoteProtocolCatalog;
                                if (catalog != null)
                                    await catalog.RefreshAsync(ct).ConfigureAwait(false);
                            } catch (Exception ex) {
                                LogWarnSafe("刷新远程协议列表失败", ex);
                            }
                            await Dispatcher.InvokeAsync(() => {
                                try {
                                    if (_shouldStartRemoteWatch && !_remoteWatchStarted) {
                                        client.StartWatch();
                                        _remoteWatchStarted = true;
                                    }
                                } catch (Exception ex) {
                                    LogWarnSafe("启动远端 Watch 失败", ex);
                                }
                            });
                            try { _log?.Info("App", "远端 EngineHost 已连通"); } catch { }
                        }
                    } else {
                        if (_remoteOfflineSince == null)
                            _remoteOfflineSince = DateTimeOffset.UtcNow;

                        if (_lastRemoteConnected) {
                            _lastRemoteConnected = false;
                            NotifyRemoteConnectionChanged(false);
                            await Dispatcher.InvokeAsync(() => {
                                try {
                                    if (_remoteWatchStarted) {
                                        client.StopWatch();
                                        _remoteWatchStarted = false;
                                    }
                                } catch (Exception ex) {
                                    LogWarnSafe("停止远端 Watch 失败", ex);
                                }
                            });
                            try { _log?.Info("App", "远端 EngineHost 已断开，等待重连"); } catch { }
                        }

                        if (_canAutoManageEngineHost &&
                            _remoteOfflineSince.Value + _engineHostRestartAfter <= DateTimeOffset.UtcNow) {
                            TryStartEngineHostProcess();
                        }
                    }

                    try {
                        await Task.Delay(_remoteProbeIntervalMs, ct).ConfigureAwait(false);
                    } catch (OperationCanceledException) {
                        return;
                    }
                }
            }, ct);
        }

        private bool CanAutoManageEngineHost (string hostAddress) {
            if (string.IsNullOrWhiteSpace(hostAddress)) return true;
            if (!Uri.TryCreate(hostAddress, UriKind.Absolute, out var uri)) return false;
            return uri.IsLoopback ||
                   string.Equals(uri.Host, "localhost", StringComparison.OrdinalIgnoreCase) ||
                   string.Equals(uri.Host, "127.0.0.1", StringComparison.OrdinalIgnoreCase);
        }

        private void TryStartEngineHostProcess () {
            DateTimeOffset now = DateTimeOffset.UtcNow;
            if (!CanStartEngineHost(now))
                return;

            RecordEngineHostStartAttempt(now);

            try {
                if (_engineHostProcess != null && !_engineHostProcess.HasExited)
                    return;
            } catch (Exception ex) {
                LogWarnSafe("EngineHost 进程句柄状态读取失败，准备重新拉起", ex);
            }

            try {
                string baseDir = AppDomain.CurrentDomain.BaseDirectory;
                string debugExe = Path.GetFullPath(Path.Combine(baseDir,
                    @"..\..\..\..\CommunicationDebuggingTools.EngineHost\bin\Debug\net8.0\CommunicationDebuggingTools.EngineHost.exe"));
                string releaseExe = Path.GetFullPath(Path.Combine(baseDir,
                    @"..\..\..\..\CommunicationDebuggingTools.EngineHost\bin\Release\net8.0\CommunicationDebuggingTools.EngineHost.exe"));
                string debugDll = Path.GetFullPath(Path.Combine(baseDir,
                    @"..\..\..\..\CommunicationDebuggingTools.EngineHost\bin\Debug\net8.0\CommunicationDebuggingTools.EngineHost.dll"));
                string releaseDll = Path.GetFullPath(Path.Combine(baseDir,
                    @"..\..\..\..\CommunicationDebuggingTools.EngineHost\bin\Release\net8.0\CommunicationDebuggingTools.EngineHost.dll"));

                ProcessStartInfo psi = null;
                string launchDirectory = baseDir;
                if (File.Exists(debugExe)) {
                    psi = new ProcessStartInfo(debugExe);
                    launchDirectory = Path.GetDirectoryName(debugExe) ?? baseDir;
                } else if (File.Exists(releaseExe)) {
                    psi = new ProcessStartInfo(releaseExe);
                    launchDirectory = Path.GetDirectoryName(releaseExe) ?? baseDir;
                } else if (File.Exists(debugDll)) {
                    psi = new ProcessStartInfo("dotnet", "\"" + debugDll + "\"");
                    launchDirectory = Path.GetDirectoryName(debugDll) ?? baseDir;
                } else if (File.Exists(releaseDll)) {
                    psi = new ProcessStartInfo("dotnet", "\"" + releaseDll + "\"");
                    launchDirectory = Path.GetDirectoryName(releaseDll) ?? baseDir;
                }

                if (psi == null) {
                    _log?.Error("App", "自动拉起 EngineHost 失败：未找到可执行文件");
                    return;
                }

                psi.UseShellExecute = false;
                psi.CreateNoWindow = true;
                psi.WorkingDirectory = launchDirectory;

                _engineHostProcess?.Dispose();
                _engineHostProcess = Process.Start(psi);
                if (_engineHostProcess == null) {
                    _log?.Error("App", "自动拉起 EngineHost 失败：进程启动返回空实例");
                    return;
                }

                _engineHostStartedByApp = true;
                _log?.Info("App", "已尝试自动拉起 EngineHost");
            } catch (Exception ex) {
                _log?.Error("App", "自动拉起 EngineHost 异常", ex);
            }
        }

        private bool CanStartEngineHost (DateTimeOffset now) {
            lock (_engineHostStartSync) {
                if (_lastHostStartAttemptAt + _engineHostStartRetryInterval > now)
                    return false;

                TrimEngineHostStartAttempts(now);
                if (_engineHostStartAttempts.Count >= _engineHostStartMaxAttempts) {
                    _log?.Warn("App", "EngineHost 自动拉起已触发限流，等待窗口冷却");
                    return false;
                }

                return true;
            }
        }

        private void RecordEngineHostStartAttempt (DateTimeOffset now) {
            lock (_engineHostStartSync) {
                TrimEngineHostStartAttempts(now);
                _engineHostStartAttempts.Enqueue(now);
                _lastHostStartAttemptAt = now;
            }
        }

        private void TrimEngineHostStartAttempts (DateTimeOffset now) {
            DateTimeOffset min = now - _engineHostStartWindow;
            while (_engineHostStartAttempts.Count > 0 && _engineHostStartAttempts.Peek() < min) {
                _engineHostStartAttempts.Dequeue();
            }
        }

        private void DisposeRemoteProbeCts () {
            CancellationTokenSource cts = Interlocked.Exchange(ref _remoteProbeCts, null);
            if (cts == null)
                return;

            try { cts.Cancel(); } catch (Exception ex) { LogWarnSafe("取消远端探测令牌失败", ex); }
            try { cts.Dispose(); } catch (Exception ex) { LogWarnSafe("释放远端探测令牌失败", ex); }
        }

        private void StopManagedEngineHostProcess () {
            Process process = _engineHostProcess;
            _engineHostProcess = null;

            if (process == null) {
                _engineHostStartedByApp = false;
                return;
            }

            try {
                if (_engineHostStartedByApp && !process.HasExited) {
                    process.Kill(true);
                    process.WaitForExit(2000);
                }
            } catch (Exception ex) {
                LogWarnSafe("停止 EngineHost 进程失败", ex);
            } finally {
                try { process.Dispose(); } catch (Exception ex) { LogWarnSafe("释放 EngineHost 进程句柄失败", ex); }
                _engineHostStartedByApp = false;
            }
        }

        private void LogWarnSafe (string message, Exception ex) {
            try {
                _log?.Warn("App", message + "：" + ex.Message);
            } catch { }
        }

        private void LogErrorSafe (string message, Exception ex) {
            try {
                _log?.Error("App", message, ex);
            } catch { }
        }

        private static void NotifyRemoteConnectionChanged (bool connected) {
            IsRemoteConnected = connected;
            try { RemoteConnectionChanged?.Invoke(connected); } catch { }
        }

        private static void RegisterPages (ServiceCollection sc) {
            // ViewModels（Transient）
            sc.AddTransient<DevicePageViewModel>();
            sc.AddTransient<VariablePageViewModel>();
            sc.AddTransient<LogPageViewModel>();
            // Pages（Transient）
            sc.AddTransient<DevicePage>();
            sc.AddTransient<VariableConfigPage>();
            sc.AddTransient<LogPage>();
            sc.AddTransient<DataMonitorPage>();
            sc.AddTransient<SettingsPage>();
            // 主窗口（Singleton）
            sc.AddSingleton<MainWindow>();
        }
    }

    /// <summary>远端模式下的空轮询引擎桩（轮询由 EngineHost 负责）。</summary>
    internal sealed class NullPollingEngine : IPollingEngine {
        public bool IsRunning => false;
        public event Action<string, bool> CycleCompleted { add { } remove { } }
        public void Start () { }
        public void Stop  () { }
    }
}
