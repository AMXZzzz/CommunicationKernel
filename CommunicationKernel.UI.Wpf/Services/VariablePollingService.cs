#nullable disable

// -----------------------------------------------------------------------------
// 文件: Services/VariablePollingService.cs
// 层级: UI 层 — WPF 变量轮询服务
// 作用: 为启用轮询的变量跑后台 ReadAsync，结果写入 LastValue；遇 RouteNotFound 则对账重注册。
// -----------------------------------------------------------------------------

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using CommunicationKernel.UI.Wpf.Core.Enums;
using CommunicationKernel.UI.Wpf.Core.Interfaces;
using CommunicationKernel.UI.Wpf.Core.Models;

namespace CommunicationKernel.UI.Wpf.Services
{
    /// <summary>
    /// 变量轮询后台服务。
    /// 单例生命周期，由 DI 容器持有。调用 <see cref="Start"/> 后开始监听变量变化。
    /// </summary>
    public sealed class VariablePollingService : IDisposable
    {
        // =========================================================================
        // 常量
        // =========================================================================

        /// <summary>变量未指定 ScanRateMs 时使用的默认轮询周期（毫秒）。</summary>
        private const int DefaultScanRateMs = 1000;

        /// <summary>连续失败时退避延迟的上限（毫秒）。</summary>
        private const int MaxBackoffMs = 60_000;

        // =========================================================================
        // 私有字段
        // =========================================================================

        /// <summary>变量服务，用于获取变量列表快照和订阅变化事件。</summary>
        private readonly IVariableService _variableService;

        /// <summary>gRPC 客户端，用于调用 ReadAsync。</summary>
        private readonly HostClient _client;

        /// <summary>
        /// 路由对账器：宿主重启导致路由消失时，据本地配置把它重新注册回去。
        /// 可为 null（此时退化为原有行为：一直退避重试）。
        /// </summary>
        private readonly IRouteReconciler _reconciler;

        /// <summary>
        /// EngineHostingServiceApp 在路由不存在时返回的错误码字面量。
        /// </summary>
        /// <remarks>
        /// 服务端以 <c>KernelErrorCode.RouteNotFound.ToString()</c> 填充 error_code，
        /// 因此这里必须与枚举成员名逐字一致。用常量而非散落的字面量，
        /// 是为了让枚举改名时至少有一处集中可查。
        /// </remarks>
        private const string RouteNotFoundCode = "RouteNotFound";

        /// <summary>
        /// 正在运行的轮询任务，键为 VariableItem.Id。
        /// 保存扫描周期以便识别「周期被改动」从而只重启该变量。
        /// </summary>
        private readonly Dictionary<string, PollHandle> _polls
            = new Dictionary<string, PollHandle>();

        /// <summary>单个变量轮询任务的句柄。</summary>
        private sealed class PollHandle
        {
            /// <summary>该任务的取消令牌源。</summary>
            public CancellationTokenSource Cts { get; set; }

            /// <summary>任务启动时使用的扫描周期，用于比较是否需要重启。</summary>
            public int ScanRateMs { get; set; }
        }

        /// <summary>为分散首次请求而共用的随机源（仅用于抖动，无需加密强度）。</summary>
        private readonly Random _jitter = new Random();

        /// <summary>保护 _polls 字典的同步锁（VariablesChanged 可能来自任意线程）。</summary>
        private readonly object _lock = new object();

        /// <summary>服务是否已启动（防止多次调用 Start）。</summary>
        private bool _started = false;

        // =========================================================================
        // 构造函数
        // =========================================================================

        /// <param name="variableService">变量服务（必须非 null）。</param>
        /// <param name="client">gRPC 客户端（必须非 null）。</param>
        /// <param name="reconciler">
        /// 路由对账器，可为 null。为 null 时宿主重启后轮询将永远收到 RouteNotFound，
        /// 需要操作员手工重新注册设备。
        /// </param>
        public VariablePollingService(
            IVariableService variableService,
            HostClient client,
            IRouteReconciler reconciler = null)
        {
            // 变量服务与 gRPC 客户端必填；对账器可空（空则 RouteNotFound 只能退避）
            _variableService = variableService
                ?? throw new ArgumentNullException(nameof(variableService));
            _client = client
                ?? throw new ArgumentNullException(nameof(client));
            _reconciler = reconciler;
        }

        // =========================================================================
        // 公开接口
        // =========================================================================

        /// <summary>
        /// 启动轮询服务：订阅 VariablesChanged 事件并执行首次同步。
        /// 应在 host.StartAsync() 后、主窗口显示前调用一次。
        /// </summary>
        public void Start()
        {
            // 防止重复订阅 VariablesChanged
            if (_started) return;
            _started = true;

            // 订阅变量列表变化事件，在变量增删改时自动重建轮询集合
            _variableService.VariablesChanged += OnVariablesChanged;

            // 立即执行一次同步，处理 Start 前已存在的启用轮询变量
            OnVariablesChanged();
        }

