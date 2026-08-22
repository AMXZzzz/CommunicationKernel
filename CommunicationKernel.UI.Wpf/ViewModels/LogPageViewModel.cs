#nullable disable

// -----------------------------------------------------------------------------
// 文件: ViewModels/LogPageViewModel.cs
// 层级: UI 层 — WPF 日志页 ViewModel
// 作用: 订阅 IAppLogger.EntryAdded，将日志转为可绑定项，支持级别/关键字过滤与清空。
// 调用链:
//   IAppLogger（任意服务层）→ EntryAdded
//     → LogPageViewModel.OnEntryAdded → FilteredEntries → ListBox / DataGrid
// -----------------------------------------------------------------------------

using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Windows;
using CommunicationKernel.UI.Wpf.Core.Logging;

namespace CommunicationKernel.UI.Wpf.ViewModels
{
    // ============================================================================
    // 单条日志条目 ViewModel
    // ============================================================================

    /// <summary>
    /// 日志列表中一行的显示数据。
    /// 不可变：由 LogPageViewModel 构造后加入集合，不支持修改。
    /// </summary>
    public sealed class LogEntryItemViewModel
    {
        /// <summary>日志产生时间，格式 HH:mm:ss。</summary>
        public string Time     { get; set; }

        /// <summary>日志级别显示字符串，例如 "INFO"、"WARN"、"ERROR"。</summary>
        public string Level    { get; set; }

        /// <summary>日志类别（通常为产生日志的类名）。</summary>
        public string Category { get; set; }

        /// <summary>日志消息正文。</summary>
        public string Message  { get; set; }

        /// <summary>
        /// 根据日志级别返回颜色字符串，绑定到 Foreground。
        /// ERROR → 红色，WARN → 黄色，INFO → 浅色，DEBUG → 灰色。
        /// </summary>
        public string LevelColor
        {
            get
            {
                // 按级别返回日志页 Foreground 颜色
                switch (Level)
                {
                    case "ERROR":
                        return "#ef4444"; // 红色
                    case "WARN":
                        return "#f59e0b"; // 黄色
                    case "INFO":
                        return "#e2e8f0"; // 浅灰白
                    default:
                        return "#6b7280"; // 灰色（DEBUG 等）
                }
            }
        }
    }

    // ============================================================================
    // 日志页 ViewModel
    // ============================================================================

    /// <summary>
    /// 通讯日志页的 ViewModel。
    /// 订阅 <see cref="IAppLogger.EntryAdded"/> 事件，动态追加日志到 <see cref="FilteredEntries"/>。
    /// 支持按级别和关键字过滤，以及清空日志。
    /// </summary>
    public sealed class LogPageViewModel : ViewModelBase
    {
        // ============================================================================
        // 常量与字段
        // ============================================================================

        /// <summary>UI 显示列表的最大条目数，超出时移除最旧的条目防止内存增长。</summary>
        private const int MaxDisplayEntries = 2000;

        /// <summary>应用日志记录器，本 ViewModel 订阅其 EntryAdded 事件。</summary>
        private readonly IAppLogger _logger;

        /// <summary>所有已接收的原始条目，用于重新过滤时遍历。</summary>
        private readonly List<LogEntryItemViewModel> _allEntries
            = new List<LogEntryItemViewModel>();

        // 过滤条件后备字段
        private string _filterLevel = "ALL";
        private string _filterText  = string.Empty;
        private bool   _autoScroll  = true;

        // ============================================================================
        // 公开属性
        // ============================================================================

        /// <summary>
        /// 过滤后的日志条目，绑定到 ListBox / DataGrid。
        /// 每次过滤条件改变或新条目到达时由 ViewModel 维护。
        /// </summary>
        public ObservableCollection<LogEntryItemViewModel> FilteredEntries { get; }
            = new ObservableCollection<LogEntryItemViewModel>();

        /// <summary>
        /// 日志级别过滤条件："ALL"、"INFO"、"WARN"、"ERROR"、"DEBUG"。
        /// 改变时自动重新计算 FilteredEntries。
        /// </summary>
        public string FilterLevel
        {
            get => _filterLevel;
            set
            {
                if (SetProperty(ref _filterLevel, value))
                {
                    // 级别条件改变，重新计算显示列表
                    ApplyFilter();
                }
            }
        }

        /// <summary>
        /// 关键字过滤文本，不区分大小写，匹配消息正文和类别。
        /// 改变时自动重新计算 FilteredEntries。
        /// </summary>
        public string FilterText
        {
            get => _filterText;
            set
            {
                if (SetProperty(ref _filterText, value))
                {
                    // 关键字改变，重新计算显示列表
                    ApplyFilter();
                }
            }
        }

        /// <summary>是否自动滚动到最新日志（绑定到 AutoScroll 复选框）。</summary>
        public bool AutoScroll
        {
            get => _autoScroll;
            set => SetProperty(ref _autoScroll, value);
        }

        // ============================================================================
        // 命令
        // ============================================================================

