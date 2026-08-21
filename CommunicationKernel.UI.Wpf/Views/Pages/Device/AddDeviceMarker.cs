#nullable disable

namespace CommunicationKernel.UI.Wpf.Views.Pages.Device {
    /// <summary>
    /// 设备列表末尾的“添加”占位对象（非真实设备）。
    /// 采用单例模式提供唯一实例，供 UI 绑定的集合在末尾添加一个“占位卡片”，
    /// XAML 中的 DataTemplate 会根据该类型匹配到 AddDeviceCard 控件而非普通设备卡片。
    /// </summary>
    public sealed class AddDeviceMarker {
        /// <summary>全局唯一占位实例。</summary>
        public static readonly AddDeviceMarker Instance = new AddDeviceMarker();

        private AddDeviceMarker () {
        }
    }
}