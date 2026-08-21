using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using CommunicationDebuggingTools.Core.Models;
using CommunicationDebuggingTools.ViewModels;
using CommunicationDebuggingTools.Views.Controls;

namespace CommunicationDebuggingTools.Views.VariableConfigPage {
    /// <summary>
    /// 变量配置页：只负责弹层 Visibility、子控件服务注入与事件路由。
    /// 业务（CRUD / 写入）全部在 <see cref="VariablePageViewModel"/>。
    /// </summary>
    public partial class VariableConfigPage : Page {
        private enum MsgPending {
            None,
            ImportClear
        }

        private readonly VariablePageViewModel _vm;
        private string _lastExportPath;
        private MsgPending _msgPending;

        public VariableConfigPage (VariablePageViewModel viewModel) {
            _vm = viewModel ?? throw new ArgumentNullException(nameof(viewModel));

            InitializeComponent();
            DataContext = _vm;

            InjectServicesIntoControls();
            WireViewModel();
            WireEvents();
            deviceList.Reload();
        }

        /// <summary>XAML 子控件属性注入（服务来自 ViewModel）。</summary>
        private void InjectServicesIntoControls () {
            if (variableTable != null)
                variableTable.VariableService = _vm.VariableService;

            if (deviceList != null) {
                deviceList.DeviceService = _vm.DeviceService;
                deviceList.VariableService = _vm.VariableService;
            }

            if (deviceHeader != null)
                deviceHeader.DeviceService = _vm.DeviceService;

            if (exportPanel != null)
                exportPanel.VariableService = _vm.VariableService;

            if (importPanel != null) {
                importPanel.VariableService = _vm.VariableService;
                importPanel.DeviceService = _vm.DeviceService;
            }
        }

        private void WireViewModel () {
            _vm.RequestRefresh += RefreshList;
            _vm.RequestShowInfo += (title, msg) => ShowInfo(title, msg);
        }

        private void WireEvents () {
            deviceList.DeviceSelected += OnDeviceSelected;
            variableTable.VariablesChanged += () => deviceList.Reload();
            variableTable.EditRequested += OpenEdit;
            variableTable.WriteRequested += OnVariableWriteRequested;

            deviceHeader.AddClicked += OpenAdd;
            deviceHeader.BatchAddClicked += OpenBatch;

            if (toolBar != null) {
                toolBar.ImportClicked += OpenImport;
                toolBar.ExportClicked += OpenExport;
            }

            if (editPanel != null) {
                editPanel.CloseRequested += CloseEdit;
                editPanel.SaveRequested += SaveEdit;
                editPanel.DeleteRequested += DeleteEdit;
                editPanel.InfoRequested += OnPanelInfo;
            }

            if (batchPanel != null) {
                batchPanel.CloseRequested += CloseBatch;
                batchPanel.BatchSaveRequested += SaveBatch;
                batchPanel.InfoRequested += OnPanelInfo;
            }

            if (exportPanel != null) {
                exportPanel.CloseRequested -= CloseExport;
                exportPanel.ExportSucceeded -= OnExportSucceeded;
                exportPanel.InfoRequested -= OnPanelInfo;
                exportPanel.CloseRequested += CloseExport;
                exportPanel.ExportSucceeded += OnExportSucceeded;
                exportPanel.InfoRequested += OnPanelInfo;
            }

            if (importPanel != null) {
                importPanel.CloseRequested -= CloseImport;
                importPanel.ConfirmClearRequested -= OnImportConfirmClear;
                importPanel.ImportSucceeded -= OnImportSucceeded;
                importPanel.InfoRequested -= OnPanelInfo;
                importPanel.CloseRequested += CloseImport;
                importPanel.ConfirmClearRequested += OnImportConfirmClear;
                importPanel.ImportSucceeded += OnImportSucceeded;
                importPanel.InfoRequested += OnPanelInfo;
            }

            if (msgDialog != null) {
                msgDialog.CloseRequested -= OnMsgClose;
                msgDialog.PrimaryRequested -= OnMsgPrimary;
                msgDialog.SecondaryRequested -= OnMessageSecondary;
                msgDialog.CloseRequested += OnMsgClose;
                msgDialog.PrimaryRequested += OnMsgPrimary;
                msgDialog.SecondaryRequested += OnMessageSecondary;
            }
        }

        // -------------------- 设备选择 --------------------

        private void OnDeviceSelected (string deviceId) {
            _vm.SelectDevice(deviceId);
            deviceHeader.Show(deviceId);
            variableTable.Load(deviceId);
        }

        // -------------------- 单条编辑 --------------------

        private void OpenAdd () {
            if (!_vm.EnsureDeviceSelected() || editPanel == null)
                return;
            editPanel.PrepareNew();
            ShowPanel(editPanel);
        }

        public void OpenEdit (VariableItem item) {
            if (item == null || editPanel == null)
                return;
            _vm.SelectDevice(item.DeviceId);
            editPanel.Load(item);
            ShowPanel(editPanel);
        }

        private void CloseEdit () => HidePanel(editPanel);

        private void SaveEdit () {
            if (editPanel == null)
                return;

            VariableItem built = editPanel.Build();
            if (built == null)
                return;

            _vm.SaveVariable(built, editPanel.IsNew);
            CloseEdit();
        }

        private void DeleteEdit () {
            if (editPanel == null || editPanel.IsNew || string.IsNullOrEmpty(editPanel.EditingId))
                return;

            _vm.DeleteVariable(editPanel.EditingId);
            CloseEdit();
        }

        // -------------------- 批量 --------------------

        private void OpenBatch () {
            if (!_vm.EnsureDeviceSelected() || batchPanel == null)
                return;
            batchPanel.Prepare(_vm.GetSelectedDeviceTitle());
            ShowPanel(batchPanel);
        }

