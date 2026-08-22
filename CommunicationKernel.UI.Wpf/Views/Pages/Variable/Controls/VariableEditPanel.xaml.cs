#nullable disable

// -----------------------------------------------------------------------------
// 文件: Views/Pages/Variable/Controls/VariableEditPanel.xaml.cs
// 层级: UI 层 — WPF Views
// 作用: 添加/编辑变量弹层：分类决定可选数据类型，校验后交给页面保存。
// -----------------------------------------------------------------------------

using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using CommunicationKernel.UI.Wpf.Core.Enums;
using CommunicationKernel.UI.Wpf.Core.Models;

namespace CommunicationKernel.UI.Wpf.Views.Pages.Variable.Controls {
    /// <summary>
    /// 添加/编辑变量弹层。
    /// 分类决定可选数据类型：状态点仅 Bool；监控/轨道宽度为数值与字符串。
    /// </summary>
    public partial class VariableEditPanel : UserControl {
        public event Action CloseRequested;
        public event Action SaveRequested;
        public event Action DeleteRequested;
        /// <summary>校验/提示：(标题, 正文)，由页面用主题对话框展示。</summary>
        public event Action<string, string> InfoRequested;

        private string _editingId;
        private VariableAccess _access = VariableAccess.ReadOnly;
        private string _category = "状态点";

        public bool IsNew => string.IsNullOrEmpty(_editingId);

        /// <summary>当前编辑中的变量 Id；新增模式为 null/空。</summary>
        public string EditingId => _editingId;

        public VariableEditPanel () {
            // 解析 XAML 并按默认分类刷新按钮/类型列表
            InitializeComponent();
            // 默认「状态点」：只读 + Bool，三组按钮同步高亮
            RefreshDataTypeList();
            UpdateAccessButtons();
            UpdateCategoryButtons();
        }

        // ============================================================================
        // 打开 / 构建
        // ============================================================================

        public void PrepareNew () {
            // 清空 Id，保存时走新增
            _editingId = null;
            txtTitle.Text = "添加变量";
            if (btnSave != null)
                btnSave.Content = "添加";
            // 新增没有可删对象，隐藏删除按钮
            if (btnDelete != null)
                btnDelete.Visibility = Visibility.Collapsed;

            txtName.Text = "";
            txtAddress.Text = "";
            txtUnit.Text = "";
            txtDesc.Text = "";
            // 默认只读状态点，数据类型刷成 Bool
            _access = VariableAccess.ReadOnly;
            _category = "状态点";
            UpdateAccessButtons();
            UpdateCategoryButtons();
            RefreshDataTypeList(preferred: VariableDataType.Bool);
        }

        public void Load (VariableItem v) {
            // 空对象按新增处理，避免半填充表单
            if (v == null) {
                PrepareNew();
                return;
            }

            _editingId = v.Id;
            txtTitle.Text = "编辑变量";
            if (btnSave != null)
                btnSave.Content = "保存";
            // 编辑已有变量时显示删除
            if (btnDelete != null)
                btnDelete.Visibility = Visibility.Visible;

            txtName.Text = v.Name ?? "";
            txtAddress.Text = v.Address ?? "";
            txtUnit.Text = v.Unit ?? "";
            txtDesc.Text = v.Description ?? "";
            _access = v.Access;
            // 旧数据可能没分类，回落到状态点
            _category = string.IsNullOrEmpty(v.Category) ? "状态点" : v.Category;
            UpdateAccessButtons();
            UpdateCategoryButtons();
            // 尽量保留原数据类型；若与分类不允许则 Refresh 内会回落到允许的第一项
            RefreshDataTypeList(preferred: v.DataType);
        }

        public VariableItem Build () {
            // 名称和地址都是必填
            if (string.IsNullOrWhiteSpace(txtName.Text) ||
                string.IsNullOrWhiteSpace(txtAddress.Text))
                return null;

            VariableDataType dt = VariableDataType.Bool;
            // 下拉选中项优先；状态点以外未选时默认 Int16
            if (cmbDataType.SelectedItem is VariableDataType selected)
                dt = selected;
            else if (_category != "状态点")
                dt = VariableDataType.Int16;

            var item = new VariableItem {
                Name = (txtName.Text ?? "").Trim(),
                Address = (txtAddress.Text ?? "").Trim(),
                Unit = (txtUnit.Text ?? "").Trim(),
                Description = (txtDesc.Text ?? "").Trim(),
                DataType = dt,
                Access = _access,
                Category = _category
            };

            // 编辑模式带回原 Id，页面据此走 Update 而不是 Add
            if (!string.IsNullOrEmpty(_editingId))
                item.Id = _editingId;

            return item;
        }

        // ============================================================================
        // 分类 / 权限按钮
        // ============================================================================

