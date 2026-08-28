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

        /// <summary>变量增删或轮询开关变化时触发，供页面刷新左侧设备列表的计数。</summary>
        public event Action VariablesChanged;
        /// <summary>请求打开编辑弹层。本控件只发事件，不认识弹层类型。</summary>
        public event Action<VariableItem> EditRequested;
        /// <summary>请求写入：参数为（变量 Id，输入框文本）。解析与下发由页面负责。</summary>
        public event Action<string, string> WriteRequested;

        /// <summary>
        /// 当前显示的行。
        /// </summary>
        /// <remarks>
        /// 每个 Row 都订阅了源 VariableItem 的 PropertyChanged，
        /// 清空或重建前必须先 <see cref="DetachAllRows"/>，否则已移除的行仍会被轮询刷新。
        /// </remarks>
        private readonly ObservableCollection<Row> _rows = new ObservableCollection<Row>();
        /// <summary>当前显示的设备 Id；筛选与删除都只作用于这台。</summary>
        private string _deviceId;
        /// <summary>当前筛选页签：All / Read / Write / 状态点 / 监控数据。</summary>
        private string _filterTag = "All";

        /// <summary>解析 XAML、绑定行集合，并挂上卸载时的退订钩子。</summary>
        public VariableTable () {
            // 解析 XAML，绑定行集合；卸载时断开 PropertyChanged 避免泄漏
            InitializeComponent();
            listRows.ItemsSource = _rows;
            Unloaded += (s, e) => DetachAllRows();
        }

        /// <summary>
        /// 退订所有行对源变量的 PropertyChanged。
        /// </summary>
        /// <remarks>
        /// 源 VariableItem 的生命周期由存储持有，远长于本控件的行对象。
        /// 漏退订会让已被移除的行继续收到轮询回填的读值，
        /// 表现为内存泄漏加上「删掉的变量还在更新」。
        /// </remarks>
        private void DetachAllRows () {
            // 控件卸载或重建前先退订 LastValue，避免已销毁行仍被轮询刷新
            foreach (Row r in _rows)
                r.Detach();
        }

        // ============================================================================
        // 筛选 / 重建
        // ============================================================================

        /// <summary>切换到指定设备并重建表格。</summary>
        /// <param name="deviceId">设备 Id；为空时表格清空。</param>
        public void Load (string deviceId) {
            // 记住当前设备，后续筛选/删除都只作用于这台
            _deviceId = deviceId;
            // 按当前页签筛选重建行
            Rebuild();
        }

        /// <summary>筛选页签切换：按 RadioButton 的 Tag 重建行。</summary>
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

        /// <summary>折叠/展开底栏提示条。</summary>
        private void BtnHint_Click (object sender, RoutedEventArgs e) {
            // 底栏提示条可折叠，控件未生成时直接忽略
            if (hintBar == null) return;
            hintBar.Visibility = hintBar.Visibility == Visibility.Visible
                ? Visibility.Collapsed : Visibility.Visible;
        }

        /// <summary>编辑按钮：找到对应变量后转交页面打开弹层。</summary>
        private void BtnEdit_Click (object sender, RoutedEventArgs e) {
            string id = (sender as FrameworkElement)?.Tag as string;
            VariableItem item = FindVariable(id);
            // 转交页面打开编辑弹层
            if (item != null) EditRequested?.Invoke(item);
        }

        /// <summary>删除按钮：从存储移除后重建表并通知计数刷新。</summary>
        /// <remarks>三步顺序固定：先落存储、再重建视图、最后广播。
        /// 先广播会让订阅方读到还没更新的存储。</remarks>
        private void BtnDelete_Click (object sender, RoutedEventArgs e) {
            string id = (sender as FrameworkElement)?.Tag as string;
            // 无 Id 或服务未注入时不能删
            if (string.IsNullOrEmpty(id) || VariableService == null) return;
            // 从存储移除后重建表，并通知左侧设备列表刷新变量计数
            VariableService.Remove(id);
            Rebuild();
            VariablesChanged?.Invoke();
        }

        /// <summary>写入按钮：清掉该行的 dirty 标记后把文本交给页面解析下发。</summary>
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

        /// <summary>写入框回车等同点「写入」；其余按键交回文本框。</summary>
        private void WriteTextBox_PreviewKeyDown (object sender, System.Windows.Input.KeyEventArgs e) {
            // 只拦截 Enter，当作点「写入」；其它键交给文本框
            if (e == null || e.Key != System.Windows.Input.Key.Enter) return;
            e.Handled = true;
            BtnWrite_Click(sender, e);
        }

        /// <summary>
        /// 按当前设备与筛选页签重建整张表，同时刷新 Tab 角标与底栏计数。
        /// </summary>
        /// <remarks>
        /// 计数统计<b>不</b>受筛选影响——Tab 上的数字要反映各类别的全量，
        /// 否则点进「只读」后其它页签的角标会全变成 0。
        /// </remarks>
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

        /// <summary>判断某个变量是否应出现在当前筛选页签下。</summary>
        /// <remarks>「可写」同时涵盖 WriteOnly 与 ReadWrite——操作员找的是"能不能写"，
        /// 不是精确的访问权限分类。</remarks>
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

        /// <summary>刷新五个页签的角标数字。</summary>
        /// <remarks>逐个判空：本方法可能在 XAML 尚未完全加载时被调用。</remarks>
        private void UpdateTabCounts (int all, int read, int write, int status, int data) {
            // 控件可能尚未加载，逐个判空后再改 Content
            if (tabAll != null) tabAll.Content = string.Format("全部 ({0})", all);
            if (tabRead != null) tabRead.Content = string.Format("只读 ({0})", read);
            if (tabWrite != null) tabWrite.Content = string.Format("可写 ({0})", write);
            if (tabStatus != null) tabStatus.Content = string.Format("状态点 ({0})", status);
            if (tabData != null) tabData.Content = string.Format("监控数据 ({0})", data);
        }

        /// <summary>刷新底栏统计。与页签角标共用同一套数字，避免两处对不上。</summary>
        private void UpdateFooter (int all, int read, int write) {
            // 底栏与 Tab 使用同一套统计，避免筛选后数字对不上
            if (txtFtAll != null) txtFtAll.Text = string.Format("共 {0} 个变量", all);
            if (txtFtRead != null) txtFtRead.Text = string.Format("只读 {0}", read);
            if (txtFtWrite != null) txtFtWrite.Text = string.Format("可写 {0}", write);
        }

        /// <summary>按 Id 在服务的变量列表里找源对象；找不到返回 null。</summary>
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
            /// <summary>属性变更通知，供 WPF 绑定刷新单元格。</summary>
            public event PropertyChangedEventHandler PropertyChanged;

            /// <summary>源变量。本行订阅它的 PropertyChanged，退订见 <see cref="Detach"/>。</summary>
            private VariableItem _source;
            /// <summary>当前读值的显示文本。</summary>
            private string _valueText;
            /// <summary>写入框的文本。</summary>
            private string _writeText;
            /// <summary>
            /// 操作员是否改动过写入框但尚未提交。
            /// </summary>
            /// <remarks>
            /// 用来挡住轮询覆盖：正在输入的内容不能被后台读值刷掉，
            /// 否则打到一半的数字会突然变成 PLC 的当前值。
            /// </remarks>
            private bool _writeDirty;
            /// <summary>写入框是否有焦点。与 dirty 一起决定能否被外部刷新。</summary>
            private bool _writeFocused;
            /// <summary>轮询开关的当前值。</summary>
            private bool _isPollingEnabled;

            /// <summary>变量 Id，按钮 Tag 与查找都用它。</summary>
            public string Id { get; set; }
            /// <summary>行号，从 1 起算，仅供显示。</summary>
            public int Index { get; set; }
            /// <summary>变量名。</summary>
            public string Name { get; set; }
            /// <summary>设备地址原文，本层不解析其格式。</summary>
            public string Address { get; set; }
            /// <summary>数据类型的显示文本。</summary>
            public string DataType { get; set; }
            /// <summary>访问权限的显示文本（只读 / 只写 / 读写）。</summary>
            public string AccessText { get; set; }
            /// <summary>工程单位；无单位时为空字符串。</summary>
            public string UnitText { get; set; }
            /// <summary>分类（状态点 / 监控数据），用于页签筛选。</summary>
            public string Category { get; set; }
            /// <summary>备注，鼠标悬停时显示。</summary>
            public string Description { get; set; }
            /// <summary>读值列是否可见——只写变量没有读值可显示。</summary>
            public Visibility ValueTextVisibility { get; set; }
            /// <summary>写入编辑器是否可见——只读变量不给写入入口，从源头杜绝误写。</summary>
            public Visibility WriteEditorVisibility { get; set; }
            /// <summary>备注 tooltip 是否可见；无备注时隐藏，避免弹出空气泡。</summary>
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

            /// <summary>轮询读回的当前值文本。相同值不发通知，减轻高频刷新下的绑定开销。</summary>
            public string ValueText {
                get { return _valueText; }
                set {
                    // 轮询高频刷新，相同文本不通知，减轻绑定开销
                    if (_valueText == value) return;
                    _valueText = value;
                    Raise(nameof(ValueText));
                }
            }

            /// <summary>写入框内容。一经修改即标记 dirty，防止轮询回写冲掉正在编辑的内容。</summary>
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

            /// <summary>记录写入框的焦点状态，由 XAML 的 GotFocus/LostFocus 调用。</summary>
            public void SetWriteFocused (bool focused) {
                _writeFocused = focused;
            }

            /// <summary>
            /// 写入框此刻能否被外部（轮询回填）覆盖。
            /// </summary>
            /// <remarks>
            /// 焦点在框内、或已改过内容尚未提交，都不允许覆盖——
            /// 否则操作员打到一半的数字会突然被 PLC 的当前值顶掉，
            /// 而且看不出是被改了，很容易误写出去。
            /// </remarks>
            public bool IsWriteEditable () {
                // 焦点在框内或已改过内容时，禁止用 LastValue 覆盖写入框
                return !_writeFocused && !_writeDirty;
            }

            /// <summary>提交写入后清掉 dirty，让写入框重新跟随读值。</summary>
            public void ClearWriteDirty () { _writeDirty = false; }

            /// <summary>
            /// 退订源变量，切断「轮询 → 本行」的刷新链路。幂等。
            /// </summary>
            /// <remarks>
            /// 置空 _source 而不只是退订：<see cref="OnSourcePropertyChanged"/> 会把刷新
            /// 封送到 Dispatcher，退订那一刻可能已有一次封送在途，靠判空拦住它。
            /// </remarks>
            public void Detach () {
                // 退订源对象，切断轮询 → UI 的刷新链路
                if (_source != null) {
                    _source.PropertyChanged -= OnSourcePropertyChanged;
                    _source = null;
                }
            }

            /// <summary>
            /// 由源变量构造一行，并订阅其读值变化。
            /// </summary>
            /// <param name="v">源变量。</param>
            /// <param name="index">显示行号，从 1 起算。</param>
            /// <remarks>
            /// 构造时就订阅，因此每个 Row 都必须配对调用 <see cref="Detach"/>——
            /// 见 <see cref="DetachAllRows"/>。
            /// </remarks>
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

            /// <summary>
            /// 源变量读值变化时刷新本行。
            /// </summary>
            /// <remarks>
            /// <para>
            /// <b>只关心 LastValue。</b>其它字段（名称、地址、类型）的变更走整表 Rebuild，
            /// 逐行刷新那些字段既无必要也容易与筛选状态不一致。
            /// </para>
            /// <para>
            /// <b>必须封送到 Dispatcher。</b>轮询在后台线程回填读值，
            /// 直接改绑定属性会抛跨线程访问异常。
            /// </para>
            /// </remarks>
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

            /// <summary>触发属性变更通知；无订阅者时不构造 EventArgs。</summary>
            private void Raise (string name) {
                PropertyChangedEventHandler h = PropertyChanged;
                // 无订阅者时不构造 EventArgs
                if (h != null)
                    h(this, new PropertyChangedEventArgs(name));
            }
        }
    }
}
