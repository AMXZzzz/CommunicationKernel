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

        private string _deviceId;
        private bool _scopeCurrent = true;

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

        private void BtnScope_Click (object sender, RoutedEventArgs e) {
            // Tag=All 导出全部设备，否则只导当前设备
            string tag = (sender as FrameworkElement)?.Tag as string;
            _scopeCurrent = tag != "All";
            UpdateScopeButtons();
        }

        private void BtnFormat_Click (object sender, RoutedEventArgs e) =>
            UpdateFormatButtons();

        private void UpdateScopeButtons () {
            // 选中项用 Primary，未选中用 Dark，和变量页其它切换按钮一致
            Style dark = TryFindResource("SF.Style.DarkButton") as Style;
            Style primary = TryFindResource("SF.Style.PrimaryButton") as Style;
            btnScopeCurrent.Style = _scopeCurrent ? primary : dark;
            btnScopeAll.Style = !_scopeCurrent ? primary : dark;
        }

        private void UpdateFormatButtons () {
            // 一期仅 JSON，始终高亮 JSON 按钮
            Style primary = TryFindResource("SF.Style.PrimaryButton") as Style;
            if (btnFmtJson != null)
                btnFmtJson.Style = primary;
        }

        // 交给页面收起导出弹层，不写文件
        private void BtnClose_Click (object sender, RoutedEventArgs e) =>
            CloseRequested?.Invoke();

        // ============================================================================
        // 导出
        // ============================================================================

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

        private static void Append (StringBuilder sb, ref bool first, string key, string value) {
            // 字段之间用逗号分隔，首字段前面不加
            if (!first) sb.Append(", ");
            first = false;
            sb.Append("\"").Append(key).Append("\": \"")
              .Append(Escape(value ?? "")).Append("\"");
        }

        private static string Escape (string s) =>
            s.Replace("\\", "\\\\").Replace("\"", "\\\"").Replace("\n", "\\n").Replace("\r", "\\r");

        private static string AccessToString (VariableAccess a) {
            // 与导入面板约定同一套文案：R / W / R/W
            if (a == VariableAccess.ReadOnly) return "R";
            if (a == VariableAccess.WriteOnly) return "W";
            return "R/W";
        }

        private static string SanitizeFileName (string name) {
            // 设备名可能含 · / : 等，替换成下划线以免保存失败
            foreach (char c in Path.GetInvalidFileNameChars())
                name = name.Replace(c, '_');
            return string.IsNullOrWhiteSpace(name) ? "export" : name.Trim();
        }
    }
}