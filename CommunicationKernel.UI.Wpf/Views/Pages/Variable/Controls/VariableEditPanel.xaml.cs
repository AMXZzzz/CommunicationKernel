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
        /// <summary>请求收起弹层，本次不保存。</summary>
        public event Action CloseRequested;

        /// <summary>校验通过，请求页面取 <see cref="Build"/> 的结果去保存。</summary>
        public event Action SaveRequested;

        /// <summary>请求删除当前变量。确认与执行都在页面，本面板不落盘。</summary>
        public event Action DeleteRequested;

        /// <summary>校验/提示：(标题, 正文)，由页面用主题对话框展示。</summary>
        public event Action<string, string> InfoRequested;

        /// <summary>正在编辑的变量 Id；为空表示新增。</summary>
        private string _editingId;

        /// <summary>读写权限。界面是三个互斥按钮而非下拉，故用字段暂存。</summary>
        private VariableAccess _access = VariableAccess.ReadOnly;

        /// <summary>分类。决定 <see cref="cmbDataType"/> 里能选哪些数据类型。</summary>
        private string _category = "状态点";

        /// <summary>当前是否为「新增变量」。</summary>
        public bool IsNew => string.IsNullOrEmpty(_editingId);

        /// <summary>当前编辑中的变量 Id；新增模式为 null/空。</summary>
        public string EditingId => _editingId;

        /// <summary>构造：加载 XAML，并按默认分类（状态点）刷新按钮与类型列表。</summary>
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

        /// <summary>切到新增态：清空表单、隐藏删除按钮、回到「只读 + 状态点 + Bool」的默认。</summary>
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

        /// <summary>切到编辑态并填入变量。</summary>
        /// <param name="v">要编辑的变量；为 null 时退化为新增态。</param>
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

        /// <summary>从表单构建变量对象。</summary>
        /// <remarks>
        /// <b>不设 DeviceId</b>：面板不知道当前选中的是哪台设备，由页面补齐。
        /// </remarks>
        /// <returns>变量对象；名称或地址为空时返回 null。</returns>
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
        /// <param name="preferred">
        /// 希望保留的数据类型（编辑既有变量时传原值）。不在允许范围内则回落到第一项。
        /// 传 null 表示尽量留住下拉当前的选中项。
        /// </param>
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

        /// <summary>某分类下允许的数据类型。</summary>
        /// <param name="category">分类名。</param>
        /// <returns>允许的类型数组，至少一项——调用方依赖 <c>[0]</c> 作为兜底。</returns>
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

        /// <summary>读 / 写 / 读写 三选一按钮，目标权限由按钮 Tag 携带。</summary>
        /// <param name="sender">事件源，Tag 为 <c>WriteOnly</c> / <c>ReadWrite</c>，其余按只读处理。</param>
        /// <param name="e">事件参数。</param>
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

        /// <summary>分类三选一按钮，分类名由按钮 Tag 携带。改完连带重填数据类型列表。</summary>
        /// <param name="sender">事件源，Tag 为分类名。</param>
        /// <param name="e">事件参数。</param>
        private void BtnCategory_Click (object sender, RoutedEventArgs e) {
            string tag = (sender as FrameworkElement)?.Tag as string;
            // Tag：状态点 / 监控数据 / 轨道宽度
            if (!string.IsNullOrEmpty(tag))
                _category = tag;
            // 分类变化后刷新高亮，并重填允许的数据类型
            UpdateCategoryButtons();
            RefreshDataTypeList();
        }

        /// <summary>把三个权限按钮刷成当前选择的样式（选中 Primary，其余 Dark）。</summary>
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

        /// <summary>把三个分类按钮刷成当前选择的样式。</summary>
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

        /// <summary>「关闭」按钮：交给页面收起编辑弹层，不保存。</summary>
        /// <param name="sender">事件源。</param>
        /// <param name="e">事件参数。</param>
        private void BtnClose_Click (object sender, RoutedEventArgs e) =>
            CloseRequested?.Invoke();

        /// <summary>吞掉弹层内的左键点击，避免冒泡到遮罩把弹层关掉。</summary>
        /// <param name="sender">事件源。</param>
        /// <param name="e">事件参数。</param>
        private void Root_MouseLeftButtonDown (object sender, MouseButtonEventArgs e) =>
            e.Handled = true;

        /// <summary>
        /// 「保存」按钮：校验必填项后发事件。
        /// </summary>
        /// <remarks>
        /// 校验失败走 <see cref="InfoRequested"/> 明确提示，而不是静默 return——
        /// 静默失败会让操作员反复点保存却不知道差什么。
        /// </remarks>
        /// <param name="sender">事件源。</param>
        /// <param name="e">事件参数。</param>
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

        /// <summary>「删除」按钮：确认与执行都在页面，本面板只发事件。</summary>
        /// <param name="sender">事件源。</param>
        /// <param name="e">事件参数。</param>
        private void BtnDelete_Click (object sender, RoutedEventArgs e) =>
            DeleteRequested?.Invoke();
    }
}
