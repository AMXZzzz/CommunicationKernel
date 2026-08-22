#nullable disable

// -----------------------------------------------------------------------------
// 文件: Views/Pages/Variable/Controls/VariableTable.xaml.cs
// 层级: UI 层 — WPF Views
// 作用: 变量表：筛选、计数、轮询开关、写入；徽章颜色由 XAML DataTrigger 负责。
// -----------------------------------------------------------------------------

using System;
using System.ComponentModel;
using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using CommunicationKernel.UI.Wpf.Core.Enums;
using CommunicationKernel.UI.Wpf.Core.Models;
using CommunicationKernel.UI.Wpf.Core.Interfaces;

namespace CommunicationKernel.UI.Wpf.Views.Pages.Variable.Controls {
    /// <summary>
    /// 变量表：筛选与计数；徽章颜色由 XAML DataTrigger 负责。
    /// </summary>
    public partial class VariableTable : UserControl {
        /// <summary>由页面注入。</summary>
        public IVariableService VariableService { get; set; }

        public event Action VariablesChanged;
        public event Action<VariableItem> EditRequested;
        public event Action<string, string> WriteRequested;

        private readonly ObservableCollection<Row> _rows = new ObservableCollection<Row>();
        private string _deviceId;
        private string _filterTag = "All";

        public VariableTable () {
            // 解析 XAML，绑定行集合；卸载时断开 PropertyChanged 避免泄漏
            InitializeComponent();
            listRows.ItemsSource = _rows;
            Unloaded += (s, e) => DetachAllRows();
        }

        private void DetachAllRows () {
            // 控件卸载或重建前先退订 LastValue，避免已销毁行仍被轮询刷新
            foreach (Row r in _rows)
                r.Detach();
        }

        // ============================================================================
        // 筛选 / 重建
        // ============================================================================

        public void Load (string deviceId) {
            // 记住当前设备，后续筛选/删除都只作用于这台
            _deviceId = deviceId;
            // 按当前页签筛选重建行
            Rebuild();
        }

        private void Filter_Checked (object sender, RoutedEventArgs e) {
            var rb = sender as RadioButton;
            // 非筛选页签来源忽略
            if (rb == null || rb.Tag == null) return;
            // Tag：All / Read / Write / 状态点 / 监控数据
            _filterTag = rb.Tag.ToString();
            Rebuild();
        }

        // ============================================================================
        // 行操作
        // ============================================================================

        /// <summary>
        /// 轮询开关点击：同步新值到 VariableItem，调用 Update 触发 VariablesChanged，
        /// VariablePollingService 收到通知后重建轮询任务集合。
        /// </summary>
        private void ChkPoll_Click(object sender, RoutedEventArgs e) {
            CheckBox cb = sender as CheckBox;
            // CheckBox 或服务未就绪时无法改轮询
            if (cb == null || VariableService == null) return;
            string id = cb.Tag as string;
            // Tag 存变量 Id，找不到则忽略这次点击
            if (string.IsNullOrEmpty(id)) return;

            bool newValue = cb.IsChecked == true;
            VariableItem item = FindVariable(id);
            // 行已被删或已切设备时不再写回
            if (item == null) return;

            // 更新 VariableItem 上的轮询标志，然后通知服务层重建轮询集合
            item.IsPollingEnabled = newValue;
            VariableService.Update(item);

            // 同步 Row 视图模型的属性，使 CheckBox 保持正确绑定状态
            foreach (Row r in _rows) {
                if (r.Id == id) {
                    r.IsPollingEnabled = newValue;
                    break;
                }
            }
        }

        private void BtnHint_Click (object sender, RoutedEventArgs e) {
            // 底栏提示条可折叠，控件未生成时直接忽略
            if (hintBar == null) return;
            hintBar.Visibility = hintBar.Visibility == Visibility.Visible
                ? Visibility.Collapsed : Visibility.Visible;
        }

        private void BtnEdit_Click (object sender, RoutedEventArgs e) {
            string id = (sender as FrameworkElement)?.Tag as string;
            VariableItem item = FindVariable(id);
            // 转交页面打开编辑弹层
            if (item != null) EditRequested?.Invoke(item);
        }

        private void BtnDelete_Click (object sender, RoutedEventArgs e) {
            string id = (sender as FrameworkElement)?.Tag as string;
            // 无 Id 或服务未注入时不能删
            if (string.IsNullOrEmpty(id) || VariableService == null) return;
            // 从存储移除后重建表，并通知左侧设备列表刷新变量计数
            VariableService.Remove(id);
            Rebuild();
            VariablesChanged?.Invoke();
        }

