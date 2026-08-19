using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using CommunicationDebuggingTools.Core.Enums;
using CommunicationDebuggingTools.Core.Models;
using CommunicationDebuggingTools.Core.Interfaces;
using Microsoft.Win32;

namespace CommunicationDebuggingTools.Views.VariableConfigPage.Controls {
    /// <summary>
    /// 导入变量弹层（一期 JSON）。
    /// 清空确认 / 成功提示由页面通过事件用 AppMessageDialog 展示。
    /// 服务由页面注入 <see cref="VariableService"/> / <see cref="DeviceService"/>
    /// </summary>
    public partial class VariableImportPanel : UserControl {
        /// <summary>由页面注入。</summary>
        public IVariableService VariableService { get; set; }

        /// <summary>由页面注入。</summary>
        public IDeviceService DeviceService { get; set; }

        public event Action CloseRequested;

        /// <summary>需要清空时：页面弹 Warning，确认后调用 <see cref="ExecuteImport"/>。</summary>
        public event Action<string, string> ConfirmClearRequested;

        /// <summary>导入成功（条数）。</summary>
        public event Action<int> ImportSucceeded;

        /// <summary>Info / 错误：(标题, 正文)。</summary>
        public event Action<string, string> InfoRequested;

        private string _deviceId;
        private bool _scopeCurrent = true;
        private string _filePath;
        private readonly List<VariableItem> _accepted = new List<VariableItem>();
        private int _skipCount;

        public VariableImportPanel () {
            InitializeComponent();
            UpdateScopeButtons();
        }

        public void Prepare (string deviceId, string deviceTitle) {
            _deviceId = deviceId;
            _scopeCurrent = true;
            _filePath = null;
            _accepted.Clear();
            _skipCount = 0;

            UpdateScopeButtons();
            txtScopeHint.Text = string.IsNullOrEmpty(deviceTitle)
                ? ""
                : ("当前设备：" + deviceTitle);
            txtFileName.Text = "未选择文件";
            txtPreview.Text = "请先选择 JSON 文件";
            txtPreview.Foreground =
                TryFindResource("SF.Brush.Text.Secondary") as System.Windows.Media.Brush;
            btnImport.IsEnabled = false;
            chkClear.IsChecked = false;
        }

        private void BtnScope_Click (object sender, RoutedEventArgs e) {
            string tag = (sender as FrameworkElement)?.Tag as string;
            _scopeCurrent = tag != "All";
            UpdateScopeButtons();
            if (!string.IsNullOrEmpty(_filePath))
                RunPreview();
        }

        private void UpdateScopeButtons () {
            Style dark = TryFindResource("SF.Style.DarkButton") as Style;
            Style primary = TryFindResource("SF.Style.PrimaryButton") as Style;
            btnScopeCurrent.Style = _scopeCurrent ? primary : dark;
            btnScopeAll.Style = !_scopeCurrent ? primary : dark;
        }

        private void DropZone_Click (object sender, MouseButtonEventArgs e) => PickFile();

        private void PickFile () {
            var dlg = new OpenFileDialog
            {
                Filter = "JSON (*.json)|*.json",
                CheckFileExists = true
            };

            Window owner = Window.GetWindow(this);
            bool? ok = owner != null ? dlg.ShowDialog(owner) : dlg.ShowDialog();
            if (ok != true)
                return;

            _filePath = dlg.FileName;
            txtFileName.Text = Path.GetFileName(_filePath);
            RunPreview();
        }

        private void RunPreview () {
            _accepted.Clear();
            _skipCount = 0;
            btnImport.IsEnabled = false;

            if (string.IsNullOrEmpty(_filePath) || !File.Exists(_filePath)) {
                txtPreview.Text = "请先选择 JSON 文件";
                return;
            }

            if (_scopeCurrent && string.IsNullOrEmpty(_deviceId)) {
                txtPreview.Text = "当前设备模式需要先在左侧选择设备";
                return;
            }

            string text;
            try {
                text = File.ReadAllText(_filePath);
            } catch (Exception ex) {
                txtPreview.Text = "读取失败：" + ex.Message;
                return;
            }

            List<RawItem> raws = ParseArray(text);
            if (raws == null) {
                txtPreview.Text = "JSON 格式无效，需要数组 [...]";
                return;
            }

            HashSet<string> existingKeys = BuildExistingKeys();
            HashSet<string> batchKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            foreach (RawItem r in raws) {
                if (!TryMap(r, out VariableItem item, out _)) {
                    _skipCount++;
                    continue;
                }

                string key = item.DeviceId + "|" + item.Address;
                if (existingKeys.Contains(key) || batchKeys.Contains(key)) {
                    _skipCount++;
                    continue;
                }

                batchKeys.Add(key);
                _accepted.Add(item);
            }

            txtPreview.Text = string.Format(
                "✓ 可导入 {0} 条\n⚠ 跳过 {1} 条（缺字段/地址重复/设备无效）",
                _accepted.Count, _skipCount);
            btnImport.IsEnabled = _accepted.Count > 0;
        }

        private HashSet<string> BuildExistingKeys () {
            var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            if (VariableService == null)
                return set;

            foreach (VariableItem v in VariableService.Variables) {
                if (v == null || string.IsNullOrEmpty(v.Address)) continue;
                if (_scopeCurrent && v.DeviceId != _deviceId) continue;
                set.Add(v.DeviceId + "|" + v.Address);
            }
            return set;
        }

