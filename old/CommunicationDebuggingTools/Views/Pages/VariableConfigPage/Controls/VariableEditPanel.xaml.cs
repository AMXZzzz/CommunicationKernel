using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using CommunicationDebuggingTools.Core.Enums;
using CommunicationDebuggingTools.Core.Models;

namespace CommunicationDebuggingTools.Views.VariableConfigPage.Controls {
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
            InitializeComponent();
            RefreshDataTypeList();
            UpdateAccessButtons();
            UpdateCategoryButtons();
        }

        public void PrepareNew () {
            _editingId = null;
            txtTitle.Text = "添加变量";
            if (btnSave != null)
                btnSave.Content = "添加";
            if (btnDelete != null)
                btnDelete.Visibility = Visibility.Collapsed;

            txtName.Text = "";
            txtAddress.Text = "";
            txtUnit.Text = "";
            txtDesc.Text = "";
            _access = VariableAccess.ReadOnly;
            _category = "状态点";
            UpdateAccessButtons();
            UpdateCategoryButtons();
            RefreshDataTypeList(preferred: VariableDataType.Bool);
        }

        public void Load (VariableItem v) {
            if (v == null) {
                PrepareNew();
                return;
            }

            _editingId = v.Id;
            txtTitle.Text = "编辑变量";
            if (btnSave != null)
                btnSave.Content = "保存";
            if (btnDelete != null)
                btnDelete.Visibility = Visibility.Visible;

            txtName.Text = v.Name ?? "";
            txtAddress.Text = v.Address ?? "";
            txtUnit.Text = v.Unit ?? "";
            txtDesc.Text = v.Description ?? "";
            _access = v.Access;
            _category = string.IsNullOrEmpty(v.Category) ? "状态点" : v.Category;
            UpdateAccessButtons();
            UpdateCategoryButtons();
            RefreshDataTypeList(preferred: v.DataType);
        }

        public VariableItem Build () {
            if (string.IsNullOrWhiteSpace(txtName.Text) ||
                string.IsNullOrWhiteSpace(txtAddress.Text))
                return null;

            VariableDataType dt = VariableDataType.Bool;
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

            if (!string.IsNullOrEmpty(_editingId))
                item.Id = _editingId;

            return item;
        }

        /// <summary>
        /// 按当前分类填充数据类型列表。
        /// 状态点：仅 Bool；其余：数值 + 浮点 + 字符串（不含 Bool，避免状态与数值混用）。
        /// </summary>
        private void RefreshDataTypeList (VariableDataType? preferred = null) {
            if (cmbDataType == null)
                return;

            VariableDataType[] allowed = GetAllowedDataTypes(_category);
            object keep = preferred.HasValue
                ? (object)preferred.Value
                : cmbDataType.SelectedItem;

            cmbDataType.Items.Clear();
            foreach (VariableDataType t in allowed)
                cmbDataType.Items.Add(t);

            if (keep is VariableDataType kd && Array.IndexOf(allowed, kd) >= 0)
                cmbDataType.SelectedItem = kd;
            else
                cmbDataType.SelectedItem = allowed[0];
        }

        private static VariableDataType[] GetAllowedDataTypes (string category) {
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
            if (!string.IsNullOrEmpty(tag))
                _category = tag;
            UpdateCategoryButtons();
            RefreshDataTypeList();
        }

        private void UpdateAccessButtons () {
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

        private void BtnClose_Click (object sender, RoutedEventArgs e) =>
            CloseRequested?.Invoke();

        private void Root_MouseLeftButtonDown (object sender, MouseButtonEventArgs e) =>
            e.Handled = true;

        private void BtnSave_Click (object sender, RoutedEventArgs e) {
            if (string.IsNullOrWhiteSpace(txtName != null ? txtName.Text : null)) {
                InfoRequested?.Invoke("提示", "请填写显示名称");
                return;
            }
            if (string.IsNullOrWhiteSpace(txtAddress != null ? txtAddress.Text : null)) {
                InfoRequested?.Invoke("提示", "请填写地址");
                return;
            }
            SaveRequested?.Invoke();
        }

        private void BtnDelete_Click (object sender, RoutedEventArgs e) =>
            DeleteRequested?.Invoke();
    }
}
