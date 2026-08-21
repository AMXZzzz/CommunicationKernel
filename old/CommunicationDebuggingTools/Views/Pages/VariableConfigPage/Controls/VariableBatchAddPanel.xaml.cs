using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using CommunicationDebuggingTools.Core.Enums;
using CommunicationDebuggingTools.Core.Models;

namespace CommunicationDebuggingTools.Views.VariableConfigPage.Controls {
    /// <summary>
    /// 批量添加变量弹层。
    /// 校验失败通过 <see cref="InfoRequested"/> 交给页面
    /// </summary>
    public partial class VariableBatchAddPanel : UserControl {
        public event Action CloseRequested;
        public event Action<IList<VariableItem>> BatchSaveRequested;

        /// <summary>校验失败等提示：(标题, 正文)。</summary>
        public event Action<string, string> InfoRequested;

        private readonly ObservableCollection<BatchRow> _rows = new ObservableCollection<BatchRow>();

        public VariableBatchAddPanel () {
            InitializeComponent();
            listRows.ItemsSource = _rows;
            _rows.CollectionChanged += (s, e) => RefreshCount();
        }

        /// <summary>打开前初始化：设备副标题 + 默认 3 行。</summary>
        public void Prepare (string deviceTitle) {
            txtSub.Text = deviceTitle ?? "";
            _rows.Clear();
            AddRows(3);
        }

        private void AddRows (int n) {
            for (int i = 0; i < n; i++)
                _rows.Add(BatchRow.Create());
            Renumber();
        }

        private void Renumber () {
            for (int i = 0; i < _rows.Count; i++)
                _rows[i].Index = i + 1;
            RefreshCount();
        }

        private void RefreshCount () =>
            txtCount.Text = "当前 " + _rows.Count + " 行";

        private void BtnAddOne_Click (object sender, RoutedEventArgs e) {
            AddRows(1);
        }

        private void BtnAddFive_Click (object sender, RoutedEventArgs e) {
            AddRows(5);
        }

        private void BtnRemoveRow_Click (object sender, RoutedEventArgs e) {
            var row = (sender as FrameworkElement)?.Tag as BatchRow;
            if (row == null) return;
            _rows.Remove(row);
            Renumber();
        }

        private void BtnClose_Click (object sender, RoutedEventArgs e) =>
            CloseRequested?.Invoke();

        private void Root_MouseLeftButtonDown (object sender, MouseButtonEventArgs e) =>
            e.Handled = true;

        private void BtnSave_Click (object sender, RoutedEventArgs e) {
            var list = new List<VariableItem>();
            foreach (BatchRow r in _rows) {
                string name = (r.Name ?? "").Trim();
                string addr = (r.Address ?? "").Trim();
                if (string.IsNullOrEmpty(name) && string.IsNullOrEmpty(addr))
                    continue;
                if (string.IsNullOrEmpty(name) || string.IsNullOrEmpty(addr)) {
                    InfoRequested?.Invoke(
                        "提示",
                        "第 " + r.Index + " 行：名称和地址需同时填写（或整行留空跳过）");
                    return;
                }
                list.Add(r.ToItem());
            }

            if (list.Count == 0) {
                InfoRequested?.Invoke("提示", "没有可添加的行");
                return;
            }

            BatchSaveRequested?.Invoke(list);
        }

        /// <summary>批量行编辑模型。</summary>
        public sealed class BatchRow : INotifyPropertyChanged {
            public event PropertyChangedEventHandler PropertyChanged;

            private int _index;
            private string _name = "";
            private string _address = "";
            private VariableDataType _dataType = VariableDataType.Bool;
            private string _accessText = "R/W";
            private string _unit = "";
            private string _category = "状态点";
            private string _description = "";

            public int Index {
                get => _index;
                set { _index = value; PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Index))); }
            }

            public string Name {
                get => _name;
                set { _name = value; PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Name))); }
            }

            public string Address {
                get => _address;
                set { _address = value; PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Address))); }
            }

            public VariableDataType DataType {
                get => _dataType;
                set { _dataType = value; PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(DataType))); }
            }

            public string AccessText {
                get => _accessText;
                set { _accessText = value; PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(AccessText))); }
            }

            public string Unit {
                get => _unit;
                set { _unit = value; PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Unit))); }
            }

            public string Category {
                get => _category;
                set {
                    if (_category == value) return;
                    _category = value;
                    PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Category)));
                    PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(DataTypeOptions)));
                    // 状态点仅 Bool
                    if (_category == "状态点" && _dataType != VariableDataType.Bool)
                        DataType = VariableDataType.Bool;
                    else if (_category != "状态点" && _dataType == VariableDataType.Bool)
                        DataType = VariableDataType.Int16;
                }
            }

            public string Description {
                get => _description;
                set { _description = value; PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Description))); }
            }

            public IList<VariableDataType> DataTypeOptions {
                get {
                    if (_category == "状态点")
                        return new[] { VariableDataType.Bool };
                    return new[] {
                        VariableDataType.Int16, VariableDataType.UInt16,
                        VariableDataType.Int32, VariableDataType.UInt32,
                        VariableDataType.Int64, VariableDataType.UInt64,
                        VariableDataType.Float, VariableDataType.Double,
                        VariableDataType.String
                    };
                }
            }

            public IList<string> AccessOptions { get; } =
                new[] { "R", "W", "R/W" };

            public IList<string> CategoryOptions { get; } =
                new[] { "状态点", "监控数据", "轨道宽度" };

            public static BatchRow Create () => new BatchRow();

            public VariableItem ToItem () {
                VariableAccess access = VariableAccess.ReadWrite;
                if (AccessText == "R") access = VariableAccess.ReadOnly;
                else if (AccessText == "W") access = VariableAccess.WriteOnly;

                return new VariableItem {
                    Name = (Name ?? "").Trim(),
                    Address = (Address ?? "").Trim(),
                    DataType = DataType,
                    Access = access,
                    Unit = (Unit ?? "").Trim(),
                    Category = Category ?? "状态点",
                    Description = (Description ?? "").Trim()
                };
            }
        }
    }
}