        /// <summary>
        /// 按当前分类填充数据类型列表。
        /// 状态点：仅 Bool；其余：数值 + 浮点 + 字符串（不含 Bool，避免状态与数值混用）。
        /// </summary>
        private void RefreshDataTypeList (VariableDataType? preferred = null) {
            // 下拉尚未生成时跳过，避免构造期 NRE
            if (cmbDataType == null)
                return;

            VariableDataType[] allowed = GetAllowedDataTypes(_category);
            // 优先保留调用方指定类型，否则尽量留住当前选中项
            object keep = preferred.HasValue
                ? (object)preferred.Value
                : cmbDataType.SelectedItem;

            cmbDataType.Items.Clear();
            // 按当前分类重填，状态点只有 Bool
            foreach (VariableDataType t in allowed)
                cmbDataType.Items.Add(t);

            // 原类型仍在允许列表里就保住，否则落到第一项（状态点=Bool）
            if (keep is VariableDataType kd && Array.IndexOf(allowed, kd) >= 0)
                cmbDataType.SelectedItem = kd;
            else
                cmbDataType.SelectedItem = allowed[0];
        }

        private static VariableDataType[] GetAllowedDataTypes (string category) {
            // 状态点只给 Bool，避免和下拉数值类型混用
            if (category == "状态点")
                return new[] { VariableDataType.Bool };

            // 监控数据 / 轨道宽度：可读写数值与字符串
            return new[] {
                VariableDataType.Int16,
                VariableDataType.UInt16,
                VariableDataType.Int32,
                VariableDataType.UInt32,
                VariableDataType.Int64,
                VariableDataType.UInt64,
                VariableDataType.Float,
                VariableDataType.Double,
                VariableDataType.String
            };
        }

        private void BtnAccess_Click (object sender, RoutedEventArgs e) {
            string tag = (sender as FrameworkElement)?.Tag as string;
            // Tag 约定：WriteOnly / ReadWrite / 其它=只读
            if (tag == "WriteOnly")
                _access = VariableAccess.WriteOnly;
            else if (tag == "ReadWrite")
                _access = VariableAccess.ReadWrite;
            else
                _access = VariableAccess.ReadOnly;
            UpdateAccessButtons();
        }

        private void BtnCategory_Click (object sender, RoutedEventArgs e) {
            string tag = (sender as FrameworkElement)?.Tag as string;
            // Tag：状态点 / 监控数据 / 轨道宽度
            if (!string.IsNullOrEmpty(tag))
                _category = tag;
            // 分类变化后刷新高亮，并重填允许的数据类型
            UpdateCategoryButtons();
            RefreshDataTypeList();
        }

        private void UpdateAccessButtons () {
            // 选中项 Primary，其余 Dark，和批量添加/导出范围按钮同一套资源
            Style dark = TryFindResource("SF.Style.DarkButton") as Style;
            Style primary = TryFindResource("SF.Style.PrimaryButton") as Style;
            if (btnAccR != null)
                btnAccR.Style = _access == VariableAccess.ReadOnly ? primary : dark;
            if (btnAccW != null)
                btnAccW.Style = _access == VariableAccess.WriteOnly ? primary : dark;
            if (btnAccRW != null)
                btnAccRW.Style = _access == VariableAccess.ReadWrite ? primary : dark;
        }

        private void UpdateCategoryButtons () {
            Style dark = TryFindResource("SF.Style.DarkButton") as Style;
            Style primary = TryFindResource("SF.Style.PrimaryButton") as Style;
            if (btnCatStatus != null)
                btnCatStatus.Style = _category == "状态点" ? primary : dark;
            if (btnCatData != null)
                btnCatData.Style = _category == "监控数据" ? primary : dark;
            if (btnCatWidth != null)
                btnCatWidth.Style = _category == "轨道宽度" ? primary : dark;
        }

        // ============================================================================
        // 关闭 / 保存
        // ============================================================================

        // 交给页面收起编辑弹层，不保存
        private void BtnClose_Click (object sender, RoutedEventArgs e) =>
            CloseRequested?.Invoke();

        // 拦点击，避免穿透到遮罩把弹层关掉
        private void Root_MouseLeftButtonDown (object sender, MouseButtonEventArgs e) =>
            e.Handled = true;

        private void BtnSave_Click (object sender, RoutedEventArgs e) {
            // 名称、地址均为必填，失败时走主题提示而不是静默 return
            if (string.IsNullOrWhiteSpace(txtName != null ? txtName.Text : null)) {
                InfoRequested?.Invoke("提示", "请填写显示名称");
                return;
            }
            if (string.IsNullOrWhiteSpace(txtAddress != null ? txtAddress.Text : null)) {
                InfoRequested?.Invoke("提示", "请填写地址");
                return;
            }
            // 校验通过，由页面 Build + SaveVariable
            SaveRequested?.Invoke();
        }

        // 删除确认/执行都在页面，本面板只发事件
        private void BtnDelete_Click (object sender, RoutedEventArgs e) =>
            DeleteRequested?.Invoke();
    }
}
