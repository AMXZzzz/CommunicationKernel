#nullable disable

// -----------------------------------------------------------------------------
// 文件: RouteReconcileGate.cs
// 层级: 客户端层 — 重注册的并发闸门
// 作用: 把「同一路由上并发的重注册请求」合并成一次，并施加最小重试间隔。
//
// 为什么单独成类：
//   宿主重启后，一台设备上挂着的几十个变量会在同一瞬间全部收到 RouteNotFound。
//   没有合并，宿主会在一瞬间收到几十次同一条路由的 RegisterRoute；
//   没有节流，PLC 拔线时每个轮询周期都会再打一次，把失败放大成持续风暴。
//   这两件事的正确性只与「时序」有关，与 WPF、gRPC 都无关，
//   放在这里才能脱离界面直接测。
// -----------------------------------------------------------------------------

using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace CommunicationKernel.Hosting.Sdk
{
    /// <summary>
    /// 按键合并并发调用、并限制最小重试间隔的闸门。线程安全。
    /// </summary>
    public sealed class RouteReconcileGate
    {
        // 同一路由两次实际调用之间的最小间隔
        private readonly TimeSpan _minInterval;

        // 时间源：生产用 UtcNow，测试注入假时钟即可验证节流
        private readonly Func<DateTime> _clock;

        // 保护 _inflight / _lastAttempt 的互斥锁；检查与登记必须在同一把锁内
        private readonly object _lock = new object();

        /// <summary>正在进行中的调用，Key = 路由 ID。</summary>
        private readonly Dictionary<string, Task<bool>> _inflight
            = new Dictionary<string, Task<bool>>(StringComparer.OrdinalIgnoreCase);

        /// <summary>各路由上一次实际发起调用的时刻，Key = 路由 ID。</summary>
        private readonly Dictionary<string, DateTime> _lastAttempt
            = new Dictionary<string, DateTime>(StringComparer.OrdinalIgnoreCase);

        /// <param name="minInterval">
        /// 同一路由两次实际调用之间的最小间隔。窗口内的请求直接判定为失败，
        /// 不发起调用。
        /// </param>
        /// <param name="clock">
        /// 时间源，默认 <see cref="DateTime.UtcNow"/>。
        /// 测试注入假时钟即可验证节流，无需真的等待。
        /// </param>
        public RouteReconcileGate(TimeSpan minInterval, Func<DateTime> clock = null)
        {
            // 负间隔没有意义，钳到零，避免时间比较反转
            _minInterval = minInterval < TimeSpan.Zero ? TimeSpan.Zero : minInterval;

            // 未注入时钟则用 UTC；测试可注入假时钟，不必真等 minInterval
            _clock       = clock ?? (() => DateTime.UtcNow);
        }

        // ============================================================================
        // 入闸
        // ============================================================================

        /// <summary>
        /// 对 <paramref name="routeId"/> 执行一次受闸门约束的调用。
        /// </summary>
        /// <param name="routeId">路由 ID。</param>
        /// <param name="operation">实际的重注册操作。</param>
        /// <returns>
        /// 操作结果；处于节流窗口内时不调用 <paramref name="operation"/> 并返回 false。
        /// 同一路由上并发调用时，后来者复用先到者的任务。
        /// </returns>
        public Task<bool> RunAsync(string routeId, Func<Task<bool>> operation)
        {
            // 空路由 ID 无法入闸，直接失败，避免污染字典
            if (string.IsNullOrWhiteSpace(routeId)) return Task.FromResult(false);

            // 无实际操作可执行，同样直接失败
            if (operation is null) return Task.FromResult(false);

            // 检查在途、节流、登记必须在同一把锁下，否则两个线程会各发一次
            lock (_lock)
            {
                // 分支1：已有在途调用——复用它。
                // 注意必须在 lock 内检查并返回：否则两个线程可能同时判定"无在途"
                // 而各自发起一次调用，合并就失效了。
                if (_inflight.TryGetValue(routeId, out Task<bool> existing))
                    return existing;

                // 读取当前时刻，与上次发起时间比较
                DateTime now = _clock();

                // 分支2：距上次调用不足最小间隔——直接判失败，不发起调用
                if (_lastAttempt.TryGetValue(routeId, out DateTime last)
                    && now - last < _minInterval)
                {
                    return Task.FromResult(false);
                }

                // 记录本次发起时刻，后续请求据此节流
                _lastAttempt[routeId] = now;

                // 启动实际调用并登记在途，后来者会命中分支1
                Task<bool> attempt = InvokeAsync(routeId, operation);
                _inflight[routeId] = attempt;

                // 返回在途任务：调用方 await 的是同一份结果
                return attempt;
            }
        }

        // ============================================================================
        // 执行并摘除在途
        // ============================================================================

        /// <summary>执行操作，无论成败都把自己从在途表摘掉。</summary>
        private async Task<bool> InvokeAsync(string routeId, Func<Task<bool>> operation)
        {
            try
            {
                // 让出线程，确保本方法不会在 RunAsync 的 lock 内同步跑完。
                // 否则 finally 的摘除会先于 _inflight[routeId] 的写入执行，
                // 留下一条永远摘不掉的在途记录，该路由此后再也无法重注册。
                await Task.Yield();

                // 真正发起重注册（通常是 HostingClient.RegisterRouteAsync）
                return await operation().ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                // 取消视为本轮失败，不向外抛，以免打断调用方的重试循环
                return false;
            }
            finally
            {
                // 无论成败都释放在途槽位，否则该路由再也进不了闸门
                lock (_lock)
                {
                    _inflight.Remove(routeId);
                }
            }
        }
    }
}