        private bool TryMap (RawItem r, out VariableItem item, out string fail) {
            item = null;
            fail = null;

            string name = (r.Name ?? "").Trim();
            string address = (r.Address ?? "").Trim();
            if (string.IsNullOrEmpty(name) || string.IsNullOrEmpty(address)) {
                fail = "name/address";
                return false;
            }

            string deviceId;
            if (_scopeCurrent) {
                deviceId = _deviceId;
            } else {
                deviceId = (r.DeviceId ?? "").Trim();
                if (string.IsNullOrEmpty(deviceId) ||
                    DeviceService == null ||
                    DeviceService.Devices.All(d => d == null || d.Id != deviceId)) {
                    fail = "deviceId";
                    return false;
                }
            }

            item = new VariableItem {
                DeviceId = deviceId,
                Name = name,
                Address = address,
                DataType = ParseDataType(r.DataType),
                Access = ParseAccess(r.Access),
                Unit = (r.Unit ?? "").Trim(),
                Category = string.IsNullOrWhiteSpace(r.Category) ? "状态点" : r.Category.Trim(),
                Description = (r.Description ?? "").Trim()
            };
            return true;
        }

        private static VariableDataType ParseDataType (string s) {
            if (string.IsNullOrWhiteSpace(s)) return VariableDataType.Int16;
            return Enum.TryParse(s.Trim(), true, out VariableDataType t) ? t : VariableDataType.Int16;
        }

        private static VariableAccess ParseAccess (string s) {
            if (string.IsNullOrWhiteSpace(s)) return VariableAccess.ReadWrite;
            s = s.Trim().ToUpperInvariant();
            if (s == "R" || s == "READONLY") return VariableAccess.ReadOnly;
            if (s == "W" || s == "WRITEONLY") return VariableAccess.WriteOnly;
            return VariableAccess.ReadWrite;
        }

        private static List<RawItem> ParseArray (string text) {
            if (string.IsNullOrWhiteSpace(text)) return null;
            text = text.Trim();
            if (!text.StartsWith("[") || !text.EndsWith("]")) return null;

            var list = new List<RawItem>();
            foreach (Match m in Regex.Matches(text, @"\{[^{}]*\}")) {
                string body = m.Value;
                list.Add(new RawItem {
                    DeviceId = Extract(body, "deviceId"),
                    Name = Extract(body, "name"),
                    Address = Extract(body, "address"),
                    DataType = Extract(body, "dataType"),
                    Access = Extract(body, "access"),
                    Unit = Extract(body, "unit"),
                    Category = Extract(body, "category"),
                    Description = Extract(body, "description")
                });
            }
            return list;
        }

        private static string Extract (string jsonObj, string key) {
            Match m = Regex.Match(jsonObj,
                "\"" + key + "\"\\s*:\\s*\"((?:\\\\.|[^\"\\\\])*)\"",
                RegexOptions.IgnoreCase);
            if (!m.Success) return "";
            return m.Groups[1].Value
                .Replace("\\\"", "\"")
                .Replace("\\\\", "\\")
                .Replace("\\n", "\n");
        }

        private void BtnImport_Click (object sender, RoutedEventArgs e) {
            if (_accepted.Count == 0 || VariableService == null)
                return;

            if (chkClear.IsChecked == true) {
                ConfirmClearRequested?.Invoke(
                    "注意",
                    "已勾选「清空已有变量」，导入后原配置不可恢复，请确认已备份。");
                return;
            }

            ExecuteImport();
        }

        private void Root_MouseLeftButtonDown (object sender, MouseButtonEventArgs e) =>
            e.Handled = true;

        /// <summary>页面确认清空后，或无需清空时直接调用。</summary>
        public void ExecuteImport () {
            if (_accepted.Count == 0 || VariableService == null)
                return;

            try {
                if (chkClear.IsChecked == true)
                    ClearTarget();

                foreach (VariableItem v in _accepted)
                    VariableService.Add(v);

                int n = _accepted.Count;
                CloseRequested?.Invoke();
                ImportSucceeded?.Invoke(n);
            } catch (Exception ex) {
                InfoRequested?.Invoke("导入失败", ex.Message);
            }
        }

        private void ClearTarget () {
            if (VariableService == null) return;

            var ids = new List<string>();
            foreach (VariableItem v in VariableService.Variables) {
                if (v == null) continue;
                if (_scopeCurrent) {
                    if (v.DeviceId == _deviceId)
                        ids.Add(v.Id);
                } else if (_accepted.Any(a => a.DeviceId == v.DeviceId)) {
                    ids.Add(v.Id);
                }
            }

            foreach (string id in ids)
                VariableService.Remove(id);
        }

        private void BtnClose_Click (object sender, RoutedEventArgs e) =>
            CloseRequested?.Invoke();

        private sealed class RawItem {
            public string DeviceId { get; set; }
            public string Name { get; set; }
            public string Address { get; set; }
            public string DataType { get; set; }
            public string Access { get; set; }
            public string Unit { get; set; }
            public string Category { get; set; }
            public string Description { get; set; }
        }
    }
}