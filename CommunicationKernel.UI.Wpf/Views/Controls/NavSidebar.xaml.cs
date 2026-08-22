#nullable disable

// -----------------------------------------------------------------------------
// 文件: Views/Controls/NavSidebar.xaml.cs
// 层级: UI 层 — WPF 通用控件
// 作用: 左侧导航栏；选中 RadioButton 后把 Tag 中的 Page 类型交给宿主导航。
// -----------------------------------------------------------------------------

using System;
using System.Windows;
using System.Windows.Controls;

namespace CommunicationKernel.UI.Wpf.Views.Controls {
    public partial class NavSidebar : UserControl {
        /// <summary>选中导航项时，参数为页面 Type（来自 RadioButton.Tag）</summary>
        public event Action<Type> NavigateRequested;

        public NavSidebar () {
            // 解析 XAML，构建视觉树
            InitializeComponent();
        }

        // ============================================================================
        // 导航选中
        // ============================================================================

        private void Nav_Checked (object sender, RoutedEventArgs e) {
            RadioButton rb = sender as RadioButton;
            // 非 RadioButton 来源直接忽略
            if (rb == null)
                return;

            Type pageType = rb.Tag as Type;
            // Tag 必须是可导航的 Page 子类
            if (pageType == null || !typeof(Page).IsAssignableFrom(pageType))
                return;

            if (NavigateRequested != null)
                NavigateRequested(pageType);
        }
    }
}