        private void CloseBatch () => HidePanel(batchPanel);

        private void SaveBatch (IList<VariableItem> items) {
            _vm.SaveBatch(items);
            CloseBatch();
        }

        // -------------------- 导入 / 导出 --------------------

        private void OpenExport () {
            if (exportPanel == null)
                return;
            exportPanel.Prepare(
                _vm.SelectedDeviceId,
                _vm.GetSelectedDeviceTitle(),
                _vm.CountCurrentVariables());
            ShowPanel(exportPanel);
        }

        private void CloseExport () => HidePanel(exportPanel);

        private void OnExportSucceeded (string path, int count) {
            CloseExport();
            ShowExportSuccess(path, count);
        }

        private void ShowExportSuccess (string path, int count) {
            if (msgDialog == null)
                return;
            _lastExportPath = path;
            _msgPending = MsgPending.None;
            msgDialog.Setup(
                AppMessageKind.Success,
                "导出完成",
                "已导出 " + count + " 条变量",
                path,
                primaryText: "确定",
                secondaryText: "打开目录",
                showSecondary: true,
                detailAsBox: true);
            ShowPanel(msgDialog);
        }

        private void OpenImport () {
            if (importPanel == null)
                return;
            importPanel.Prepare(_vm.SelectedDeviceId, _vm.GetSelectedDeviceTitle());
            ShowPanel(importPanel);
        }

        private void CloseImport () => HidePanel(importPanel);

        private void OnImportConfirmClear (string title, string detail) {
            if (msgDialog == null)
                return;
            _msgPending = MsgPending.ImportClear;
            msgDialog.Setup(
                AppMessageKind.Warning,
                title,
                "导入将覆盖当前范围内变量",
                detail,
                primaryText: "继续导入",
                secondaryText: "取消",
                showSecondary: true);
            ShowPanel(msgDialog);
        }

        private void OnImportSucceeded (int count) {
            RefreshList();
            if (msgDialog == null)
                return;
            _msgPending = MsgPending.None;
            msgDialog.Setup(
                AppMessageKind.Success,
                "导入完成",
                "已导入 " + count + " 条变量",
                detail: null,
                primaryText: "确定",
                showSecondary: false);
            ShowPanel(msgDialog);
        }

        private void OnMsgPrimary () {
            MsgPending pending = _msgPending;
            _msgPending = MsgPending.None;
            HidePanel(msgDialog);
            if (pending == MsgPending.ImportClear)
                importPanel?.ExecuteImport();
        }

        private void OnMsgClose () {
            _msgPending = MsgPending.None;
            HidePanel(msgDialog);
        }

        private void OnMessageSecondary () {
            if (string.IsNullOrEmpty(_lastExportPath))
                return;

            string dir = Path.GetDirectoryName(_lastExportPath);
            if (string.IsNullOrEmpty(dir) || !Directory.Exists(dir)) {
                ShowInfo("提示", "目录不存在或已被删除");
                return;
            }

            try {
                Process.Start(new ProcessStartInfo {
                    FileName = dir,
                    UseShellExecute = true
                });
            } catch (InvalidOperationException ex) {
                ShowInfo("打开目录失败", ex.Message);
            } catch (System.ComponentModel.Win32Exception ex) {
                ShowInfo("打开目录失败", ex.Message);
            }
        }

        // -------------------- 写入 --------------------

        private async void OnVariableWriteRequested (string variableId, string writeText) {
            await _vm.WriteVariableAsync(variableId, writeText);
        }

        // -------------------- 消息 --------------------

        private void OnPanelInfo (string title, string message) =>
            ShowInfo(title, message);

        private void ShowInfo (string title, string message) {
            if (msgDialog == null)
                return;
            _msgPending = MsgPending.None;
            msgDialog.Setup(
                AppMessageKind.Info,
                title ?? "提示",
                message ?? "",
                detail: null,
                primaryText: "确定",
                showSecondary: false);
            ShowPanel(msgDialog);
        }

        private void EditOverlay_MouseLeftButtonDown (object sender, MouseButtonEventArgs e) {
            CloseAllPanels();
        }

        // -------------------- 弹层 Visibility --------------------

        private void ShowPanel (UIElement panel) {
            HideAllPanels();
            if (panel != null)
                panel.Visibility = Visibility.Visible;
            if (editOverlay != null)
                editOverlay.Visibility = Visibility.Visible;
        }

        private void HidePanel (UIElement panel) {
            if (panel != null)
                panel.Visibility = Visibility.Collapsed;
            HideOverlayIfIdle();
        }

        private void HideAllPanels () {
            SetCollapsed(editPanel);
            SetCollapsed(batchPanel);
            SetCollapsed(exportPanel);
            SetCollapsed(importPanel);
            SetCollapsed(msgDialog);
        }

        private void CloseAllPanels () {
            _msgPending = MsgPending.None;
            HideAllPanels();
            if (editOverlay != null)
                editOverlay.Visibility = Visibility.Collapsed;
        }

        private void HideOverlayIfIdle () {
            bool busy =
                IsShown(editPanel) || IsShown(batchPanel) ||
                IsShown(exportPanel) || IsShown(importPanel) ||
                IsShown(msgDialog);

            if (!busy && editOverlay != null)
                editOverlay.Visibility = Visibility.Collapsed;
        }

        private static void SetCollapsed (UIElement e) {
            if (e != null)
                e.Visibility = Visibility.Collapsed;
        }

        private static bool IsShown (UIElement e) =>
            e != null && e.Visibility == Visibility.Visible;

        private void RefreshList () {
            variableTable.Load(_vm.SelectedDeviceId);
            deviceList.Reload();
        }
    }
}