        /// <summary>
        /// 停止所有轮询任务并释放资源。
        /// 由 DI 容器在应用退出时调用。
        /// </summary>
        public void Dispose()
        {
            // 取消订阅，防止 Dispose 后继续触发
            _variableService.VariablesChanged -= OnVariablesChanged;

            lock (_lock)
            {
                // 取消并释放所有正在运行的轮询任务
                foreach (PollHandle handle in _polls.Values)
                {
                    handle.Cts.Cancel();
                    handle.Cts.Dispose();
                }
                _polls.Clear();
            }
        }

        // =========================================================================
        // 私有方法
        // =========================================================================

        /// <summary>
        /// 变量列表变化时触发：差量同步轮询任务集合。
        /// 只启动新增/新启用的、停止已删除/已禁用的、重启周期被改动的，
        /// 其余任务原样运行不受打扰。
        /// </summary>
        /// <remarks>
        /// 不使用"全停全起"：那会让每次勾选一个复选框都重置所有变量的
        /// 退避状态与计时相位，使 N 个变量的请求对齐到同一时刻形成周期性尖峰
        /// （对串口路由尤其危险）。
        /// </remarks>
        private void OnVariablesChanged()
        {
            // 取得当前变量列表快照（线程安全）
            IReadOnlyList<VariableItem> snapshot = _variableService.Variables;

            // 计算期望运行的轮询集合：Id → 扫描周期
            Dictionary<string, int> desired = new Dictionary<string, int>();
            foreach (VariableItem item in snapshot)
            {
                // 跳过无效或未启用轮询的变量
                if (item == null
                    || !item.IsPollingEnabled
                    || string.IsNullOrEmpty(item.Id)
                    || string.IsNullOrEmpty(item.DeviceId)
                    || string.IsNullOrEmpty(item.Address))
                {
                    continue;
                }

                desired[item.Id] = item.ScanRateMs > 0 ? item.ScanRateMs : DefaultScanRateMs;
            }

            lock (_lock)
            {
                // 1. 停止不再需要的任务：已删除、已禁用，或周期发生变化需重启
                List<string> toStop = new List<string>();
                foreach (KeyValuePair<string, PollHandle> running in _polls)
                {
                    // 仍在期望集合且周期未变才保留
                    bool stillWanted = desired.TryGetValue(running.Key, out int wantedRate)
                                       && wantedRate == running.Value.ScanRateMs;
                    if (!stillWanted)
                        toStop.Add(running.Key);
                }

                foreach (string id in toStop)
                {
                    // 取消后台循环并释放令牌
                    PollHandle handle = _polls[id];
                    handle.Cts.Cancel();
                    handle.Cts.Dispose();
                    _polls.Remove(id);
                }

                // 2. 启动尚未运行的任务（含因周期变化刚被停掉的）
                foreach (KeyValuePair<string, int> want in desired)
                {
                    if (_polls.ContainsKey(want.Key))
                        continue;   // 已在运行且周期未变，保持不动

                    CancellationTokenSource cts = new CancellationTokenSource();
                    _polls[want.Key] = new PollHandle { Cts = cts, ScanRateMs = want.Value };

                    // 捕获必要参数避免闭包捕获循环变量
                    string capturedId     = want.Key;
                    int    capturedRateMs = want.Value;
                    CancellationToken token = cts.Token;

                    // 首次请求加入 0~周期 之间的随机抖动，避免批量启用时
                    // 所有变量在同一时刻并发发起 Read
                    int startJitterMs;
                    lock (_jitter)
                        startJitterMs = _jitter.Next(0, Math.Max(1, capturedRateMs));

                    _ = Task.Run(
                        () => PollLoop(capturedId, capturedRateMs, startJitterMs, token), token);
                }
            }
        }

