// -----------------------------------------------------------------------------
// 文件: Views/Pages/Variable/Controls/VariableImportPanel.xaml.cs
// 层级: UI 层 — 变量配置页 导入子面板 code-behind
// 作用: 提供 JSON 文件导入变量的完整逻辑：文件选取、解析预览、范围切换、执行导入。
//       服务由父页面注入（VariableService / DeviceService），
//       导入结果、清空确认、错误提示均通过事件回调委托给父页面展示。
// 调用链:
//   VariableConfigPage → importPanel.VariableService / DeviceService 注入
//   importPanel.Prepare(deviceId, deviceTitle) → 复位状态
//   用户点击 DropZone → PickFile → RunPreview → 显示预览
//   用户点击 BtnImport_Click → 有清空标志 → ConfirmClearRequested（页面弹 Warning）
//                           → 无清空标志 → ExecuteImport()
//   页面确认后调用 ExecuteImport() → VariableService.Add → ImportSucceeded 事件
// -----------------------------------------------------------------------------

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using CommunicationKernel.UI.Wpf.Core.Enums;
using CommunicationKernel.UI.Wpf.Core.Interfaces;
using CommunicationKernel.UI.Wpf.Core.Models;
using Microsoft.Win32;

namespace CommunicationKernel.UI.Wpf.Views.Pages.Variable.Controls {

    /// <summary>
    /// 导入变量弹层（JSON 格式）。
    /// 清空确认 / 成功提示由页面通过事件用对话框展示。
    /// 服务由页面注入 <see cref="VariableService"/> / <see cref="DeviceService"/>。
    /// </summary>
    public partial class VariableImportPanel : UserControl {

        // -------------------------------------------------------------------------
        // 注入属性（由父页面赋值）
        // -------------------------------------------------------------------------

        /// <summary>变量管理服务，由 VariableConfigPage 在初始化后注入。</summary>
        public IVariableService VariableService { get; set; }

        /// <summary>设备管理服务，由 VariableConfigPage 在初始化后注入，供全局范围导入时验证 DeviceId。</summary>
        public IDeviceService DeviceService { get; set; }

        // -------------------------------------------------------------------------
        // 事件回调（委托给父页面处理 UI 层弹窗）
        // -------------------------------------------------------------------------

        /// <summary>请求关闭此面板（导入完成或点击关闭按钮时触发）。</summary>
        public event Action CloseRequested;

        /// <summary>
        /// 用户勾选了「清空已有变量」并点击导入时触发。
        /// 参数：(标题, 正文)；父页面弹出 Warning 对话框，用户确认后调用 <see cref="ExecuteImport"/>。
        /// </summary>
        public event Action<string, string> ConfirmClearRequested;

        /// <summary>导入成功时触发，参数为成功导入的条数。</summary>
        public event Action<int> ImportSucceeded;

        /// <summary>
        /// 需要弹出 Info / 错误消息时触发。
        /// 参数：(标题, 正文)。
        /// </summary>
        public event Action<string, string> InfoRequested;

        // -------------------------------------------------------------------------
        // 私有状态
        // -------------------------------------------------------------------------

        /// <summary>当前选中的目标设备 ID（当前设备模式使用）。</summary>
        private string _deviceId;

        /// <summary>true = 仅导入到当前设备；false = 按 JSON 中 deviceId 字段分配。</summary>
        private bool _scopeCurrent = true;

        /// <summary>已选文件的完整路径。</summary>
        private string _filePath;

        /// <summary>预览时解析出的可接受变量列表（无重复、字段完整）。</summary>
        private readonly List<VariableItem> _accepted = new List<VariableItem>();

        /// <summary>预览时跳过（字段缺失或地址重复）的条数。</summary>
        private int _skipCount;

        // -------------------------------------------------------------------------
        // 构造函数
        // -------------------------------------------------------------------------

        public VariableImportPanel() {
            InitializeComponent();
            // 初始化范围按钮样式（默认「当前设备」模式高亮）
            UpdateScopeButtons();
        }

        // -------------------------------------------------------------------------
        // 公开接口
        // -------------------------------------------------------------------------

