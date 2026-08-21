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
            InitializeComponent();
            UpdateScopeButtons();
            UpdateFormatButtons();
        }

        /// <summary>打开前注入当前设备信息。</summary>
        public void Prepare (string deviceId, string deviceTitle, int variableCount) {
            _deviceId = deviceId;
            _scopeCurrent = true;
            UpdateScopeButtons();

            txtScopeHint.Text = string.IsNullOrEmpty(deviceTitle)
                ? ""
                : ("当前设备：" + deviceTitle + " · " + variableCount + " 个变量");

            string safe = SanitizeFileName(
                string.IsNullOrEmpty(deviceTitle) ? "变量" : deviceTitle.Split('·')[0].Trim());
            txtFileName.Text = safe + "_变量_" + DateTime.Now.ToString("yyyyMMdd");
        }

        private void BtnScope_Click (object sender, RoutedEventArgs e) {
            string tag = (sender as FrameworkElement)?.Tag as string;
            _scopeCurrent = tag != "All";
            UpdateScopeButtons();
        }

        private void BtnFormat_Click (object sender, RoutedEventArgs e) =>
            UpdateFormatButtons();

        private void UpdateScopeButtons () {
            Style dark = TryFindResource("SF.Style.DarkButton") as Style;
            Style primary = TryFindResource("SF.Style.PrimaryButton") as Style;
            btnScopeCurrent.Style = _scopeCurrent ? primary : dark;
            btnScopeAll.Style = !_scopeCurrent ? primary : dark;
        }

        private void UpdateFormatButtons () {
            Style primary = TryFindResource("SF.Style.PrimaryButton") as Style;
            if (btnFmtJson != null)
                btnFmtJson.Style = primary;
        }

        private void BtnClose_Click (object sender, RoutedEventArgs e) =>
            CloseRequested?.Invoke();

        private void BtnExport_Click (object sender, RoutedEventArgs e) {
            if (VariableService == null)
                return;

            IEnumerable<VariableItem> query = VariableService.Variables
                .Where(v => v != null);

            if (_scopeCurrent) {
                if (string.IsNullOrEmpty(_deviceId)) {
                    InfoRequested?.Invoke("提示", "请先选择设备");
                    return;
                }
                query = query.Where(v => v.DeviceId == _deviceId);
            }

            List<VariableItem> list = query.ToList();
            if (list.Count == 0) {
                InfoRequested?.Invoke("提示", "没有可导出的变量");
                return;
            }

            string name = (txtFileName.Text ?? "").Trim();
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

            Window owner = Window.GetWindow(this);
            bool? ok = owner != null
                ? dlg.ShowDialog(owner)
                : dlg.ShowDialog();

            if (ok != true)
                return;

            try {
                string json = BuildJson(list);
                File.WriteAllText(dlg.FileName, json, new UTF8Encoding(false));
                CloseRequested?.Invoke();
                ExportSucceeded?.Invoke(dlg.FileName, list.Count);
            } catch (Exception ex) {
                InfoRequested?.Invoke("导出失败", ex.Message);
            }
        }

        private string BuildJson (List<VariableItem> list) {
            var sb = new StringBuilder();
            sb.Append("[\n");
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
                if (i < list.Count - 1) sb.Append(",");
                sb.Append("\n");
            }
            sb.Append("]\n");
            return sb.ToString();
        }

        private static void Append (StringBuilder sb, ref bool first, string key, string value) {
            if (!first) sb.Append(", ");
            first = false;
            sb.Append("\"").Append(key).Append("\": \"")
              .Append(Escape(value ?? "")).Append("\"");
        }

        private static string Escape (string s) =>
            s.Replace("\\", "\\\\").Replace("\"", "\\\"").Replace("\n", "\\n").Replace("\r", "\\r");

        private static string AccessToString (VariableAccess a) {
            if (a == VariableAccess.ReadOnly) return "R";
            if (a == VariableAccess.WriteOnly) return "W";
            return "R/W";
        }

        private static string SanitizeFileName (string name) {
            foreach (char c in Path.GetInvalidFileNameChars())
                name = name.Replace(c, '_');
            return string.IsNullOrWhiteSpace(name) ? "export" : name.Trim();
        }
    }
}