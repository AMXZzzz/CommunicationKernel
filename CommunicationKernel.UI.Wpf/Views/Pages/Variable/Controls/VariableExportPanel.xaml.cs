#nullable disable

// -----------------------------------------------------------------------------
// 文件: Views/Pages/Variable/Controls/VariableExportPanel.xaml.cs
// 层级: UI 层 — WPF Views
// 作用: 导出变量弹层（一期仅 JSON）；成功路径通过事件交给页面展示。
// -----------------------------------------------------------------------------

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using CommunicationKernel.UI.Wpf.Core.Enums;
using CommunicationKernel.UI.Wpf.Core.Models;
using CommunicationKernel.UI.Wpf.Core.Interfaces;
using Microsoft.Win32;

namespace CommunicationKernel.UI.Wpf.Views.Pages.Variable.Controls {
    /// <summary>
    /// 导出变量弹层（一期仅 JSON）。
    /// 成功后通过 <see cref="ExportSucceeded"/> 通知页面弹主题成功框
    /// 服务由页面注入 <see cref="VariableService"/>
    /// </summary>
    public partial class VariableExportPanel : UserControl {
        /// <summary>由页面注入。</summary>
        public IVariableService VariableService { get; set; }

        /// <summary>请求关闭本面板。</summary>
        public event Action CloseRequested;

        /// <summary>导出成功：(完整路径, 条数)。</summary>
        public event Action<string, int> ExportSucceeded;

        /// <summary>需页面用主题框提示：(标题, 正文)。</summary>
        public event Action<string, string> InfoRequested;

        /// <summary>当前选中的设备 Id，「当前设备」范围据此过滤。</summary>
        private string _deviceId;

        /// <summary>导出范围：true = 仅当前设备，false = 全部设备。</summary>
        private bool _scopeCurrent = true;

        /// <summary>构造：加载 XAML 并同步范围/格式按钮高亮。</summary>
        public VariableExportPanel () {
            // 解析 XAML 并同步范围/格式按钮高亮
            InitializeComponent();
            // 默认「当前设备」+ JSON 高亮，与 Prepare 的初始状态一致
            UpdateScopeButtons();
            UpdateFormatButtons();
        }

        // ============================================================================
        // 打开 / 范围
        // ============================================================================

        /// <summary>打开前注入当前设备信息。</summary>
        /// <param name="deviceId">当前设备 Id。</param>
        /// <param name="deviceTitle">当前设备显示名，用于提示条与默认文件名。</param>
        /// <param name="variableCount">该设备下的变量数，仅用于提示条。</param>
        public void Prepare (string deviceId, string deviceTitle, int variableCount) {
            // 记住当前设备，默认范围切回「当前设备」
            _deviceId = deviceId;
            _scopeCurrent = true;
            UpdateScopeButtons();

            // 提示条展示设备名和变量数，未选设备时留空
            txtScopeHint.Text = string.IsNullOrEmpty(deviceTitle)
                ? ""
                : ("当前设备：" + deviceTitle + " · " + variableCount + " 个变量");

            // 默认文件名：设备名_变量_日期；非法字符先替换，避免保存对话框报错
            string safe = SanitizeFileName(
                string.IsNullOrEmpty(deviceTitle) ? "变量" : deviceTitle.Split('·')[0].Trim());
            txtFileName.Text = safe + "_变量_" + DateTime.Now.ToString("yyyyMMdd");
        }

        /// <summary>导出范围切换，目标范围由按钮 Tag 携带。</summary>
        /// <param name="sender">事件源，Tag 为 <c>All</c> 表示全部设备，其余表示当前设备。</param>
        /// <param name="e">事件参数。</param>
        private void BtnScope_Click (object sender, RoutedEventArgs e) {
            // Tag=All 导出全部设备，否则只导当前设备
            string tag = (sender as FrameworkElement)?.Tag as string;
            _scopeCurrent = tag != "All";
            UpdateScopeButtons();
        }

        /// <summary>格式切换。一期只有 JSON，点了也只是刷新高亮。</summary>
        /// <param name="sender">事件源。</param>
        /// <param name="e">事件参数。</param>
        private void BtnFormat_Click (object sender, RoutedEventArgs e) =>
            UpdateFormatButtons();

