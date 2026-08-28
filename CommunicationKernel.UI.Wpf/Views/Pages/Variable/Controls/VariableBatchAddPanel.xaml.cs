#nullable disable

// -----------------------------------------------------------------------------
// 文件: Views/Pages/Variable/Controls/VariableBatchAddPanel.xaml.cs
// 层级: UI 层 — WPF Views
// 作用: 批量添加变量弹层：行编辑、校验，成功后把列表交给页面。
//
// 本控件不碰存储，也不知道当前是哪台设备：只负责把操作员填的若干行
// 校验成合法的 VariableItem，通过事件交出去。DeviceId 由页面补齐。
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

        /// <summary>请求收起弹层（点关闭按钮）。本次不入库。</summary>
        public event Action CloseRequested;

        /// <summary>校验通过，请求页面把这批变量写入当前设备。</summary>
        public event Action<IList<VariableItem>> BatchSaveRequested;

        /// <summary>校验失败等提示：(标题, 正文)。</summary>
        public event Action<string, string> InfoRequested;

        /// <summary>当前编辑中的行。绑定到 listRows，增删自动刷新计数。</summary>
        private readonly ObservableCollection<BatchRow> _rows = new ObservableCollection<BatchRow>();

        /// <summary>构造：加载 XAML 并把行集合绑定到列表。</summary>
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
        /// <param name="deviceTitle">当前设备的显示名，写在副标题上。</param>
        public void Prepare (string deviceTitle) {
            // 副标题写当前设备，避免批量加到错误设备上
            txtSub.Text = deviceTitle ?? "";
            // 打开弹层时清空残留，从空白表开始
            _rows.Clear();
            // 默认 3 行，减少第一次还要点「添加一行」
            AddRows(3);
        }

        /// <summary>追加 n 条空白行并重排序号。</summary>
        /// <param name="n">追加行数。</param>
        private void AddRows (int n) {
            // 追加 n 条空白行，默认分类/权限由 BatchRow.Create 给出
            for (int i = 0; i < n; i++)
                _rows.Add(BatchRow.Create());
            // 序号从 1 重排，保存校验用「第 N 行」提示
            Renumber();
        }

        /// <summary>
        /// 把行号重排成 1..N。
        /// </summary>
        /// <remarks>
        /// 删掉中间某行后必须重排：保存失败时提示的是「第 N 行」，
        /// 序号跳号会让操作员找不到出错的那一行。
        /// </remarks>
        private void Renumber () {
            // 行号与界面序号对齐，删中间行后也保持连续
            for (int i = 0; i < _rows.Count; i++)
                _rows[i].Index = i + 1;
            // 同步底栏「当前 N 行」文案
            RefreshCount();
        }

        /// <summary>刷新底栏行数文案。</summary>
        private void RefreshCount () =>
            txtCount.Text = "当前 " + _rows.Count + " 行";

        /// <summary>「添加一行」按钮。</summary>
        /// <param name="sender">事件源。</param>
        /// <param name="e">事件参数。</param>
        private void BtnAddOne_Click (object sender, RoutedEventArgs e) {
            // 操作员一次补一行
            AddRows(1);
        }

        /// <summary>「添加五行」按钮。</summary>
        /// <param name="sender">事件源。</param>
        /// <param name="e">事件参数。</param>
        private void BtnAddFive_Click (object sender, RoutedEventArgs e) {
            // 一次补 5 行，适合批量铺状态点/监控点
            AddRows(5);
        }

        /// <summary>行尾「删除」按钮，行对象由按钮 Tag 携带。</summary>
        /// <param name="sender">事件源，Tag 为要删除的 <see cref="BatchRow"/>。</param>
        /// <param name="e">事件参数。</param>
        private void BtnRemoveRow_Click (object sender, RoutedEventArgs e) {
            var row = (sender as FrameworkElement)?.Tag as BatchRow;
            // Tag 不是 BatchRow 时忽略
            if (row == null) return;
            // 从集合去掉后重排序号与计数
            _rows.Remove(row);
            Renumber();
        }

        /// <summary>「关闭」按钮：交给页面收起弹层，本次不入库。</summary>
        /// <param name="sender">事件源。</param>
        /// <param name="e">事件参数。</param>
        private void BtnClose_Click (object sender, RoutedEventArgs e) =>
            CloseRequested?.Invoke();

        /// <summary>
        /// 吞掉弹层内的左键点击。
        /// </summary>
        /// <remarks>
        /// 不拦的话事件会冒泡到背后的遮罩，操作员点弹层内部就把自己填的内容关没了。
        /// </remarks>
        /// <param name="sender">事件源。</param>
        /// <param name="e">事件参数。</param>
        private void Root_MouseLeftButtonDown (object sender, MouseButtonEventArgs e) =>
            e.Handled = true;

        /// <summary>
        /// 「保存」按钮：逐行校验后整批交给页面。
        /// </summary>
        /// <remarks>
        /// 校验策略是<b>全有或全无</b>：只要有一行半填就整批中止，不做部分入库。
        /// 部分成功会让操作员分不清哪几条进去了，比直接失败更难收拾。
        /// </remarks>
        /// <param name="sender">事件源。</param>
        /// <param name="e">事件参数。</param>
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

            /// <summary>属性变更通知，供 XAML 双向绑定。</summary>
            public event PropertyChangedEventHandler PropertyChanged;

            /// <summary>行号（1 起）。</summary>
            private int _index;

            /// <summary>变量名。</summary>
            private string _name = "";

            /// <summary>PLC 地址原文，格式由协议插件校验。</summary>
            private string _address = "";

            /// <summary>数据类型，默认 Bool（新行多为状态点）。</summary>
            private VariableDataType _dataType = VariableDataType.Bool;

            /// <summary>读写权限的界面文案，取值 R / W / R/W。</summary>
            private string _accessText = "R/W";

            /// <summary>工程单位，可空。</summary>
            private string _unit = "";

            /// <summary>分类，决定可选的数据类型。</summary>
            private string _category = "状态点";

            /// <summary>备注。</summary>
            private string _description = "";

            /// <summary>行号（1 起），由 <see cref="Renumber"/> 维护。</summary>
            public int Index {
                get => _index;
                set { _index = value; PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Index))); }
            }

            /// <summary>变量名。与地址必须同填或同空。</summary>
            public string Name {
                get => _name;
                set { _name = value; PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Name))); }
            }

            /// <summary>PLC 地址原文。合法性由协议插件在下发时判定，此处不解析。</summary>
            public string Address {
                get => _address;
                set { _address = value; PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Address))); }
            }

            /// <summary>数据类型。可选范围随 <see cref="Category"/> 变化。</summary>
            public VariableDataType DataType {
                get => _dataType;
                set { _dataType = value; PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(DataType))); }
            }

            /// <summary>读写权限文案（R / W / R/W），入库时由 <see cref="ToItem"/> 转成枚举。</summary>
            public string AccessText {
                get => _accessText;
                set { _accessText = value; PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(AccessText))); }
            }

            /// <summary>工程单位，仅用于显示。</summary>
            public string Unit {
                get => _unit;
                set { _unit = value; PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Unit))); }
            }

            /// <summary>
            /// 分类。改动会连带修正 <see cref="DataType"/> 并刷新可选项。
            /// </summary>
            /// <remarks>
            /// 状态点在界面上是开关灯，只有 Bool 说得通；反过来监控数据挂 Bool
            /// 会让曲线只有 0/1。因此这里做双向纠正，而不是仅仅过滤下拉项——
            /// 只过滤的话，已经选好类型再改分类就会留下一个非法组合。
            /// </remarks>
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

            /// <summary>备注，仅用于显示。</summary>
            public string Description {
                get => _description;
                set { _description = value; PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Description))); }
            }

            /// <summary>当前分类下允许的数据类型，绑定到类型下拉框。</summary>
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

            /// <summary>权限下拉的固定选项。</summary>
            public IList<string> AccessOptions { get; } =
                new[] { "R", "W", "R/W" };

            /// <summary>分类下拉的固定选项。</summary>
            public IList<string> CategoryOptions { get; } =
                new[] { "状态点", "监控数据", "轨道宽度" };

            /// <summary>建一条取默认值的空白行。</summary>
            /// <returns>新行。</returns>
            public static BatchRow Create () => new BatchRow();

            /// <summary>
            /// 转成入库用的 <see cref="VariableItem"/>。
            /// </summary>
            /// <remarks>
            /// <b>不填 DeviceId</b>：本控件不知道当前是哪台设备，由页面按选中设备补齐。
            /// </remarks>
            /// <returns>字段已 Trim 的变量项。</returns>
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
