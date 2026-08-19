using System;
using System.Windows;
using System.Windows.Controls;

namespace CommunicationDebuggingTools.Views.VariableConfigPage.Controls {
    /// <summary>变量配置顶栏：导入 / 导出 / 实时刷新（对齐 variable_config 模板）。</summary>
    public partial class VariableToolBar : UserControl {
        public event Action ImportClicked;
        public event Action ExportClicked;
        public event Action LiveRefreshClicked;

        public VariableToolBar () {
            InitializeComponent();
        }

        private void BtnImport_Click (object sender, RoutedEventArgs e) =>
            ImportClicked?.Invoke();

        private void BtnExport_Click (object sender, RoutedEventArgs e) =>
            ExportClicked?.Invoke();

        private void BtnLive_Click (object sender, RoutedEventArgs e) =>
            LiveRefreshClicked?.Invoke();
    }
}