        /// <summary>
        /// 复位面板状态，由父页面在弹出前调用。
        /// </summary>
        /// <param name="deviceId">当前选中设备的路由 ID，可为空（空则禁用「当前设备」模式）。</param>
        /// <param name="deviceTitle">当前设备显示名称，用于范围提示文字。</param>
        public void Prepare(string deviceId, string deviceTitle) {
            // 复位所有内部状态
            _deviceId = deviceId;
            _scopeCurrent = true;
            _filePath = null;
            _accepted.Clear();
            _skipCount = 0;

            // 复位 UI 控件
            UpdateScopeButtons();
            txtScopeHint.Text = string.IsNullOrEmpty(deviceTitle)
                ? ""
                : ("当前设备：" + deviceTitle);
            txtFileName.Text = "未选择文件";
            txtPreview.Text  = "请先选择 JSON 文件";
            txtPreview.Foreground =
                TryFindResource("SF.Brush.Text.Secondary") as System.Windows.Media.Brush;
            btnImport.IsEnabled = false;
            chkClear.IsChecked  = false;
        }

        /// <summary>
        /// 父页面确认清空后（或无需清空时）直接调用，执行实际的变量写入操作。
        /// </summary>
        public void ExecuteImport() {
            // 无数据或服务未注入时静默返回
            if (_accepted.Count == 0 || VariableService == null)
                return;

            try {
                // 若勾选了清空，先删除作用范围内的已有变量
                if (chkClear.IsChecked == true)
                    ClearTarget();

                // 批量添加解析结果
                foreach (VariableItem v in _accepted)
                    VariableService.Add(v);

                int n = _accepted.Count;

                // 关闭面板，通知父页面成功条数
                CloseRequested?.Invoke();
                ImportSucceeded?.Invoke(n);
            } catch (Exception ex) {
                // 写入异常通过 InfoRequested 委托给父页面弹窗
                InfoRequested?.Invoke("导入失败", ex.Message);
            }
        }

        // -------------------------------------------------------------------------
        // 事件处理
        // -------------------------------------------------------------------------

        /// <summary>
        /// 范围切换按钮点击（Tag="Current" 或 Tag="All"）。
        /// 切换后若已选文件则重新执行预览。
        /// </summary>
        private void BtnScope_Click(object sender, RoutedEventArgs e) {
            // 从 Tag 读取范围标记
            string tag = (sender as FrameworkElement)?.Tag as string;
            _scopeCurrent = (tag != "All");
            UpdateScopeButtons();

            // 已选文件时重新预览（范围变化会影响可导入条数）
            if (!string.IsNullOrEmpty(_filePath))
                RunPreview();
        }

        /// <summary>点击文件拖放区域 → 弹出文件选择对话框。</summary>
        private void DropZone_Click(object sender, MouseButtonEventArgs e) => PickFile();

        /// <summary>
        /// 导入按钮点击。
        /// 若勾选了清空，先触发 ConfirmClearRequested 由父页面确认；
        /// 否则直接调用 ExecuteImport。
        /// </summary>
        private void BtnImport_Click(object sender, RoutedEventArgs e) {
            if (_accepted.Count == 0 || VariableService == null)
                return;

            if (chkClear.IsChecked == true) {
                // 委托父页面弹出确认对话框；确认后由父页面调用 ExecuteImport()
                ConfirmClearRequested?.Invoke(
                    "注意",
                    "已勾选「清空已有变量」，导入后原配置不可恢复，请确认已备份。");
                return;
            }

            ExecuteImport();
        }

        /// <summary>关闭按钮：触发 CloseRequested。</summary>
        private void BtnClose_Click(object sender, RoutedEventArgs e) =>
            CloseRequested?.Invoke();

        /// <summary>
        /// 拦截面板根容器的鼠标点击事件，防止事件冒泡穿透到背景遮罩层。
        /// </summary>
        private void Root_MouseLeftButtonDown(object sender, MouseButtonEventArgs e) =>
            e.Handled = true;

        // -------------------------------------------------------------------------
        // 私有实现
        // -------------------------------------------------------------------------

        /// <summary>打开 OpenFileDialog 让用户选择 JSON 文件，成功后触发预览。</summary>
        private void PickFile() {
            var dlg = new OpenFileDialog {
                Filter = "JSON (*.json)|*.json",
                CheckFileExists = true
            };

            // 以主窗口为 owner，防止对话框出现在主窗口后面
            Window owner = Window.GetWindow(this);
            bool? ok = owner != null ? dlg.ShowDialog(owner) : dlg.ShowDialog();
            if (ok != true)
                return;

            _filePath = dlg.FileName;
            txtFileName.Text = Path.GetFileName(_filePath);

            // 立即触发预览解析
            RunPreview();
        }

