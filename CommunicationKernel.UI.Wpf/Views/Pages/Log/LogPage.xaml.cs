#nullable disable

// -----------------------------------------------------------------------------
// 文件: Views/Pages/Log/LogPage.xaml.cs
// 层级: UI 层 — 通讯日志页 code-behind
// 作用: 绑定 LogPageViewModel 到 ListBox，处理清除按钮点击；
//       新条目由 LogPageViewModel 内部通过 IAppLogger.EntryAdded 追加，
//       本 Page 只做 UI 滚动和按钮路由。
// -----------------------------------------------------------------------------

using System;
using System.Collections.Specialized;
using System.Windows;
using System.Windows.Controls;
using CommunicationKernel.UI.Wpf.ViewModels;

namespace CommunicationKernel.UI.Wpf.Views.Pages.Log {

    /// <summary>通讯日志页 code-behind。</summary>
    public partial class LogPage : Page {

        // -------------------------------------------------------------------------
        // 私有字段
        // -------------------------------------------------------------------------

        /// <summary>日志页 ViewModel（由 DI 注入）。</summary>
        private readonly LogPageViewModel _vm;

        // -------------------------------------------------------------------------
        // 构造函数
        // -------------------------------------------------------------------------

        /// <param name="viewModel">从 DI 容器注入的 LogPageViewModel。</param>
        public LogPage(LogPageViewModel viewModel) {
            _vm = viewModel ?? throw new ArgumentNullException(nameof(viewModel));
            InitializeComponent();
            DataContext = _vm;

            // 绑定过滤后的日志条目到列表控件
            listLog.ItemsSource = _vm.FilteredEntries;

            // 新条目加入时自动滚动到最新行
            _vm.FilteredEntries.CollectionChanged += OnEntriesChanged;

            // 页面卸载时取消订阅，防止内存泄漏
            Unloaded += (_, __) => _vm.FilteredEntries.CollectionChanged -= OnEntriesChanged;
        }

        // -------------------------------------------------------------------------
        // 事件处理
        // -------------------------------------------------------------------------

        /// <summary>日志集合变化时，若有新增条目则自动滚动到最后一行。</summary>
        private void OnEntriesChanged(object sender, NotifyCollectionChangedEventArgs e) {
            // 只处理新增条目（Add 操作），其他操作（Clear/Reset）不滚动
            if (e.Action != NotifyCollectionChangedAction.Add)
                return;

            Dispatcher.BeginInvoke(new Action(() => {
                // 若集合非空则滚动到最末行
                if (_vm.FilteredEntries.Count > 0)
                    listLog.ScrollIntoView(_vm.FilteredEntries[_vm.FilteredEntries.Count - 1]);
            }));
        }

        /// <summary>清除按钮：调用 ViewModel 的 ClearCommand。</summary>
        private void BtnClear_Click(object sender, RoutedEventArgs e) {
            // CanExecute 检查防止在命令不可用时执行
            if (_vm.ClearCommand?.CanExecute(null) == true)
                _vm.ClearCommand.Execute(null);
        }
    }
}
