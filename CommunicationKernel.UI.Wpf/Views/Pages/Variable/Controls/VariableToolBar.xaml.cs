#nullable disable

// -----------------------------------------------------------------------------
// 文件: Views/Pages/Variable/Controls/VariableToolBar.xaml.cs
// 层级: UI 层 — WPF Views
// 作用: 变量配置顶栏；导入/导出/实时刷新只发事件，业务由页面处理。
// -----------------------------------------------------------------------------

using System;
using System.Windows;
using System.Windows.Controls;

namespace CommunicationKernel.UI.Wpf.Views.Pages.Variable.Controls {
    /// <summary>变量配置顶栏：导入 / 导出 / 实时刷新（对齐 variable_config 模板）。</summary>
    public partial class VariableToolBar : UserControl {
        public event Action ImportClicked;
        public event Action ExportClicked;
        public event Action LiveRefreshClicked;

        public VariableToolBar () {
            // 解析 XAML，构建视觉树
            InitializeComponent();
        }

        // ============================================================================
        // 按钮 → 事件
        // ============================================================================

        private void BtnImport_Click (object sender, RoutedEventArgs e) =>
            ImportClicked?.Invoke();

        private void BtnExport_Click (object sender, RoutedEventArgs e) =>
            ExportClicked?.Invoke();

        private void BtnLive_Click (object sender, RoutedEventArgs e) =>
            LiveRefreshClicked?.Invoke();
    }
}
