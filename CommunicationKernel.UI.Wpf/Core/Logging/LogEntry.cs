#nullable disable

// -----------------------------------------------------------------------------
// 文件: Core/Logging/LogEntry.cs
// 层级: UI 层 — WPF 核心日志模型
// 作用: 一条不可变日志记录，由 IAppLogger 写入循环缓冲区后供日志页绑定。
// -----------------------------------------------------------------------------

using System;

namespace CommunicationKernel.UI.Wpf.Core.Logging
{
    /// <summary>
    /// 单条应用日志记录，不可变。
    /// 由 <see cref="IAppLogger"/> 实现在写入时创建，消费方只读。
    /// </summary>
    public sealed class LogEntry
    {
        /// <summary>日志产生的本地时间（DateTime.Now），在构造时捕获。</summary>
        public DateTime Timestamp { get; }

        /// <summary>
        /// 日志级别字符串。
        /// 合法值: "INFO"、"WARN"、"ERROR"、"DEBUG"。
        /// </summary>
        public string Level { get; }

        /// <summary>
        /// 日志类别，通常为产生日志的类名或功能模块名。
        /// 例如 "GrpcDeviceService"、"LocalVariableStore"。
        /// </summary>
        public string Category { get; }

        /// <summary>日志消息正文，描述发生的事件。</summary>
        public string Message { get; }

        /// <summary>
        /// 构造一条日志记录，时间戳自动设为当前本地时间。
        /// </summary>
        /// <param name="level">日志级别字符串，如 "INFO"。</param>
        /// <param name="category">日志类别。</param>
        /// <param name="message">消息正文。</param>
        public LogEntry(string level, string category, string message)
        {
            // 捕获当前时间作为不可变时间戳
            Timestamp = DateTime.Now;
            // 空入参归一为空串，避免后续格式化出现 null
            Level     = level    ?? string.Empty;
            Category  = category ?? string.Empty;
            Message   = message  ?? string.Empty;
        }

        /// <summary>
        /// 返回格式化字符串，用于调试输出或文本导出。
        /// 格式: "[HH:mm:ss] [LEVEL] [Category] Message"
        /// </summary>
        public override string ToString()
        {
            // 格式化为可读的单行文本
            return string.Format("[{0:HH:mm:ss}] [{1}] [{2}] {3}",
                Timestamp, Level, Category, Message);
        }
    }
}
