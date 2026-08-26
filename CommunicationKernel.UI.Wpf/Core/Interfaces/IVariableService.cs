#nullable disable

// -----------------------------------------------------------------------------
// 文件: Core/Interfaces/IVariableService.cs
// 层级: UI 层 — WPF 核心接口
// 作用: 变量管理抽象，供 VariablePageViewModel 注入；写入走 gRPC WriteAsync。
// -----------------------------------------------------------------------------

using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using CommunicationKernel.EngineHost.Sdk;
using CommunicationKernel.UI.Wpf.Core.Models;

namespace CommunicationKernel.UI.Wpf.Core.Interfaces
{
    /// <summary>
    /// 变量管理服务接口。
    /// 提供本地变量列表的增删改查，以及通过 gRPC 执行写入操作。
    /// </summary>
    public interface IVariableService
    {
        /// <summary>
        /// 变量列表发生变化（Add / Update / Remove）时触发。
        /// 轮询服务订阅此事件以同步启停轮询任务。
        /// 回调可能在任意线程触发，订阅方需自行切换线程。
        /// </summary>
        event Action VariablesChanged;

        /// <summary>
        /// 当前变量列表的只读视图。
        /// 调用方不得修改返回的列表，需修改时通过 Add/Update/Remove 方法进行。
        /// </summary>
        IReadOnlyList<VariableItem> Variables { get; }

        /// <summary>
        /// 添加新变量到本地列表。
        /// 若 item.Id 为空，实现应自动生成 Guid。
        /// </summary>
        /// <param name="item">要添加的变量定义。</param>
        void Add(VariableItem item);

        /// <summary>
        /// 更新已有变量的定义。
        /// 根据 item.Id 查找并替换，若 Id 不存在则忽略。
        /// </summary>
        /// <param name="item">已修改的变量定义，Id 须与现有变量匹配。</param>
        void Update(VariableItem item);

        /// <summary>
        /// 从本地列表移除指定变量。
        /// </summary>
        /// <param name="id">要移除的变量 Id（Guid 字符串）。</param>
        void Remove(string id);

        /// <summary>
        /// 向 PLC 写入变量值，通过 gRPC WriteAsync 完成。
        /// </summary>
        /// <param name="id">目标变量的 Id。</param>
        /// <param name="value">要写入的值（类型由变量的 DataType 字段决定）。</param>
        /// <param name="ct">取消令牌。</param>
        /// <returns>操作结果，Success = true 表示写入成功。</returns>
        Task<HostOperationResult> WriteAsync(string id, object value, CancellationToken ct);
    }
}