        private void BtnWrite_Click (object sender, RoutedEventArgs e) {
            string id = (sender as FrameworkElement)?.Tag as string;
            // 没有变量 Id 就无法下发写入
            if (string.IsNullOrEmpty(id)) return;
            // 按 Id 找到行，清 dirty 后把写入值交给页面
            foreach (Row r in _rows) {
                if (r.Id == id) {
                    r.ClearWriteDirty();
                    WriteRequested?.Invoke(id, r.WriteText ?? "");
                    return;
                }
            }
        }

        private void WriteTextBox_PreviewKeyDown (object sender, System.Windows.Input.KeyEventArgs e) {
            // 只拦截 Enter，当作点「写入」；其它键交给文本框
            if (e == null || e.Key != System.Windows.Input.Key.Enter) return;
            e.Handled = true;
            BtnWrite_Click(sender, e);
        }

        private void Rebuild () {
            // 先退订旧行再清空，避免残留订阅刷到已移除的行
            DetachAllRows();
            _rows.Clear();
            int all = 0, read = 0, write = 0, status = 0, data = 0;

            // 未选设备：清空计数并显示空状态
            if (string.IsNullOrEmpty(_deviceId) || VariableService == null) {
                UpdateTabCounts(0, 0, 0, 0, 0);
                UpdateFooter(0, 0, 0);
                txtEmpty.Visibility = Visibility.Visible;
                return;
            }

            int index = 1;
            foreach (VariableItem v in VariableService.Variables) {
                // 只统计当前设备
                if (v == null || v.DeviceId != _deviceId) continue;

                // 计数按权限/分类累加，给 Tab 和底栏用（不受当前筛选影响）
                all++;
                if (v.Access == VariableAccess.ReadOnly) read++;
                if (v.Access == VariableAccess.WriteOnly || v.Access == VariableAccess.ReadWrite)
                    write++;
                if (v.Category == "状态点") status++;
                if (v.Category == "监控数据") data++;

                // 页签过滤未命中的不进表格，但仍计入上方 Tab 数字
                if (!PassFilter(v)) continue;
                _rows.Add(Row.From(v, index++));
            }

            // 同步 Tab 角标和底栏计数；无可见行时显示空状态
            UpdateTabCounts(all, read, write, status, data);
            UpdateFooter(all, read, write);
            txtEmpty.Visibility = _rows.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        }

        private bool PassFilter (VariableItem v) {
            // 「全部」或未选页签：不过滤
            if (string.IsNullOrEmpty(_filterTag) || _filterTag == "All") return true;
            switch (_filterTag) {
                case "Read": return v.Access == VariableAccess.ReadOnly;
                case "Write":
                    return v.Access == VariableAccess.WriteOnly
                        || v.Access == VariableAccess.ReadWrite;
                case "状态点": return v.Category == "状态点";
                case "监控数据": return v.Category == "监控数据";
                default: return true;
            }
        }

        private void UpdateTabCounts (int all, int read, int write, int status, int data) {
            // 控件可能尚未加载，逐个判空后再改 Content
            if (tabAll != null) tabAll.Content = string.Format("全部 ({0})", all);
            if (tabRead != null) tabRead.Content = string.Format("只读 ({0})", read);
            if (tabWrite != null) tabWrite.Content = string.Format("可写 ({0})", write);
            if (tabStatus != null) tabStatus.Content = string.Format("状态点 ({0})", status);
            if (tabData != null) tabData.Content = string.Format("监控数据 ({0})", data);
        }

        private void UpdateFooter (int all, int read, int write) {
            // 底栏与 Tab 使用同一套统计，避免筛选后数字对不上
            if (txtFtAll != null) txtFtAll.Text = string.Format("共 {0} 个变量", all);
            if (txtFtRead != null) txtFtRead.Text = string.Format("只读 {0}", read);
            if (txtFtWrite != null) txtFtWrite.Text = string.Format("可写 {0}", write);
        }

        private VariableItem FindVariable (string id) {
            // 无 Id 或服务未注入时查不到
            if (string.IsNullOrEmpty(id) || VariableService == null) return null;
            // 在当前服务的变量列表里按 Id 找源对象
            foreach (VariableItem v in VariableService.Variables)
                if (v != null && v.Id == id) return v;
            return null;
        }

        // ============================================================================
        // 行视图模型
        // ============================================================================

        /// <summary>
        /// 行视图模型：订阅 VariableItem.LastValue，轮询读回后刷新界面。
        /// </summary>
        private sealed class Row : INotifyPropertyChanged {
            public event PropertyChangedEventHandler PropertyChanged;

            private VariableItem _source;
            private string _valueText;
            private string _writeText;
            private bool _writeDirty;
            private bool _writeFocused;
            private bool _isPollingEnabled;

            public string Id { get; set; }
            public int Index { get; set; }
            public string Name { get; set; }
            public string Address { get; set; }
            public string DataType { get; set; }
            public string AccessText { get; set; }
            public string UnitText { get; set; }
            public string Category { get; set; }
            public string Description { get; set; }
            public Visibility ValueTextVisibility { get; set; }
            public Visibility WriteEditorVisibility { get; set; }
            public Visibility DescToolTipVisibility { get; set; }

