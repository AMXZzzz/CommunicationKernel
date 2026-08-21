using CommunicationDebuggingTools.Core;
using CommunicationDebuggingTools.Core.Interfaces;
using CommunicationDebuggingTools.Core.Models;
using CommunicationDebuggingTools.Core.Logging;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace CommunicationDebuggingTools.Business.Variable {

    /// <summary>
    /// 变量周期采集引擎。
    ///
    /// 设计决策：
    ///   1. 按设备分组：同一设备的变量串行读取，避免并发争抢会话锁。
    ///      不同设备的循环互相独立（各自一个 Task），并发采集。
    ///   2. 按 ScanRateMs 计时：每读完一个周期记录下次应读时间，
    ///      通过 100 ms 主定时节拍驱动，实际精度 ≤ 100 ms 误差。
    ///   3. 变量读写走协议真异步 ReadAsync；按设备串行、跨设备并发，避免打满会话锁。
    ///   4. 属性更新通过 SynchronizationContext.Post 回 UI 线程，WPF 绑定安全。
    ///      （WPF 4.5+ 对单个属性的跨线程通知有内置 marshal，但显式 Post 更可靠）
    /// </summary>
    public sealed class PollingEngine : IPollingEngine {

        // ── 常量 ─────────────────────────────────────────
        /// <summary>主循环节拍（毫秒）。决定了 ScanRateMs 的实际最小粒度。</summary>
        private const int TICK_MS = AppConfig.PollingTickMs;

        /// <summary>Stop() 等待后台任务结束的超时。</summary>
        private const int STOP_WAIT_MS = AppConfig.PollingStopWaitMs;

        // ── 依赖 ─────────────────────────────────────────
        private readonly IVariableService  _variables;
        private readonly IDeviceService    _devices;
        private readonly IAppLogger        _log;

        /// <summary>构造时在 UI 线程捕获；用于把属性更新回传 UI 线程。</summary>
        private readonly SynchronizationContext _uiCtx;

        // ── 运行时状态 ────────────────────────────────────
        private CancellationTokenSource _cts;
        private Task                    _masterTask;

        /// <summary>variableId → 下次应读时间（UTC）。在后台线程读写，单线程安全。</summary>
        private readonly Dictionary<string, DateTime> _nextRead =
            new Dictionary<string, DateTime>();

        // ── 接口实现 ──────────────────────────────────────
        public bool IsRunning =>
            _masterTask != null &&
            !_masterTask.IsCompleted &&
            !_masterTask.IsCanceled &&
            !_masterTask.IsFaulted;

        public event Action<string, bool> CycleCompleted;

        // ── 构造 ──────────────────────────────────────────
        /// <summary>
        /// 必须在 UI 线程构造，以便捕获 WPF DispatcherSynchronizationContext。
        /// </summary>
        public PollingEngine (
            IVariableService variables,
            IDeviceService devices,
            IAppLogger logger = null) {

            _variables = variables ?? throw new ArgumentNullException(nameof(variables));
            _devices = devices ?? throw new ArgumentNullException(nameof(devices));
            _log = logger;
            _uiCtx = SynchronizationContext.Current;

            if (_uiCtx == null)
                _log?.Warn("PollingEngine", "构造时 SynchronizationContext 为 null，" +
                    "属性更新将在后台线程执行（请确保在 UI 线程构造 PollingEngine）。");
        }

        // ── 公开控制 ──────────────────────────────────────
        public void Start () {
            if (IsRunning) return;

            _cts = new CancellationTokenSource();
            _masterTask = Task.Run(() => MasterLoopAsync(_cts.Token));
            _log?.Info("PollingEngine", "轮询引擎已启动，节拍=" + TICK_MS + " ms");
        }

        public void Stop () {
            if (_cts == null) return;

            _cts.Cancel();
            try {
                _masterTask?.Wait(STOP_WAIT_MS);
            } catch (AggregateException) { }   // OperationCanceledException 正常
            finally {
                _cts.Dispose();
                _cts = null;
                _masterTask = null;
                _nextRead.Clear();
            }
            _log?.Info("PollingEngine", "轮询引擎已停止");
        }

        // ── 主循环 ────────────────────────────────────────
        /// <summary>
        /// 主循环：每 TICK_MS 毫秒扫描一次，按设备分组并发触发设备循环。
        /// </summary>
        private async Task MasterLoopAsync (CancellationToken ct) {
            while (!ct.IsCancellationRequested) {
                DateTime tickStart = DateTime.UtcNow;

                // 按设备分组，各设备的循环互相独立
                IEnumerable<IGrouping<string, VariableItem>> byDevice;
                try {
                    // Variables 是 ObservableCollection，只在 UI 线程修改，
                    // 此处后台线程只读，.ToList() 创建快照避免枚举中被修改
                    byDevice = _variables.Variables
                        .Where(v => v != null
                            && !string.IsNullOrEmpty(v.DeviceId)
                            && v.IsPollingEnabled
                            && v.Access != Core.Enums.VariableAccess.WriteOnly)
                        .ToList()
                        .GroupBy(v => v.DeviceId);
                } catch (Exception ex) {
                    _log?.Warn("PollingEngine", "变量列表快照失败: " + ex.Message);
                    await DelayTickAsync(tickStart, ct).ConfigureAwait(false);
                    continue;
                }

                // 每个设备启动独立 Task（不同设备可并发，同设备内串行）
                var deviceTasks = new List<Task>();
                foreach (var group in byDevice) {
                    string deviceId = group.Key;
                    List<VariableItem> vars = group.ToList();
                    deviceTasks.Add(Task.Run(() => PollDeviceAsync(deviceId, vars, ct), ct));
                }

                try {
                    await Task.WhenAll(deviceTasks).ConfigureAwait(false);
                } catch (OperationCanceledException) {
                    return;
                } catch (Exception ex) {
                    _log?.Warn("PollingEngine", "设备轮询批次异常: " + ex.Message);
                }

                await DelayTickAsync(tickStart, ct).ConfigureAwait(false);
            }
        }

        /// <summary>
        /// 单设备轮询：串行读取当前节拍到期的变量。
        /// </summary>
        private async Task PollDeviceAsync (
            string deviceId,
            List<VariableItem> vars,
            CancellationToken ct) {

            // 检查设备是否已连接（快速路径，避免无用 I/O）
            DeviceInfo device = FindDevice(deviceId);
            if (device == null || !device.IsConnected) return;

            DateTime now = DateTime.UtcNow;

            foreach (VariableItem v in vars) {
                if (ct.IsCancellationRequested) return;

                // 检查是否到期
                DateTime due;
                if (_nextRead.TryGetValue(v.Id, out due) && now < due)
                    continue;

                OperationResult result = null;
                try {
                    result = await _variables.ReadAsync(v.Id, ct).ConfigureAwait(false);
                } catch (OperationCanceledException) {
                    return;
                } catch (Exception ex) {
                    _log?.Warn("PollingEngine",
                        string.Format("读取 [{0}]{1} 异常: {2}", deviceId, v.Name, ex.Message));
                }
                bool ok = result != null && result.Success;

                // 更新下次读取时间
                _nextRead[v.Id] = DateTime.UtcNow.AddMilliseconds(v.ScanRateMs);

                // 回调通知（Post 到 UI 线程）
                FireCycleCompleted(v.Id, ok);
            }
        }

        // ── 辅助 ──────────────────────────────────────────
        private DeviceInfo FindDevice (string deviceId) {
            try {
                return _devices.Devices.FirstOrDefault(d => d != null && d.Id == deviceId);
            } catch {
                return null;
            }
        }

        private void FireCycleCompleted (string variableId, bool ok) {
            Action<string, bool> handler = CycleCompleted;
            if (handler == null) return;

            if (_uiCtx != null)
                _uiCtx.Post(_ => handler(variableId, ok), null);
            else
                handler(variableId, ok);
        }

        private static async Task DelayTickAsync (DateTime tickStart, CancellationToken ct) {
            TimeSpan elapsed  = DateTime.UtcNow - tickStart;
            TimeSpan remaining = TimeSpan.FromMilliseconds(TICK_MS) - elapsed;
            if (remaining > TimeSpan.Zero) {
                try {
                    await Task.Delay(remaining, ct).ConfigureAwait(false);
                } catch (OperationCanceledException) { }
            }
        }
    }
}