#nullable disable

// -----------------------------------------------------------------------------
// 文件: Views/Pages/MesMonitor/Controls/MESControlsTitleBar.xaml.cs
// 层级: UI 层 — MES 监控页子控件
// 作用: 监控页顶部标题栏；当前仅为静态展示。
// -----------------------------------------------------------------------------

using System.Windows.Controls;

namespace CommunicationKernel.UI.Wpf.Views.Pages.MesMonitor.Controls {
    /// <summary>
    /// MES 监控页顶部标题栏控件（MESControlsTitleBar.xaml 的交互逻辑）。
    /// 当前仅为静态展示控件，无额外交互逻辑。
    /// </summary>
    public partial class MESControlsTitleBar : UserControl {
        public MESControlsTitleBar () {
            // 解析 XAML，构建视觉树
            InitializeComponent();
        }
    }
}