        /// <summary>把两个范围按钮刷成当前选择的样式。</summary>
        private void UpdateScopeButtons () {
            // 选中项用 Primary，未选中用 Dark，和变量页其它切换按钮一致
            Style dark = TryFindResource("SF.Style.DarkButton") as Style;
            Style primary = TryFindResource("SF.Style.PrimaryButton") as Style;
            btnScopeCurrent.Style = _scopeCurrent ? primary : dark;
            btnScopeAll.Style = !_scopeCurrent ? primary : dark;
        }

        /// <summary>刷新格式按钮高亮。一期恒为 JSON。</summary>
        private void UpdateFormatButtons () {
            // 一期仅 JSON，始终高亮 JSON 按钮
            Style primary = TryFindResource("SF.Style.PrimaryButton") as Style;
            if (btnFmtJson != null)
                btnFmtJson.Style = primary;
        }

        /// <summary>「关闭」按钮：交给页面收起导出弹层，不写文件。</summary>
        /// <param name="sender">事件源。</param>
        /// <param name="e">事件参数。</param>
        private void BtnClose_Click (object sender, RoutedEventArgs e) =>
            CloseRequested?.Invoke();

        // ============================================================================
        // 导出
        // ============================================================================

        /// <summary>
        /// 「导出」按钮：按范围取变量、弹保存框、写文件。
        /// </summary>
        /// <remarks>
        /// 成功后<b>先关弹层再报成功</b>：顺序反过来的话，成功框会被弹层遮住一部分，
        /// 而成功框里带「打开目录」，点不到就等于没有。
        /// 失败则保持弹层打开，方便改个文件名直接重试。
        /// </remarks>
        /// <param name="sender">事件源。</param>
        /// <param name="e">事件参数。</param>
        private void BtnExport_Click (object sender, RoutedEventArgs e) {
            // 服务未注入时无法取变量列表
            if (VariableService == null)
                return;

            // 先丢掉 null 项，再按范围收窄
            IEnumerable<VariableItem> query = VariableService.Variables
                .Where(v => v != null);

            if (_scopeCurrent) {
                // 当前设备模式必须已选设备
                if (string.IsNullOrEmpty(_deviceId)) {
                    InfoRequested?.Invoke("提示", "请先选择设备");
                    return;
                }
                // 只导出挂在当前 RouteId 下的变量
                query = query.Where(v => v.DeviceId == _deviceId);
            }

            List<VariableItem> list = query.ToList();
            // 范围内没有变量时不弹出保存框
            if (list.Count == 0) {
                InfoRequested?.Invoke("提示", "没有可导出的变量");
                return;
            }

            string name = (txtFileName.Text ?? "").Trim();
            // 文件名为空时给默认名，并强制 .json 后缀
            if (string.IsNullOrEmpty(name))
                name = "variables_export";
            if (!name.EndsWith(".json", StringComparison.OrdinalIgnoreCase))
                name += ".json";

            var dlg = new SaveFileDialog
            {
                Filter = "JSON (*.json)|*.json",
                FileName = name,
                AddExtension = true,
                DefaultExt = ".json"
            };

            // 以主窗口为 owner，防止对话框落到主窗口后面
            Window owner = Window.GetWindow(this);
            bool? ok = owner != null
                ? dlg.ShowDialog(owner)
                : dlg.ShowDialog();

            // 取消保存则保持弹层打开
            if (ok != true)
                return;

            try {
                // 写出无 BOM 的 UTF-8 JSON
                string json = BuildJson(list);
                File.WriteAllText(dlg.FileName, json, new UTF8Encoding(false));
                // 先关弹层，再让页面弹成功框（含「打开目录」）
                CloseRequested?.Invoke();
                ExportSucceeded?.Invoke(dlg.FileName, list.Count);
            } catch (Exception ex) {
                // 磁盘权限/占用等失败走主题提示，不关弹层方便重试
                InfoRequested?.Invoke("导出失败", ex.Message);
            }
        }

        // ============================================================================
        // JSON 序列化
        // ============================================================================

