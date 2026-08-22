#nullable disable

// -----------------------------------------------------------------------------
// 文件: Resources/Styles/VariableTableStyles.xaml.cs
// 层级: UI 层 — WPF 资源字典 code-behind
// 作用: VariableTable 样式资源字典的部分类，仅调用 InitializeComponent 加载 XAML。
// -----------------------------------------------------------------------------

using System.Windows;

namespace CommunicationKernel.UI.Wpf.Resources.Styles {
    /// <summary>
    /// 变量表（VariableTable）专用样式资源字典。
    /// 样式定义在同名 XAML 中，本文件只负责把资源加载进字典。
    /// </summary>
    public partial class VariableTableStyles : ResourceDictionary {
        /// <summary>构造时加载 XAML 中的 DataGrid / 单元格样式。</summary>
        public VariableTableStyles () {
            // 加载同名 XAML 资源，供变量配置页 DataGrid 引用
            InitializeComponent();
        }
    }
}
