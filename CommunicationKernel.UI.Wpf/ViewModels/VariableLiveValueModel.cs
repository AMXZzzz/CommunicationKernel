#nullable disable

// -----------------------------------------------------------------------------
// 文件: ViewModels/VariableLiveValueModel.cs
// 层级: UI 层 — WPF 呈现层模型
// 作用: 订阅 VariablePollingService 的读取结果，在 UI 线程上写回 VariableItem。
//
// 这是变量侧唯一允许调用 Dispatcher 的地方，与设备侧的 DeviceListModel 对称。
//   纪律 4：服务只发布事件，切线程是订阅方的责任。
//   轮询服务此前自己在三处调 Application.Current.Dispatcher 写
//   VariableItem.LastValue / LastError，那让它离开 WPF 就无法使用。
//
// 为什么不做成 ViewModel：
//   它不面向某个页面，也没有可绑定的属性——它只是把后台结果安全地
//   送进已有的 VariableItem。变量表本身由 IVariableService 持有，
//   本类不复制一份，避免出现两个数据源。
// -----------------------------------------------------------------------------

using System;
using System.Windows;
using System.Windows.Threading;
using CommunicationKernel.UI.Wpf.Core.Interfaces;
using CommunicationKernel.UI.Wpf.Core.Models;
using CommunicationKernel.UI.Wpf.Services;

namespace CommunicationKernel.UI.Wpf.ViewModels
{
    /// <summary>
    /// 把轮询结果落到界面绑定的 <see cref="VariableItem"/> 上。
    /// </summary>
    /// <remarks>
    /// 构造即订阅。必须在 <see cref="VariablePollingService.Start"/> <b>之前</b>
    /// 从 DI 解析出来——事件是即发即忘的，没有订阅者时那一轮结果直接丢失。
    /// 见 <c>App.xaml.cs</c> 启动顺序。
    /// </remarks>
    public sealed class VariableLiveValueModel
    {
        /// <summary>变量表，按 Id 查找目标条目。</summary>
        private readonly IVariableService _variables;

        /// <param name="variables">变量服务。</param>
        /// <param name="polling">轮询服务，事件来源。</param>
        public VariableLiveValueModel(IVariableService variables, VariablePollingService polling)
        {
            _variables = variables ?? throw new ArgumentNullException(nameof(variables));
            if (polling == null) throw new ArgumentNullException(nameof(polling));

            polling.ValueUpdated += OnValueUpdated;
        }

        /// <summary>收到一次读取结果：切到 UI 线程后写回对应变量。</summary>
        private void OnValueUpdated(VariableReadUpdate update)
        {
            if (update == null || string.IsNullOrEmpty(update.VariableId)) return;

            OnUi(() =>
            {
                // 变量可能已被删除——轮询循环退出前的最后一条结果常常晚于删除动作到达
                VariableItem item = Find(update.VariableId);
                if (item == null) return;

                // Value 为 null 表示"本次不改动已显示的值"。
                // 读失败时保留上一次读到的数字，操作员才看得出这个值停在什么时候；
                // 一律清空反而丢掉排查线索。
                if (update.Value != null)
                    item.LastValue = update.Value;

                item.LastError = update.Error ?? string.Empty;
            });
        }

        /// <summary>按 Id 在变量表快照里查找；未找到返回 null。</summary>
        private VariableItem Find(string id)
        {
            foreach (VariableItem v in _variables.Variables)
            {
                if (v != null && v.Id == id) return v;
            }
            return null;
        }

        /// <summary>
        /// 把动作放到 UI 线程执行；已在 UI 线程则就地执行。
        /// </summary>
        /// <remarks>
        /// <para>
        /// <see cref="VariableItem"/> 绑定在变量表格上，属性变更通知必须发生在 UI 线程。
        /// </para>
        /// <para>
        /// <c>Application.Current</c> 在应用退出途中变为 null，此时静默丢弃是正确的：
        /// 界面都没了，更新它没有意义。
        /// </para>
        /// </remarks>
        private static void OnUi(Action action)
        {
            Application app = Application.Current;
            if (app == null) return;

            Dispatcher dispatcher = app.Dispatcher;
            if (dispatcher.CheckAccess())
                action();
            else
                dispatcher.InvokeAsync(action);
        }
    }
}
