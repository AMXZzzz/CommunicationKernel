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
        /// <summary>「导入」被点击。</summary>
        public event Action ImportClicked;

        /// <summary>「导出」被点击。</summary>
        public event Action ExportClicked;

        /// <summary>「实时刷新」被点击。开关状态由页面维护，本控件不记。</summary>
        public event Action LiveRefreshClicked;

        /// <summary>构造：解析 XAML，构建视觉树。</summary>
        public VariableToolBar () {
            // 解析 XAML，构建视觉树
            InitializeComponent();
        }

        // ============================================================================
        // 按钮 → 事件
        // ============================================================================

        /// <summary>「导入」按钮。</summary>
        /// <param name="sender">事件源。</param>
        /// <param name="e">事件参数。</param>
        private void BtnImport_Click (object sender, RoutedEventArgs e) =>
            ImportClicked?.Invoke();

        /// <summary>「导出」按钮。</summary>
        /// <param name="sender">事件源。</param>
        /// <param name="e">事件参数。</param>
        private void BtnExport_Click (object sender, RoutedEventArgs e) =>
            ExportClicked?.Invoke();

        /// <summary>「实时刷新」按钮。</summary>
        /// <param name="sender">事件源。</param>
        /// <param name="e">事件参数。</param>
        private void BtnLive_Click (object sender, RoutedEventArgs e) =>
            LiveRefreshClicked?.Invoke();
    }
}
