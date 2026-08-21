#nullable disable

// 文件: Core/Logging/IAppLogger.cs
// 层级: UI层 — 核心日志接口
// 作用: 定义应用日志记录器的抽象接口，供 ViewModel 和服务层依赖注入使用。
//       实现类（MemoryAppLogger）将条目存入循环缓冲区并触发 EntryAdded 事件。

using System;
using System.Collections.Generic;

namespace CommunicationKernel.UI.Wpf.Core.Logging
{
    /// <summary>
    /// 应用内日志记录器接口。
    /// ViewModel 通过此接口写入日志，LogPageViewModel 通过 EntryAdded 事件订阅显示。
    /// </summary>
    public interface IAppLogger
    {
        /// <summary>
        /// 每当有新的日志条目写入时触发。
        /// 订阅者（通常是 LogPageViewModel）在此事件中将条目追加到 UI 列表。
        /// </summary>
        event Action<LogEntry> EntryAdded;

        /// <summary>
        /// 写入 INFO 级别日志。
        /// 用于记录正常业务流程事件，例如设备注册成功。
        /// </summary>
        /// <param name="category">日志类别（通常为调用类的类名）。</param>
        /// <param name="message">消息正文。</param>
        void Info(string category, string message);

        /// <summary>
        /// 写入 WARN 级别日志。
        /// 用于记录非致命的异常情况，例如重试、超时。
        /// </summary>
        /// <param name="category">日志类别。</param>
        /// <param name="message">消息正文。</param>
        void Warn(string category, string message);

        /// <summary>
        /// 写入 ERROR 级别日志。
        /// 用于记录导致操作失败的错误，可附带异常对象。
        /// </summary>
        /// <param name="category">日志类别。</param>
        /// <param name="message">消息正文。</param>
        /// <param name="ex">可选的异常对象，为 null 时不附加异常信息。</param>
        void Error(string category, string message, Exception ex = null);

        /// <summary>
        /// 写入 DEBUG 级别日志。
        /// 用于开发调试，生产环境可过滤不显示。
        /// </summary>
        /// <param name="category">日志类别。</param>
        /// <param name="message">消息正文。</param>
        void Debug(string category, string message);

        /// <summary>
        /// 获取最近的日志条目列表（循环缓冲区当前内容的快照）。
        /// 返回的列表为只读，顺序从旧到新。
        /// </summary>
        /// <returns>最近的日志条目集合。</returns>
        IReadOnlyList<LogEntry> GetRecent();

        /// <summary>
        /// 清空所有已记录的日志条目。
        /// 通常由 UI 上的"清空日志"按钮触发。
        /// </summary>
        void Clear();
    }
}
