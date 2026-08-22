#nullable disable

// -----------------------------------------------------------------------------
// 文件: Views/Pages/MesMonitor/Controls/MesFlowTrack.xaml.cs
// 层级: UI 层 — MES 监控页子控件
// 作用: 产线流程轨迹展示；当前仅为静态布局。
// -----------------------------------------------------------------------------

using System.Windows.Controls;

namespace CommunicationKernel.UI.Wpf.Views.Pages.MesMonitor.Controls {
    /// <summary>
    /// 产线流程轨迹展示控件（MesFlowTrack.xaml 的交互逻辑）。
    /// 当前仅为静态展示控件，无额外交互逻辑。
    /// </summary>
    public partial class MesFlowTrack : UserControl {
        public MesFlowTrack () {
            // 解析 XAML，构建视觉树
            InitializeComponent();
        }
    }
}