        /// <summary>
        /// 单变量轮询循环。
        /// 每隔 <paramref name="scanRateMs"/> 毫秒向 EngineHostingServiceApp 发起一次 Read，
        /// 将结果写入 <see cref="VariableItem.LastValue"/> / <see cref="VariableItem.LastError"/>。
        /// </summary>
        /// <param name="variableId">目标变量 ID。</param>
        /// <param name="scanRateMs">轮询间隔（毫秒）。</param>
        /// <param name="startJitterMs">首轮额外延迟，用于分散批量启动时的并发请求。</param>
        /// <param name="ct">外部取消令牌，取消后退出循环。</param>
        private async Task PollLoop(
            string variableId, int scanRateMs, int startJitterMs, CancellationToken ct)
        {
            // 首轮抖动：错开同时启用的多个变量，避免请求对齐成尖峰
            if (startJitterMs > 0)
            {
                try
                {
                    await Task.Delay(startJitterMs, ct).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    return;
                }
            }

            // 连续失败计数，用于指数退避延迟计算
            int consecutiveFails = 0;

            while (!ct.IsCancellationRequested)
            {
                // 指数退避：每次失败后下一轮延迟翻倍，上限 60 秒；成功后重置为正常周期
                // 首次或成功后使用正常 scanRateMs；失败后 delay = min(scanRateMs * 2^n, 60000)
                // 先按 long 计算再夹取上限，避免大周期 × 退避倍数时 int 溢出
                int delayMs = consecutiveFails == 0
                    ? scanRateMs
                    : (int)Math.Min(
                        (long)scanRateMs * (1L << Math.Min(consecutiveFails, 10)),
                        MaxBackoffMs);

                try
                {
                    await Task.Delay(delayMs, ct).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    break;
                }

                // 重新从变量服务获取最新的变量引用
                // （Update 可能替换了内部对象，重新查找保证操作最新实例）
                VariableItem item = FindVariable(variableId);
                if (item == null)
                {
                    // 变量已被删除：退出轮询
                    break;
                }

                if (!item.IsPollingEnabled)
                {
                    // 变量轮询已被禁用（Update 修改了标志）：退出，等待下次 VariablesChanged 重建
                    break;
                }

                try
                {
                    ReadResultDto result = await _client
                        .ReadAsync(item.DeviceId, item.Address, item.Length, ct)
                        .ConfigureAwait(false);

                    if (result.Success)
                    {
                        // 读取成功：重置退避计数，解析字节数组并更新 UI
                        consecutiveFails = 0;

                        bool parsed = ValueParser.TryParseBytes(
                            item.DataType, result.Data, out string display);

                        Application app = Application.Current;
                        if (app != null)
                        {
                            await app.Dispatcher.InvokeAsync(() =>
                            {
                                item.LastValue = parsed ? display : "?";
                                item.LastError = string.Empty;
                            });
                        }
                    }
                    else if (string.Equals(result.ErrorCode, RouteNotFoundCode, StringComparison.Ordinal)
                             && _reconciler != null)
                    {
                        // 路由在宿主侧不存在——几乎总是宿主重启导致的（其路由是纯内存的）。
                        //
                        // 这一类失败与「PLC 拒绝读取」性质完全不同：单纯退避重试
                        // 永远好不了，因为请求本身发往一条已经不存在的路由。
                        // 用本地留存的设备配置把它重新注册回去，才可能恢复。
                        //
                        // 对账器内部做并发合并与最小间隔节流，这里可以放心每轮都调。
                        bool restored = await _reconciler
                            .EnsureRouteAsync(item.DeviceId, ct)
                            .ConfigureAwait(false);

                        if (restored)
                        {
                            // 路由已恢复：清零退避，下一轮立刻按正常周期重试
                            consecutiveFails = 0;
                            await SetErrorAsync(item, "路由已重新注册，正在恢复读取").ConfigureAwait(false);
                        }
                        else
                        {
                            // 仍未恢复（宿主没起来 / PLC 不可达 / 处于节流窗口）：照常退避
                            consecutiveFails++;
                            await SetErrorAsync(item, "路由不存在，正在尝试重新注册").ConfigureAwait(false);
                        }
                    }
                    else
                    {
                        // 读取业务失败（PLC 拒绝）：累计失败次数，触发退避
                        consecutiveFails++;
                        string errText = string.IsNullOrEmpty(result.ErrorCode)
                            ? result.ErrorMessage
                            : string.Format("{0}: {1}", result.ErrorCode, result.ErrorMessage);

                        await SetErrorAsync(item, errText).ConfigureAwait(false);
                    }
                }
                catch (OperationCanceledException)
                {
                    break;
                }
                catch (Exception ex)
                {
                    // 网络异常：累计失败次数，触发退避，不中断轮询（等下一个周期重试）
                    consecutiveFails++;
                    Application app = Application.Current;
                    if (app != null)
                    {
                        string errMsg = ex.Message;
                        await app.Dispatcher.InvokeAsync(() =>
                        {
                            item.LastError = errMsg;
                        });
                    }
                }
            }
        }

        /// <summary>切回 UI 线程写入变量的错误文本。</summary>
        /// <remarks>
        /// VariableItem 绑定在界面上，属性变更通知必须发生在 UI 线程；
        /// 三处失败分支都要做同一件事，收敛到一处以免其中一处漏掉线程切换。
        /// </remarks>
        private static async Task SetErrorAsync(VariableItem item, string message)
        {
            // 应用退出时 Dispatcher 可能已空
            Application app = Application.Current;
            if (app == null) return;

            await app.Dispatcher.InvokeAsync(() =>
            {
                item.LastError = message;
            });
        }

        /// <summary>
        /// 在 <see cref="IVariableService.Variables"/> 快照中查找指定 ID 的变量。
        /// </summary>
        private VariableItem FindVariable(string id)
        {
            foreach (VariableItem v in _variableService.Variables)
            {
                if (v != null && v.Id == id)
                    return v;
            }
            // 变量已被删除
            return null;
        }
    }
}