        /// <summary>清空所有日志命令，清空 IAppLogger 的缓冲区及本 ViewModel 的显示列表。</summary>
        public RelayCommand ClearCommand { get; }

        // ============================================================================
        // 构造函数
        // ============================================================================

        /// <summary>
        /// 初始化 LogPageViewModel 并订阅 IAppLogger 的日志事件。
        /// </summary>
        /// <param name="logger">应用日志记录器，必须非 null。</param>
        public LogPageViewModel(IAppLogger logger)
        {
            // 保存日志器引用并订阅新条目事件
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
            _logger.EntryAdded += OnEntryAdded;

            // 绑定清空命令
            ClearCommand = new RelayCommand(ExecuteClear);

            // 加载已有的历史条目（启动阶段服务层可能已写过日志）
            foreach (LogEntry entry in _logger.GetRecent())
            {
                // 将历史条目转为 ViewModel，符合当前过滤条件才加入显示列表
                LogEntryItemViewModel vm = ConvertEntry(entry);
                _allEntries.Add(vm);
                if (MatchesFilter(vm))
                    FilteredEntries.Add(vm);
            }
        }

        // ============================================================================
        // 事件处理
        // ============================================================================

        /// <summary>
        /// 响应 IAppLogger.EntryAdded 事件，将新条目追加到显示列表。
        /// 此方法可能在任意线程调用，内部切回 UI 线程更新集合。
        /// </summary>
        /// <param name="entry">新到达的日志条目。</param>
        private void OnEntryAdded(LogEntry entry)
        {
            // 构造 ViewModel（在调用线程上完成，不占 UI 线程时间）
            LogEntryItemViewModel vm = ConvertEntry(entry);

            // 切回 UI 线程修改 ObservableCollection（WPF 要求）
            Application.Current?.Dispatcher.InvokeAsync(() =>
            {
                // 追加到全量列表
                _allEntries.Add(vm);

                // 全量列表超限时移除最旧条目（循环缓冲区语义）
                if (_allEntries.Count > MaxDisplayEntries)
                    _allEntries.RemoveAt(0);

                // 符合当前过滤条件时才加入显示列表
                if (MatchesFilter(vm))
                {
                    FilteredEntries.Add(vm);

                    // 显示列表也超限时移除最旧条目
                    if (FilteredEntries.Count > MaxDisplayEntries)
                        FilteredEntries.RemoveAt(0);
                }
            });
        }

        // ============================================================================
        // 私有方法
        // ============================================================================

        /// <summary>
        /// 将 LogEntry 核心模型转换为 UI 绑定用的 LogEntryItemViewModel。
        /// </summary>
        /// <param name="entry">来自 IAppLogger 的日志条目。</param>
        /// <returns>对应的 ViewModel。</returns>
        private static LogEntryItemViewModel ConvertEntry(LogEntry entry)
        {
            // 映射核心模型字段到 ViewModel 属性
            return new LogEntryItemViewModel
            {
                Time     = entry.Timestamp.ToString("HH:mm:ss"),
                Level    = entry.Level,
                Category = entry.Category,
                Message  = entry.Message,
            };
        }

        /// <summary>
        /// 判断一条日志 ViewModel 是否满足当前过滤条件。
        /// </summary>
        /// <param name="vm">待判断的日志 ViewModel。</param>
        /// <returns>true = 满足条件，应显示；false = 不满足，应隐藏。</returns>
        private bool MatchesFilter(LogEntryItemViewModel vm)
        {
            // 级别过滤：ALL = 不过滤，否则只保留匹配级别
            if (_filterLevel != "ALL" && vm.Level != _filterLevel)
                return false;

            // 关键字过滤：为空则不过滤；否则消息或类别中有一项包含关键字即通过
            if (!string.IsNullOrWhiteSpace(_filterText))
            {
                bool inMessage  = vm.Message.IndexOf(_filterText,
                    StringComparison.OrdinalIgnoreCase) >= 0;
                bool inCategory = vm.Category.IndexOf(_filterText,
                    StringComparison.OrdinalIgnoreCase) >= 0;

                // 两者均不包含关键字则过滤掉
                if (!inMessage && !inCategory)
                    return false;
            }

            // 所有条件均满足
            return true;
        }

        /// <summary>
        /// 重新应用过滤条件，从全量列表重建 FilteredEntries。
        /// 在过滤条件改变时调用。
        /// </summary>
        private void ApplyFilter()
        {
            // 清空当前显示列表
            FilteredEntries.Clear();

            // 遍历全量列表，符合条件的加入显示列表
            foreach (LogEntryItemViewModel vm in _allEntries)
            {
                if (MatchesFilter(vm))
                    FilteredEntries.Add(vm);
            }
        }

        /// <summary>
        /// 清空所有日志：清空 IAppLogger 缓冲区和本 ViewModel 的显示列表。
        /// </summary>
        private void ExecuteClear()
        {
            // 清空日志器内部缓冲区
            _logger.Clear();

            // 清空 ViewModel 内的列表
            _allEntries.Clear();
            FilteredEntries.Clear();
        }
    }
}
