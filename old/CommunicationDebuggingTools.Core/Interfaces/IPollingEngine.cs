using System;
using System.Threading;

namespace CommunicationDebuggingTools.Core.Interfaces {

    /// <summary>
    /// 变量周期采集引擎契约。
    ///
    /// 职责：
    ///   按 <see cref="Models.VariableItem.ScanRateMs"/> 周期性调用协议读取，
    ///   将结果写回 VariableItem.LastValue / Quality，触发 INotifyPropertyChanged。
    ///
    /// 不在此接口的职责：
    ///   - 设备连接管理（由 IDeviceService 负责）
    ///   - 地址解析（由 Protocol 插件负责）
    ///   - 变量配置持久化（由 IVariableService 负责）
    ///
    /// 线程模型：
    ///   Start/Stop 在 UI 线程调用。
    ///   内部循环运行在后台线程，VariableItem 属性更新通过构造时捕获的
    ///   SynchronizationContext 回调到 UI 线程（WPF binding-safe）。
    /// </summary>
    public interface IPollingEngine {

        /// <summary>当前是否正在轮询。</summary>
        bool IsRunning { get; }

        /// <summary>
        /// 启动轮询。已运行时无副作用。
        /// 必须在 UI 线程调用（捕获 SynchronizationContext）。
        /// </summary>
        void Start ();

        /// <summary>
        /// 停止轮询并等待当前采集周期结束（最多等 5 秒）。
        /// 幂等，可重复调用。
        /// </summary>
        void Stop ();

        /// <summary>
        /// 某个变量完成一次采集后触发（无论成功或失败）。
        /// 在 UI 线程上触发，可直接操作绑定属性。
        /// 参数：variableId, 成功与否
        /// </summary>
        event Action<string, bool> CycleCompleted;
    }
}