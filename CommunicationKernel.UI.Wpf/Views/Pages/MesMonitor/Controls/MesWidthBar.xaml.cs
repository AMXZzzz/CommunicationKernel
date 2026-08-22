#nullable disable

// -----------------------------------------------------------------------------
// 文件: Views/Pages/MesMonitor/Controls/MesWidthBar.xaml.cs
// 层级: UI 层 — MES 监控页子控件
// 作用: PCB 轨道宽度条；当前仅为静态展示。
// -----------------------------------------------------------------------------

using System.Windows.Controls;

namespace CommunicationKernel.UI.Wpf.Views.Pages.MesMonitor.Controls {
    /// <summary>
    /// 幅宽/数值条形展示控件（MesWidthBar.xaml 的交互逻辑）。
    /// 当前仅为静态展示控件，无额外交互逻辑。
    /// </summary>
    public partial class MesWidthBar : UserControl {
        public MesWidthBar () {
            // 解析 XAML，构建视觉树
            InitializeComponent();
        }
    }
}
