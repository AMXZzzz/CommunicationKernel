using System;
using System.Windows;
using System.Windows.Controls;

namespace CommunicationDebuggingTools.Views.Controls {
    public partial class NavSidebar : UserControl {
        /// <summary>选中导航项时，参数为页面 Type（来自 RadioButton.Tag）</summary>
        public event Action<Type> NavigateRequested;

        public NavSidebar () {
            InitializeComponent();
        }

        private void Nav_Checked (object sender, RoutedEventArgs e) {
            RadioButton rb = sender as RadioButton;
            if (rb == null)
                return;

            Type pageType = rb.Tag as Type;
            if (pageType == null || !typeof(Page).IsAssignableFrom(pageType))
                return;

            if (NavigateRequested != null)
                NavigateRequested(pageType);
        }
    }
}