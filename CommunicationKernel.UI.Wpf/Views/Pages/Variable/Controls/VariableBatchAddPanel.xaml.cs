#nullable disable

// -----------------------------------------------------------------------------
// 文件: Views/Pages/Variable/Controls/VariableBatchAddPanel.xaml.cs
// 层级: UI 层 — WPF Views
// 作用: 批量添加变量弹层：行编辑、校验，成功后把列表交给页面。
// -----------------------------------------------------------------------------

using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using CommunicationKernel.UI.Wpf.Core.Enums;
using CommunicationKernel.UI.Wpf.Core.Models;

namespace CommunicationKernel.UI.Wpf.Views.Pages.Variable.Controls {
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
            // 解析 XAML 并把行集合绑到列表
            InitializeComponent();
            listRows.ItemsSource = _rows;
            _rows.CollectionChanged += (s, e) => RefreshCount();
        }

        // ============================================================================
        // 行操作
        // ============================================================================

        /// <summary>打开前初始化：设备副标题 + 默认 3 行。</summary>
        public void Prepare (string deviceTitle) {
            // 副标题写当前设备，避免批量加到错误设备上
            txtSub.Text = deviceTitle ?? "";
            // 打开弹层时清空残留，从空白表开始
            _rows.Clear();
            // 默认 3 行，减少第一次还要点「添加一行」
            AddRows(3);
        }

        private void AddRows (int n) {
            // 追加 n 条空白行，默认分类/权限由 BatchRow.Create 给出
            for (int i = 0; i < n; i++)
                _rows.Add(BatchRow.Create());
            // 序号从 1 重排，保存校验用「第 N 行」提示
            Renumber();
        }

        private void Renumber () {
            // 行号与界面序号对齐，删中间行后也保持连续
            for (int i = 0; i < _rows.Count; i++)
                _rows[i].Index = i + 1;
            // 同步底栏「当前 N 行」文案
            RefreshCount();
        }

        private void RefreshCount () =>
            txtCount.Text = "当前 " + _rows.Count + " 行";

        private void BtnAddOne_Click (object sender, RoutedEventArgs e) {
            // 操作员一次补一行
            AddRows(1);
        }

        private void BtnAddFive_Click (object sender, RoutedEventArgs e) {
            // 一次补 5 行，适合批量铺状态点/监控点
            AddRows(5);
        }

        private void BtnRemoveRow_Click (object sender, RoutedEventArgs e) {
            var row = (sender as FrameworkElement)?.Tag as BatchRow;
            // Tag 不是 BatchRow 时忽略
            if (row == null) return;
            // 从集合去掉后重排序号与计数
            _rows.Remove(row);
            Renumber();
        }

        // 交给页面收起弹层，本次不入库
        private void BtnClose_Click (object sender, RoutedEventArgs e) =>
            CloseRequested?.Invoke();

        // 拦点击，避免穿透到遮罩把弹层关掉
        private void Root_MouseLeftButtonDown (object sender, MouseButtonEventArgs e) =>
            e.Handled = true;

        private void BtnSave_Click (object sender, RoutedEventArgs e) {
            // 逐行校验后再交给页面入库，避免半填行脏数据
            var list = new List<VariableItem>();
            // 空行可跳过；只填名称或地址则整批中止并提示行号
            foreach (BatchRow r in _rows) {
                string name = (r.Name ?? "").Trim();
                string addr = (r.Address ?? "").Trim();
                // 整行留空：跳过；只填一项：提示后中止
                if (string.IsNullOrEmpty(name) && string.IsNullOrEmpty(addr))
                    continue;
                if (string.IsNullOrEmpty(name) || string.IsNullOrEmpty(addr)) {
                    // 走主题提示并中止整批，避免半填行入库
                    InfoRequested?.Invoke(
                        "提示",
                        "第 " + r.Index + " 行：名称和地址需同时填写（或整行留空跳过）");
                    return;
                }
                // 通过校验的行转成 VariableItem，DeviceId 由页面补当前设备
                list.Add(r.ToItem());
            }

            // 全部是空行时没有可保存内容
            if (list.Count == 0) {
                // 走主题提示，弹层保持打开方便继续填
                InfoRequested?.Invoke("提示", "没有可添加的行");
                return;
            }

            // 列表交给页面 ViewModel 批量写入当前设备
            BatchSaveRequested?.Invoke(list);
        }

        // ============================================================================
        // 行模型
        // ============================================================================

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
                    // 分类未变则不刷绑定，避免 ComboBox 抖动
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
                    // 状态点只允许 Bool，下拉只给一项
                    if (_category == "状态点")
                        return new[] { VariableDataType.Bool };
                    // 监控数据 / 轨道宽度：数值与字符串，不含 Bool
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
                // 界面用 R/W 文案，入库转成枚举；默认读写
                VariableAccess access = VariableAccess.ReadWrite;
                if (AccessText == "R") access = VariableAccess.ReadOnly;
                else if (AccessText == "W") access = VariableAccess.WriteOnly;

                // Trim 后交给页面，DeviceId 由页面按当前设备补齐
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
