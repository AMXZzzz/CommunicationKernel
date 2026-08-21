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
            InitializeComponent();
            listRows.ItemsSource = _rows;
            Unloaded += (s, e) => DetachAllRows();
        }

        private void DetachAllRows () {
            foreach (Row r in _rows)
                r.Detach();
        }

        public void Load (string deviceId) {
            _deviceId = deviceId;
            Rebuild();
        }

        private void Filter_Checked (object sender, RoutedEventArgs e) {
            var rb = sender as RadioButton;
            if (rb == null || rb.Tag == null) return;
            _filterTag = rb.Tag.ToString();
            Rebuild();
        }

        /// <summary>
        /// 轮询开关点击：同步新值到 VariableItem，调用 Update 触发 VariablesChanged，
        /// VariablePollingService 收到通知后重建轮询任务集合。
        /// </summary>
        private void ChkPoll_Click(object sender, RoutedEventArgs e) {
            CheckBox cb = sender as CheckBox;
            if (cb == null || VariableService == null) return;
            string id = cb.Tag as string;
            if (string.IsNullOrEmpty(id)) return;

            bool newValue = cb.IsChecked == true;
            VariableItem item = FindVariable(id);
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
            if (hintBar == null) return;
            hintBar.Visibility = hintBar.Visibility == Visibility.Visible
                ? Visibility.Collapsed : Visibility.Visible;
        }

        private void BtnEdit_Click (object sender, RoutedEventArgs e) {
            string id = (sender as FrameworkElement)?.Tag as string;
            VariableItem item = FindVariable(id);
            if (item != null) EditRequested?.Invoke(item);
        }

        private void BtnDelete_Click (object sender, RoutedEventArgs e) {
            string id = (sender as FrameworkElement)?.Tag as string;
            if (string.IsNullOrEmpty(id) || VariableService == null) return;
            VariableService.Remove(id);
            Rebuild();
            VariablesChanged?.Invoke();
        }

        private void BtnWrite_Click (object sender, RoutedEventArgs e) {
            string id = (sender as FrameworkElement)?.Tag as string;
            if (string.IsNullOrEmpty(id)) return;
            foreach (Row r in _rows) {
                if (r.Id == id) {
                    r.ClearWriteDirty();
                    WriteRequested?.Invoke(id, r.WriteText ?? "");
                    return;
                }
            }
        }

        private void WriteTextBox_PreviewKeyDown (object sender, System.Windows.Input.KeyEventArgs e) {
            if (e == null || e.Key != System.Windows.Input.Key.Enter) return;
            e.Handled = true;
            BtnWrite_Click(sender, e);
        }

        private void Rebuild () {
            DetachAllRows();
            _rows.Clear();
            int all = 0, read = 0, write = 0, status = 0, data = 0;

            if (string.IsNullOrEmpty(_deviceId) || VariableService == null) {
                UpdateTabCounts(0, 0, 0, 0, 0);
                UpdateFooter(0, 0, 0);
                txtEmpty.Visibility = Visibility.Visible;
                return;
            }

            int index = 1;
            foreach (VariableItem v in VariableService.Variables) {
                if (v == null || v.DeviceId != _deviceId) continue;

                all++;
                if (v.Access == VariableAccess.ReadOnly) read++;
                if (v.Access == VariableAccess.WriteOnly || v.Access == VariableAccess.ReadWrite)
                    write++;
                if (v.Category == "状态点") status++;
                if (v.Category == "监控数据") data++;

                if (!PassFilter(v)) continue;
                _rows.Add(Row.From(v, index++));
            }

            UpdateTabCounts(all, read, write, status, data);
            UpdateFooter(all, read, write);
            txtEmpty.Visibility = _rows.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        }

        private bool PassFilter (VariableItem v) {
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
            if (tabAll != null) tabAll.Content = string.Format("全部 ({0})", all);
            if (tabRead != null) tabRead.Content = string.Format("只读 ({0})", read);
            if (tabWrite != null) tabWrite.Content = string.Format("可写 ({0})", write);
            if (tabStatus != null) tabStatus.Content = string.Format("状态点 ({0})", status);
            if (tabData != null) tabData.Content = string.Format("监控数据 ({0})", data);
        }

        private void UpdateFooter (int all, int read, int write) {
            if (txtFtAll != null) txtFtAll.Text = string.Format("共 {0} 个变量", all);
            if (txtFtRead != null) txtFtRead.Text = string.Format("只读 {0}", read);
            if (txtFtWrite != null) txtFtWrite.Text = string.Format("可写 {0}", write);
        }

        private VariableItem FindVariable (string id) {
            if (string.IsNullOrEmpty(id) || VariableService == null) return null;
            foreach (VariableItem v in VariableService.Variables)
                if (v != null && v.Id == id) return v;
            return null;
        }

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
                    if (_isPollingEnabled == value) return;
                    _isPollingEnabled = value;
                    Raise(nameof(IsPollingEnabled));
                }
            }

            public string ValueText {
                get { return _valueText; }
                set {
                    if (_valueText == value) return;
                    _valueText = value;
                    Raise(nameof(ValueText));
                }
            }

            public string WriteText {
                get { return _writeText; }
                set {
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
                return !_writeFocused && !_writeDirty;
            }

            public void ClearWriteDirty () { _writeDirty = false; }

            public void Detach () {
                if (_source != null) {
                    _source.PropertyChanged -= OnSourcePropertyChanged;
                    _source = null;
                }
            }

            public static Row From (VariableItem v, int index) {
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
                v.PropertyChanged += row.OnSourcePropertyChanged;
                return row;
            }

            private void OnSourcePropertyChanged (object sender, PropertyChangedEventArgs e) {
                if (e == null) return;
                if (e.PropertyName != nameof(VariableItem.LastValue) && e.PropertyName != "LastValue")
                    return;

                VariableItem v = _source;
                if (v == null) return;

                Action apply = () => {
                    string text = v.LastValue != null ? v.LastValue.ToString() : "—";
                    ValueText = text;
                    if (IsWriteEditable()) {
                        _writeText = v.LastValue != null ? v.LastValue.ToString() : "";
                        Raise(nameof(WriteText));
                    }
                };

                var d = Application.Current != null ? Application.Current.Dispatcher : null;
                if (d != null && !d.CheckAccess())
                    d.BeginInvoke(apply);
                else
                    apply();
            }

            private void Raise (string name) {
                PropertyChangedEventHandler h = PropertyChanged;
                if (h != null)
                    h(this, new PropertyChangedEventArgs(name));
            }
        }
    }
}
