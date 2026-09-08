#nullable disable

// -----------------------------------------------------------------------------
// 文件: Core/Models/VariableReadUpdate.cs
// 层级: UI 层 — WPF 核心模型
// 作用: VariablePollingService 每轮读取之后发布的结果载体。
//
// 为什么不直接改 VariableItem：
//   VariableItem 绑定在界面上。轮询循环跑在后台线程，直接写它的属性就必须
//   自己切回 UI 线程——那正是纪律 4 禁止的（服务只发事件，切线程是订阅方的事）。
//   服务因此改为只产出「哪个变量、读到什么、错在哪」这三项纯数据，
//   由 ViewModels/VariableLiveValueModel 在 UI 线程上落到 VariableItem。
//
//   与设备侧的 DeviceStatusUpdate 是同一套做法，见 DeviceServiceEvents.cs。
// -----------------------------------------------------------------------------

namespace CommunicationKernel.UI.Wpf.Core.Models
{
    /// <summary>一次变量读取的结果。</summary>
    /// <remarks>
    /// 只带 Id 与文本，不带 <see cref="VariableItem"/> 引用：
    /// 让订阅方自己在 UI 线程上按 Id 查找，避免服务持有界面正在绑定的对象。
    /// </remarks>
    public sealed class VariableReadUpdate
    {
        /// <summary>变量 Id。</summary>
        public string VariableId { get; set; }

        /// <summary>
        /// 供显示的读取值；<c>null</c> 表示本次不改动已显示的值。
        /// </summary>
        /// <remarks>
        /// 失败时刻意传 <c>null</c> 而不是空字符串：读失败不等于值失效，
        /// 保留上一次读到的数字、只把错误文本亮出来，操作员才看得出
        /// 「这个值停在什么时候」。清成空白反而丢掉了排查线索。
        /// </remarks>
        public string Value { get; set; }

        /// <summary>
        /// 错误文本；<see cref="string.Empty"/> 表示本次成功、应清除既有错误。
        /// </summary>
        public string Error { get; set; }
    }
}