            /// <summary>
            /// 是否启用轮询。绑定 CheckBox.IsChecked（OneWay），
            /// 点击时由 ChkPoll_Click 写回此属性并调用 VariableService.Update。
            /// </summary>
            public bool IsPollingEnabled {
                get { return _isPollingEnabled; }
                set {
                    // 值未变不 Raise，避免 CheckBox 来回触发
                    if (_isPollingEnabled == value) return;
                    _isPollingEnabled = value;
                    Raise(nameof(IsPollingEnabled));
                }
            }

            public string ValueText {
                get { return _valueText; }
                set {
                    // 轮询高频刷新，相同文本不通知，减轻绑定开销
                    if (_valueText == value) return;
                    _valueText = value;
                    Raise(nameof(ValueText));
                }
            }

            public string WriteText {
                get { return _writeText; }
                set {
                    // 操作员改了写入框才标 dirty，避免轮询回写冲掉正在编辑的内容
                    if (_writeText == value) return;
                    _writeText = value;
                    _writeDirty = true;
                    Raise(nameof(WriteText));
                }
            }

            public void SetWriteFocused (bool focused) {
                _writeFocused = focused;
            }

            public bool IsWriteEditable () {
                // 焦点在框内或已改过内容时，禁止用 LastValue 覆盖写入框
                return !_writeFocused && !_writeDirty;
            }

            public void ClearWriteDirty () { _writeDirty = false; }

            public void Detach () {
                // 退订源对象，切断轮询 → UI 的刷新链路
                if (_source != null) {
                    _source.PropertyChanged -= OnSourcePropertyChanged;
                    _source = null;
                }
            }

            public static Row From (VariableItem v, int index) {
                // 枚举转界面文案：只读 R、只写 W、其余 R/W
                string access = "R/W";
                if (v.Access == VariableAccess.ReadOnly) access = "R";
                else if (v.Access == VariableAccess.WriteOnly) access = "W";

                bool canWrite = v.Access == VariableAccess.WriteOnly
                             || v.Access == VariableAccess.ReadWrite;
                string desc = v.Description ?? "";
                string val = v.LastValue != null ? v.LastValue.ToString() : "—";
                string write = v.LastValue != null ? v.LastValue.ToString() : "";

                var row = new Row {
                    Id = v.Id,
                    Index = index,
                    Name = v.Name ?? "",
                    Address = v.Address ?? "",
                    DataType = v.DataType.ToString(),
                    AccessText = access,
                    UnitText = string.IsNullOrWhiteSpace(v.Unit) ? "—" : v.Unit,
                    Category = string.IsNullOrWhiteSpace(v.Category) ? "—" : v.Category,
                    Description = desc,
                    ValueTextVisibility = Visibility.Visible,  // 始终显示实时值，写入编辑器独立
                    WriteEditorVisibility = canWrite ? Visibility.Visible : Visibility.Collapsed,
                    DescToolTipVisibility = string.IsNullOrWhiteSpace(desc)
                        ? Visibility.Collapsed : Visibility.Visible
                };
                row._valueText = val;
                row._writeText = write;
                row._writeDirty = false;
                row._isPollingEnabled = v.IsPollingEnabled;
                row._source = v;
                // 订阅 LastValue，轮询读回后刷新本行
                v.PropertyChanged += row.OnSourcePropertyChanged;
                return row;
            }

            private void OnSourcePropertyChanged (object sender, PropertyChangedEventArgs e) {
                // 只关心 LastValue；其它字段变更走 Rebuild 而不是逐行刷
                if (e == null) return;
                if (e.PropertyName != nameof(VariableItem.LastValue) && e.PropertyName != "LastValue")
                    return;

                VariableItem v = _source;
                // 已 Detach 则不再更新
                if (v == null) return;

                Action apply = () => {
                    string text = v.LastValue != null ? v.LastValue.ToString() : "—";
                    ValueText = text;
                    // 写入框空闲时跟读值走，正在编辑则保留操作员输入
                    if (IsWriteEditable()) {
                        _writeText = v.LastValue != null ? v.LastValue.ToString() : "";
                        Raise(nameof(WriteText));
                    }
                };

                // 轮询线程可能非 UI 线程，必须封送到 Dispatcher
                var d = Application.Current != null ? Application.Current.Dispatcher : null;
                if (d != null && !d.CheckAccess())
                    d.BeginInvoke(apply);
                else
                    apply();
            }

            private void Raise (string name) {
                PropertyChangedEventHandler h = PropertyChanged;
                // 无订阅者时不构造 EventArgs
                if (h != null)
                    h(this, new PropertyChangedEventArgs(name));
            }
        }
    }
}