        /// <summary>把变量列表拼成 JSON 数组文本。</summary>
        /// <remarks>
        /// 手写拼串而非用序列化器：字段是否输出由界面上的复选框逐项决定，
        /// 用 <c>JsonSerializer</c> 得为每种勾选组合建一个类型或写自定义转换器，
        /// 反而更绕。字段少、结构扁平，值一律按字符串写出并转义。
        /// <para>
        /// <c>deviceId</c> 无视勾选<b>始终写出</b>：没有它，导出的文件再导入时
        /// 不知道该挂到哪台设备上。
        /// </para>
        /// </remarks>
        /// <param name="list">要导出的变量。</param>
        /// <returns>JSON 文本。</returns>
        private string BuildJson (List<VariableItem> list) {
            var sb = new StringBuilder();
            sb.Append("[\n");
            // 按勾选字段拼扁平对象，deviceId 始终写出便于再导入
            for (int i = 0; i < list.Count; i++) {
                VariableItem v = list[i];
                sb.Append("  {");
                bool first = true;
                Append(sb, ref first, "deviceId", v.DeviceId);
                if (chkName.IsChecked == true) Append(sb, ref first, "name", v.Name);
                if (chkAddress.IsChecked == true) Append(sb, ref first, "address", v.Address);
                if (chkType.IsChecked == true) Append(sb, ref first, "dataType", v.DataType.ToString());
                if (chkAccess.IsChecked == true) Append(sb, ref first, "access", AccessToString(v.Access));
                if (chkUnit.IsChecked == true) Append(sb, ref first, "unit", v.Unit ?? "");
                if (chkCategory.IsChecked == true) Append(sb, ref first, "category", v.Category ?? "");
                if (chkDesc.IsChecked == true) Append(sb, ref first, "description", v.Description ?? "");
                sb.Append("}");
                // 末项不加逗号，保持合法 JSON 数组
                if (i < list.Count - 1) sb.Append(",");
                sb.Append("\n");
            }
            sb.Append("]\n");
            return sb.ToString();
        }

        /// <summary>追加一个 <c>"键": "值"</c> 对，自动处理字段间的逗号。</summary>
        /// <param name="sb">目标缓冲。</param>
        /// <param name="first">是否为本对象的首个字段；调用后被置为 false。</param>
        /// <param name="key">字段名。</param>
        /// <param name="value">字段值，null 按空串处理。</param>
        private static void Append (StringBuilder sb, ref bool first, string key, string value) {
            // 字段之间用逗号分隔，首字段前面不加
            if (!first) sb.Append(", ");
            first = false;
            sb.Append("\"").Append(key).Append("\": \"")
              .Append(Escape(value ?? "")).Append("\"");
        }

        /// <summary>转义 JSON 字符串值。</summary>
        /// <remarks>
        /// 反斜杠必须<b>第一个</b>替换：放到后面会把前几步刚插入的转义符再转义一遍。
        /// 只处理这四类——备注里可能带换行和引号，其余字符按 UTF-8 原样写出。
        /// </remarks>
        /// <param name="s">原始值。</param>
        /// <returns>可直接放进双引号里的文本。</returns>
        private static string Escape (string s) =>
            s.Replace("\\", "\\\\").Replace("\"", "\\\"").Replace("\n", "\\n").Replace("\r", "\\r");

        /// <summary>权限枚举 → 导出文案。与导入面板约定同一套：R / W / R/W。</summary>
        /// <param name="a">权限。</param>
        /// <returns>文案。</returns>
        private static string AccessToString (VariableAccess a) {
            // 与导入面板约定同一套文案：R / W / R/W
            if (a == VariableAccess.ReadOnly) return "R";
            if (a == VariableAccess.WriteOnly) return "W";
            return "R/W";
        }

        /// <summary>把设备名清洗成合法文件名。</summary>
        /// <param name="name">原始名称。设备名常含 <c>·</c>、<c>/</c>、<c>:</c> 等。</param>
        /// <returns>非法字符替换为下划线后的名称；清洗后为空时返回 <c>"export"</c>。</returns>
        private static string SanitizeFileName (string name) {
            // 设备名可能含 · / : 等，替换成下划线以免保存失败
            foreach (char c in Path.GetInvalidFileNameChars())
                name = name.Replace(c, '_');
            return string.IsNullOrWhiteSpace(name) ? "export" : name.Trim();
        }
    }
}