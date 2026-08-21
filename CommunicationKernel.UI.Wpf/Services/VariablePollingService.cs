// -----------------------------------------------------------------------------
// 文件: Services/VariablePollingService.cs
// 层级: UI 层 — 变量轮询服务
// 作用: 管理所有启用了轮询（IsPollingEnabled = true）的变量的后台读取任务。
//       订阅 IVariableService.VariablesChanged 事件，在变量列表发生变化时
//       自动重建轮询任务集合（停止已删除/禁用的，启动新增/启用的）。
//       每次轮询调用 EngineHostGrpcClient.ReadAsync，将读取结果通过
//       ValueParser.TryParseBytes 转为可读字符串，写入 VariableItem.LastValue。
// 生命周期:
//   App.xaml.cs 在 host.StartAsync() 后调用 Start()；
//   应用退出时 Dispose() 取消所有后台任务。
// 线程模型:
//   VariablesChanged 回调可能来自任意线程 → OnVariablesChanged 内部持锁操作字典；
//   每个变量独立一个 Task.Run 轮询循环；
//   写入 VariableItem 属性时切回 UI 线程（Application.Current.Dispatcher.InvokeAsync）。
// 调用链:
//   IVariableService.VariablesChanged
//     → OnVariablesChanged → 重建 _polls 字典
//       → Task.Run(PollLoop) → ReadAsync → TryParseBytes → item.LastValue / item.LastError
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
        // 私有字段
        // =========================================================================

        /// <summary>变量服务，用于获取变量列表快照和订阅变化事件。</summary>
        private readonly IVariableService _variableService;

        /// <summary>gRPC 客户端，用于调用 ReadAsync。</summary>
        private readonly EngineHostGrpcClient _client;

        /// <summary>
        /// 每个变量 ID 对应的轮询任务取消令牌源。
        /// 键为 VariableItem.Id，值为该变量轮询任务的 CancellationTokenSource。
        /// </summary>
        private readonly Dictionary<string, CancellationTokenSource> _polls
            = new Dictionary<string, CancellationTokenSource>();

        /// <summary>保护 _polls 字典的同步锁（VariablesChanged 可能来自任意线程）。</summary>
        private readonly object _lock = new object();

        /// <summary>服务是否已启动（防止多次调用 Start）。</summary>
        private bool _started = false;

        // =========================================================================
        // 构造函数
        // =========================================================================

        /// <param name="variableService">变量服务（必须非 null）。</param>
        /// <param name="client">gRPC 客户端（必须非 null）。</param>
        public VariablePollingService(IVariableService variableService, EngineHostGrpcClient client)
        {
            _variableService = variableService
                ?? throw new ArgumentNullException(nameof(variableService));
            _client = client
                ?? throw new ArgumentNullException(nameof(client));
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
                foreach (CancellationTokenSource cts in _polls.Values)
                {
                    cts.Cancel();
                    cts.Dispose();
                }
                _polls.Clear();
            }
        }

        // =========================================================================
        // 私有方法
        // =========================================================================

        /// <summary>
        /// 变量列表变化时触发：停止所有现有轮询任务，重新启动所有启用了轮询的变量任务。
        /// "全停全起"策略：变量管理操作频率极低，重建开销可忽略，逻辑最简洁。
        /// </summary>
        private void OnVariablesChanged()
        {
            // 取得当前变量列表快照（线程安全）
            IReadOnlyList<VariableItem> snapshot = _variableService.Variables;

            lock (_lock)
            {
                // 停止所有现有轮询任务
                foreach (CancellationTokenSource cts in _polls.Values)
                {
                    cts.Cancel();
                    cts.Dispose();
                }
                _polls.Clear();

                // 为每个启用轮询且配置完整的变量启动新任务
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

                    CancellationTokenSource cts = new CancellationTokenSource();
                    _polls[item.Id] = cts;

                    // 在后台线程运行轮询循环，捕获必要参数避免闭包问题
                    string capturedId      = item.Id;
                    int    capturedRateMs  = item.ScanRateMs > 0 ? item.ScanRateMs : 1000;
                    CancellationToken token = cts.Token;

                    _ = Task.Run(() => PollLoop(capturedId, capturedRateMs, token), token);
                }
            }
        }

        /// <summary>
        /// 单变量轮询循环。
        /// 每隔 <paramref name="scanRateMs"/> 毫秒向 EngineHost 发起一次 Read，
        /// 将结果写入 <see cref="VariableItem.LastValue"/> / <see cref="VariableItem.LastError"/>。
        /// </summary>
        /// <param name="variableId">目标变量 ID。</param>
        /// <param name="scanRateMs">轮询间隔（毫秒）。</param>
        /// <param name="ct">外部取消令牌，取消后退出循环。</param>
        private async Task PollLoop(string variableId, int scanRateMs, CancellationToken ct)
        {
            while (!ct.IsCancellationRequested)
            {
                // 先等待一个周期再读取，避免启动时立即发起请求与设备建立连接时机冲突
                try
                {
                    await Task.Delay(scanRateMs, ct).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    // 等待期间被取消：正常退出
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

                // 向 EngineHost 发起读取请求
                try
                {
                    ReadResultDto result = await _client
                        .ReadAsync(item.DeviceId, item.Address, item.Length, ct)
                        .ConfigureAwait(false);

                    if (result.Success)
                    {
                        // 解析字节数组为可读字符串
                        bool parsed = ValueParser.TryParseBytes(
                            item.DataType, result.Data, out string display);

                        // 切回 UI 线程更新属性（VariableItem 的属性 setter 触发 PropertyChanged）
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
                    else
                    {
                        // 读取业务失败：展示错误码，清空 LastValue
                        string errText = string.IsNullOrEmpty(result.ErrorCode)
                            ? result.ErrorMessage
                            : string.Format("{0}: {1}", result.ErrorCode, result.ErrorMessage);

                        Application app = Application.Current;
                        if (app != null)
                        {
                            await app.Dispatcher.InvokeAsync(() =>
                            {
                                item.LastError = errText;
                            });
                        }
                    }
                }
                catch (OperationCanceledException)
                {
                    // 读取期间被取消：正常退出
                    break;
                }
                catch (Exception ex)
                {
                    // 网络异常：记录到 LastError，不中断轮询（等下一个周期重试）
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
            return null;
        }
    }
}