        /// <summary>
        /// 解析 JSON 文件，过滤重复/无效条目，更新预览文字和导入按钮状态。
        /// </summary>
        private void RunPreview() {
            _accepted.Clear();
            _skipCount      = 0;
            btnImport.IsEnabled = false;

            // 未选文件
            if (string.IsNullOrEmpty(_filePath) || !File.Exists(_filePath)) {
                txtPreview.Text = "请先选择 JSON 文件";
                return;
            }

            // 当前设备模式要求左侧已选设备
            if (_scopeCurrent && string.IsNullOrEmpty(_deviceId)) {
                txtPreview.Text = "当前设备模式需要先在左侧选择设备";
                return;
            }

            // 读文件
            string text;
            try {
                text = File.ReadAllText(_filePath);
            } catch (Exception ex) {
                txtPreview.Text = "读取失败：" + ex.Message;
                return;
            }

            // 解析 JSON 数组
            List<RawItem> raws = ParseArray(text);
            if (raws == null) {
                txtPreview.Text = "JSON 格式无效，需要数组 [...]";
                return;
            }

            // 构建已有变量键集合（用于去重）
            HashSet<string> existingKeys = BuildExistingKeys();
            HashSet<string> batchKeys   = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            foreach (RawItem r in raws) {
                // 字段映射校验
                if (!TryMap(r, out VariableItem item, out _)) {
                    _skipCount++;
                    continue;
                }

                // 地址去重（已有 + 本批次内部）
                string key = item.DeviceId + "|" + item.Address;
                if (existingKeys.Contains(key) || batchKeys.Contains(key)) {
                    _skipCount++;
                    continue;
                }

                batchKeys.Add(key);
                _accepted.Add(item);
            }

            // 更新预览文字
            txtPreview.Text = string.Format(
                "✓ 可导入 {0} 条\n⚠ 跳过 {1} 条（缺字段 / 地址重复 / 设备无效）",
                _accepted.Count, _skipCount);

            btnImport.IsEnabled = _accepted.Count > 0;
        }

        /// <summary>
        /// 构建当前作用范围内已有变量的 "DeviceId|Address" 键集合，用于导入去重。
        /// </summary>
        private HashSet<string> BuildExistingKeys() {
            var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            if (VariableService == null)
                return set;

            foreach (VariableItem v in VariableService.Variables) {
                if (v == null || string.IsNullOrEmpty(v.Address))
                    continue;
                // 当前设备模式只检查同设备的变量
                if (_scopeCurrent && v.DeviceId != _deviceId)
                    continue;
                set.Add(v.DeviceId + "|" + v.Address);
            }
            return set;
        }

        /// <summary>
        /// 将 <see cref="RawItem"/> 映射到 <see cref="VariableItem"/>，校验必填字段。
        /// </summary>
        /// <returns>true = 映射成功；false = 字段缺失或 DeviceId 无效。</returns>
        private bool TryMap(RawItem r, out VariableItem item, out string fail) {
            item = null;
            fail = null;

            string name    = (r.Name    ?? "").Trim();
            string address = (r.Address ?? "").Trim();
            if (string.IsNullOrEmpty(name) || string.IsNullOrEmpty(address)) {
                fail = "name/address";
                return false;
            }

            string deviceId;
            if (_scopeCurrent) {
                // 当前设备模式：强制使用页面传入的 DeviceId
                deviceId = _deviceId;
            } else {
                // 全局模式：从 JSON 读 deviceId 并验证存在
                deviceId = (r.DeviceId ?? "").Trim();
                if (string.IsNullOrEmpty(deviceId) ||
                    DeviceService == null ||
                    DeviceService.Devices.All(d => d == null || d.Id != deviceId)) {
                    fail = "deviceId";
                    return false;
                }
            }

            item = new VariableItem {
                DeviceId    = deviceId,
                Name        = name,
                Address     = address,
                DataType    = ParseDataType(r.DataType),
                Access      = ParseAccess(r.Access),
                Unit        = (r.Unit        ?? "").Trim(),
                Category    = string.IsNullOrWhiteSpace(r.Category) ? "状态点" : r.Category.Trim(),
                Description = (r.Description ?? "").Trim()
            };
            return true;
        }

