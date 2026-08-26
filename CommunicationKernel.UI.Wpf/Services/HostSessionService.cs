#nullable disable

// -----------------------------------------------------------------------------
// 文件: Services/HostSessionService.cs
// 层级: UI 层 — WPF 服务
// 作用: 维护与 EngineHostingServiceApp 的会话状态（在线与否、版本、路由数），后台周期性健康探测。
// 调用链:
//   App 启动 → HostSessionService.Start() → HostClient.HealthAsync() → gRPC
//   状态变化 → Changed 事件 → MainWindow 更新顶栏指示灯
//
// 为什么要有这个类:
//   健康轮询此前直接写在 MainWindow.xaml.cs 里——连接生命周期落在了视图层。
//   问题有三：
//     1) 无法在不起窗口的前提下测试会话状态机；
//     2) 其他页面想知道"Host 在不在线"只能反向去问窗口；
//     3) 窗口的职责本应只是"把状态画出来"，却顺带管起了轮询节奏与取消。
//   Web 端的对应实现是 UI.WebMaster/Services/HostSession.cs，两端职责划分现已一致。
//
// 方向纪律:
//   本服务只向上<b>发布</b>事件，不引用任何窗口或页面类型。
//   视图订阅 Changed 并自行决定怎么渲染——这是依赖倒置，不是向上调用。
// -----------------------------------------------------------------------------

using System;
using System.Threading;
using System.Threading.Tasks;
using CommunicationKernel.EngineHost.Sdk;
using CommunicationKernel.UI.Wpf.Core.Logging;

namespace CommunicationKernel.UI.Wpf.Services
{
    /// <summary>
    /// EngineHostingServiceApp 会话状态，单例。周期性健康探测并在状态变化时发出 <see cref="Changed"/>。
    /// </summary>
    public sealed class HostSessionService : IDisposable
    {
        // ====================================================================
        // 常量
        // ====================================================================

        /// <summary>
        /// 健康探测间隔（秒）。
        /// 太短会让离线期间的日志被失败记录淹没，太长则状态灯滞后于现实。
        /// </summary>
        private const int PollIntervalSeconds = 10;

        // ====================================================================
        // 私有字段
        // ====================================================================

        /// <summary>gRPC 客户端，由 DI 注入的单例。</summary>
        private readonly IHostClient _client;

        /// <summary>应用日志器，可为 null（此时不记录）。</summary>
        private readonly IAppLogger _log;

        /// <summary>轮询取消源；<see cref="Dispose"/> 时触发。</summary>
        private readonly CancellationTokenSource _cts = new CancellationTokenSource();

        /// <summary>轮询任务句柄，仅用于避免重复启动。</summary>
        private Task _pollTask;

        /// <summary>Dispose 幂等标志，0 = 未释放，1 = 已释放。</summary>
        private int _disposed;

        // ====================================================================
        // 构造函数
        // ====================================================================

        /// <param name="client">EngineHostingServiceApp 客户端，必填。</param>
        /// <param name="log">可选日志器，为 null 时静默。</param>
        public HostSessionService(IHostClient client, IAppLogger log = null)
        {
            // 没有客户端就无从探测，属于装配错误，尽早失败
            _client = client ?? throw new ArgumentNullException(nameof(client));
            _log    = log;
        }

        // ====================================================================
        // 会话状态
        // ====================================================================

        /// <summary>EngineHostingServiceApp 是否在线。初始为 false，首次探测成功后置 true。</summary>
        public bool Online { get; private set; }

        /// <summary>宿主版本号；离线时为空字符串。</summary>
        public string HostVersion { get; private set; } = string.Empty;

        /// <summary>宿主当前登记的路由条数；离线时为 0。</summary>
        public int RouteCount { get; private set; }

        /// <summary>
        /// 会话状态发生变化时触发。
        /// </summary>
        /// <remarks>
        /// 在<b>后台线程</b>上触发。订阅方（视图）必须自行切回 UI 线程，
        /// 本服务刻意不引用 Dispatcher——那会把 WPF 的线程模型焊进服务层，
        /// 使它无法在测试或其他宿主里复用。
        /// </remarks>
        public event Action Changed;

        // ====================================================================
        // 生命周期
        // ====================================================================

        /// <summary>
        /// 启动后台健康轮询。重复调用无副作用。
        /// </summary>
        public void Start()
        {
            // 已启动或已释放则直接返回，避免起第二个轮询循环
            if (_pollTask != null || Volatile.Read(ref _disposed) != 0)
                return;

            _pollTask = Task.Run(() => PollLoopAsync(_cts.Token));
        }

        /// <summary>
        /// 停止轮询并释放取消源。幂等。
        /// </summary>
        public void Dispose()
        {
            // 用 Interlocked 保证多次 Dispose 只执行一次实际释放
            if (Interlocked.Exchange(ref _disposed, 1) != 0)
                return;

            try
            {
                // 取消令牌，轮询循环在下一个检查点退出
                _cts.Cancel();
                _cts.Dispose();
            }
            catch (ObjectDisposedException)
            {
                // 退出阶段的竞态：令牌源已被释放，无需处理
            }
        }

        // ====================================================================
        // 轮询循环
        // ====================================================================

        /// <summary>
        /// 周期性探测宿主健康状态，直到令牌取消。
        /// </summary>
        /// <remarks>
        /// 必须可取消：无取消的 while(true) 会在应用退出后继续存活，
        /// 向已释放的 gRPC 通道发请求。
        /// </remarks>
        private async Task PollLoopAsync(CancellationToken ct)
        {
            while (!ct.IsCancellationRequested)
            {
                try
                {
                    // HealthAsync 内部已设 5 秒截止时间，且不会因网络问题抛出
                    HealthResultDto health = await _client.HealthAsync(ct).ConfigureAwait(false);
                    Apply(health.Ok, health.HostVersion, health.RouteCount);
                }
                catch (OperationCanceledException)
                {
                    // 应用退出触发的取消：正常结束，不记日志
                    return;
                }
                catch (ObjectDisposedException)
                {
                    // 通道已随应用退出释放，轮询仍在途：视为结束
                    return;
                }
                catch (Exception ex)
                {
                    // 其余异常标记离线但不中断轮询——宿主可能稍后恢复
                    _log?.Warn("Host", "健康检查失败: " + ex.Message);
                    Apply(false, string.Empty, 0);
                }

                try
                {
                    await Task.Delay(TimeSpan.FromSeconds(PollIntervalSeconds), ct)
                              .ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    return;
                }
                catch (ObjectDisposedException)
                {
                    // 令牌源已在 Dispose 中释放
                    return;
                }
            }
        }

        /// <summary>
        /// 写入最新状态，仅在确有变化时发出事件。
        /// </summary>
        /// <remarks>
        /// 过滤掉无变化的探测结果很重要：轮询每 10 秒一次，
        /// 若每次都触发事件，视图会被无意义的重绘刷屏。
        /// </remarks>
        private void Apply(bool online, string version, int routeCount)
        {
            string normalizedVersion = version ?? string.Empty;

            // 三个字段全部相同则本次探测无新信息
            bool unchanged = Online      == online
                          && HostVersion == normalizedVersion
                          && RouteCount  == routeCount;
            if (unchanged)
                return;

            Online      = online;
            HostVersion = normalizedVersion;
            RouteCount  = routeCount;

            // 事件在后台线程上触发，订阅方负责切线程
            Changed?.Invoke();
        }
    }
}
