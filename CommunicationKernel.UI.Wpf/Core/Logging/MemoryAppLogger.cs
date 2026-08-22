#nullable disable

// -----------------------------------------------------------------------------
// 文件: Core/Logging/MemoryAppLogger.cs
// 层级: UI 层 — WPF 核心日志实现
// 作用: IAppLogger 的内存循环缓冲区实现，最多保留 500 条，超出丢弃最旧条目。
// -----------------------------------------------------------------------------

using System;
using System.Collections.Generic;

namespace CommunicationKernel.UI.Wpf.Core.Logging
{
    /// <summary>
    /// 基于内存循环缓冲区的 <see cref="IAppLogger"/> 实现。
    /// 线程安全：所有对 _entries 的读写均在 lock 内完成。
    /// 单例生命周期：由 DI 容器管理，应用退出时自动释放。
    /// </summary>
    public sealed class MemoryAppLogger : IAppLogger
    {
        // ============================================================================
        // 常量与字段
        // ============================================================================

        /// <summary>循环缓冲区最大容量，超出后移除最旧的条目。</summary>
        private const int MaxCapacity = 500;

        /// <summary>日志条目列表，用作循环缓冲区。对此字段的所有访问须在 lock 内。</summary>
        private readonly List<LogEntry> _entries = new List<LogEntry>();

        /// <summary>用于保护 _entries 的同步锁对象。</summary>
        private readonly object _lock = new object();

        // ============================================================================
        // 事件
        // ============================================================================

        /// <summary>
        /// 每写入一条日志条目后触发。
        /// 事件在调用线程上触发（非 UI 线程），订阅者若需更新 UI 须自行切回 UI 线程。
        /// </summary>
        public event Action<LogEntry> EntryAdded;

        // ============================================================================
        // IAppLogger 实现
        // ============================================================================

        /// <summary>写入 INFO 级别日志。</summary>
        /// <param name="category">日志类别。</param>
        /// <param name="message">消息正文。</param>
        public void Info(string category, string message)
        {
            // 构造 INFO 条目并追加到循环缓冲区
            Append(new LogEntry("INFO", category, message));
        }

        /// <summary>写入 WARN 级别日志。</summary>
        /// <param name="category">日志类别。</param>
        /// <param name="message">消息正文。</param>
        public void Warn(string category, string message)
        {
            // 构造 WARN 条目并追加到循环缓冲区
            Append(new LogEntry("WARN", category, message));
        }

        /// <summary>
        /// 写入 ERROR 级别日志，可选附加异常信息。
        /// </summary>
        /// <param name="category">日志类别。</param>
        /// <param name="message">消息正文。</param>
        /// <param name="ex">可选异常对象；非 null 时将异常类型和消息附加到正文后。</param>
        public void Error(string category, string message, Exception ex = null)
        {
            // 有异常时把类型和消息拼进正文，便于日志页直接阅读
            string fullMessage = ex != null
                ? string.Format("{0} [{1}: {2}]", message, ex.GetType().Name, ex.Message)
                : message;

            // 构造 ERROR 条目并追加到循环缓冲区
            Append(new LogEntry("ERROR", category, fullMessage));
        }

        /// <summary>写入 DEBUG 级别日志。</summary>
        /// <param name="category">日志类别。</param>
        /// <param name="message">消息正文。</param>
        public void Debug(string category, string message)
        {
            // 构造 DEBUG 条目并追加到循环缓冲区
            Append(new LogEntry("DEBUG", category, message));
        }

        /// <summary>
        /// 获取当前缓冲区内所有条目的只读快照，顺序从旧到新。
        /// </summary>
        /// <returns>日志条目只读列表。</returns>
        public IReadOnlyList<LogEntry> GetRecent()
        {
            lock (_lock)
            {
                // 返回列表副本，避免调用方持有内部列表引用造成线程安全问题
                return _entries.ToArray();
            }
        }

        /// <summary>
        /// 清空所有已记录的日志条目。
        /// </summary>
        public void Clear()
        {
            lock (_lock)
            {
                // 清空内部列表（日志页会同步清空自己的显示集合）
                _entries.Clear();
            }
        }

        // ============================================================================
        // 私有辅助方法
        // ============================================================================

        /// <summary>
        /// 将条目写入循环缓冲区并触发 EntryAdded 事件。
        /// 此方法是所有 Info/Warn/Error/Debug 的公共路径。
        /// </summary>
        /// <param name="entry">已构造好的日志条目。</param>
        private void Append(LogEntry entry)
        {
            lock (_lock)
            {
                // 追加到列表末尾
                _entries.Add(entry);

                // 超出容量时丢掉最旧条目，保持循环缓冲区语义
                if (_entries.Count > MaxCapacity)
                    _entries.RemoveAt(0);
            }

            // 在 lock 外触发事件，避免在锁内调用外部代码导致死锁
            EntryAdded?.Invoke(entry);
        }
    }
}