        /// <summary>清除当前导入范围内的已有变量。</summary>
        private void ClearTarget() {
            if (VariableService == null)
                return;

            var ids = new List<string>();
            foreach (VariableItem v in VariableService.Variables) {
                if (v == null) continue;
                if (_scopeCurrent) {
                    // 当前设备模式：仅清除同设备变量
                    if (v.DeviceId == _deviceId)
                        ids.Add(v.Id);
                } else {
                    // 全局模式：清除与本批次涉及的设备相同的所有变量
                    if (_accepted.Any(a => a.DeviceId == v.DeviceId))
                        ids.Add(v.Id);
                }
            }

            foreach (string id in ids)
                VariableService.Remove(id);
        }

        /// <summary>更新范围切换按钮的高亮样式（当前/全局）。</summary>
        private void UpdateScopeButtons() {
            Style dark    = TryFindResource("SF.Style.DarkButton")    as Style;
            Style primary = TryFindResource("SF.Style.PrimaryButton") as Style;
            // 当前模式高亮当前按钮，全局模式高亮全局按钮
            btnScopeCurrent.Style = _scopeCurrent  ? primary : dark;
            btnScopeAll.Style     = !_scopeCurrent ? primary : dark;
        }

        // -------------------------------------------------------------------------
        // 静态解析辅助
        // -------------------------------------------------------------------------

        private static VariableDataType ParseDataType(string s) {
            if (string.IsNullOrWhiteSpace(s))
                return VariableDataType.Int16;
            return Enum.TryParse(s.Trim(), true, out VariableDataType t)
                ? t : VariableDataType.Int16;
        }

        private static VariableAccess ParseAccess(string s) {
            if (string.IsNullOrWhiteSpace(s))
                return VariableAccess.ReadWrite;
            s = s.Trim().ToUpperInvariant();
            if (s == "R" || s == "READONLY")  return VariableAccess.ReadOnly;
            if (s == "W" || s == "WRITEONLY") return VariableAccess.WriteOnly;
            return VariableAccess.ReadWrite;
        }

        /// <summary>
        /// 用正则从 JSON 字符串中提取数组，返回粗解析的 <see cref="RawItem"/> 列表。
        /// 仅支持扁平对象数组（不含嵌套），满足变量 JSON 导入的格式要求。
        /// </summary>
        private static List<RawItem> ParseArray(string text) {
            if (string.IsNullOrWhiteSpace(text)) return null;
            text = text.Trim();
            if (!text.StartsWith("[") || !text.EndsWith("]")) return null;

            var list = new List<RawItem>();
            foreach (Match m in Regex.Matches(text, @"\{[^{}]*\}")) {
                string body = m.Value;
                list.Add(new RawItem {
                    DeviceId    = Extract(body, "deviceId"),
                    Name        = Extract(body, "name"),
                    Address     = Extract(body, "address"),
                    DataType    = Extract(body, "dataType"),
                    Access      = Extract(body, "access"),
                    Unit        = Extract(body, "unit"),
                    Category    = Extract(body, "category"),
                    Description = Extract(body, "description")
                });
            }
            return list;
        }

        /// <summary>从 JSON 对象字符串中提取指定键的字符串值（不依赖第三方库）。</summary>
        private static string Extract(string jsonObj, string key) {
            Match m = Regex.Match(
                jsonObj,
                "\"" + key + "\"\\s*:\\s*\"((?:\\\\.|[^\"\\\\])*)\"",
                RegexOptions.IgnoreCase);
            if (!m.Success) return "";
            return m.Groups[1].Value
                .Replace("\\\"", "\"")
                .Replace("\\\\", "\\")
                .Replace("\\n",  "\n");
        }

        // -------------------------------------------------------------------------
        // 内部 DTO
        // -------------------------------------------------------------------------

        /// <summary>JSON 粗解析中间体，字段全部为可空字符串。</summary>
        private sealed class RawItem {
            public string DeviceId    { get; set; }
            public string Name        { get; set; }
            public string Address     { get; set; }
            public string DataType    { get; set; }
            public string Access      { get; set; }
            public string Unit        { get; set; }
            public string Category    { get; set; }
            public string Description { get; set; }
        }
    }
}
