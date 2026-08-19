using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Threading;
using System.Threading.Tasks;
using CommunicationDebuggingTools.Core.Models;

namespace CommunicationDebuggingTools.Core.Interfaces {

    /// <summary>
    /// 变量配置与读写。设备连接由 <see cref="IDeviceService"/> 管理。
    ///
    /// ReadAsync / WriteAsync 从 Task&lt;bool&gt; 改为 Task&lt;OperationResult&gt;：
    ///   - 调用方无需再访问 VariableItem.LastError 获取失败原因。
    ///   - OperationErrorCode 支持按错误类型分支处理（断线重连、仅记录等）。
    ///   - VariableItem.LastError / Quality 仍同步更新，供 UI 绑定实时状态。
    /// </summary>
    public interface IVariableService {

        ObservableCollection<VariableItem> Variables { get; }

        void Load ();
        void Save ();

        void Add (VariableItem item);
        void Update (VariableItem item);
        void Remove (string id);

        /// <summary>
        /// 读一点。结果同步写入 VariableItem.LastValue / Quality / LastError；
        /// 同时通过返回值告知调用方失败原因，无需二次访问 VariableItem。
        /// </summary>
        Task<OperationResult> ReadAsync (string variableId, CancellationToken cancellationToken);

        /// <summary>
        /// 写一点。成功后 VariableItem.LastValue 更新为写入值。
        /// </summary>
        Task<OperationResult> WriteAsync (string variableId, object value, CancellationToken cancellationToken);

        /// <summary>按设备批量读所有可读变量。</summary>
        Task ReadByDeviceAsync (string deviceId, CancellationToken cancellationToken);
    }
}