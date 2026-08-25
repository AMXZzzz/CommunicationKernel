#nullable disable

// -----------------------------------------------------------------------------
// 文件: ViewModels/VariablePageViewModel.cs
// 层级: UI 层 — WPF 变量配置页 ViewModel
// 作用: 封装设备选中、变量 CRUD、批量添加与协议写入；Popup 仍由页面处理。
// 调用链:
//   VariableConfigPage → VariablePageViewModel
//     → IVariableService.Add/Update/Remove/WriteAsync
//       → LocalVariableStore → HostClient
// -----------------------------------------------------------------------------

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using CommunicationKernel.Host.Sdk;
using CommunicationKernel.UI.Wpf.Core.Interfaces;
using CommunicationKernel.UI.Wpf.Core.Logging;
using CommunicationKernel.UI.Wpf.Core.Models;
using CommunicationKernel.UI.Wpf.Services;

namespace CommunicationKernel.UI.Wpf.ViewModels;

/// <summary>
/// 变量配置页（VariableConfigPage）的 ViewModel。
/// 负责设备选中状态、变量 CRUD、批量添加、协议写入；
/// 不碰 Popup / Visibility——由 <c>VariableConfigPage</c> 处理。
/// </summary>
public sealed class VariablePageViewModel : ViewModelBase {

    // ============================================================================
    // 私有字段
    // ============================================================================

    private readonly IVariableService _variables;
    private readonly IDeviceService   _devices;
    private readonly IAppLogger       _log;

    private string _selectedDeviceId;

    // ============================================================================
    // 公开属性
    // ============================================================================

    /// <summary>供页面给子控件属性注入。</summary>
    public IVariableService VariableService => _variables;

    /// <summary>供页面给子控件属性注入。</summary>
    public IDeviceService DeviceService => _devices;

    /// <summary>左侧当前选中的设备 Id。</summary>
    public string SelectedDeviceId {
        get => _selectedDeviceId;
        private set => SetField(ref _selectedDeviceId, value);
    }

    // ============================================================================
    // 事件（VM → View）
    // ============================================================================

    /// <summary>请求页面刷新变量表和左侧设备列表。</summary>
    public event Action RequestRefresh;

    /// <summary>请求页面弹出主题 Info 框：(标题, 正文)。</summary>
    public event Action<string, string> RequestShowInfo;

    // ============================================================================
    // 构造函数
    // ============================================================================

    /// <param name="variables">变量管理服务（必须非 null）。</param>
    /// <param name="devices">设备管理服务（必须非 null）。</param>
    /// <param name="logger">可选日志记录器，为 null 时不记录日志。</param>
    public VariablePageViewModel(
        IVariableService variables,
        IDeviceService   devices,
        IAppLogger       logger = null) {
        // 变量与设备服务必填；日志器可空
        _variables = variables ?? throw new ArgumentNullException(nameof(variables));
        _devices   = devices   ?? throw new ArgumentNullException(nameof(devices));
        _log       = logger;
    }

    // ============================================================================
    // 公开操作
    // ============================================================================

    /// <summary>更新当前选中设备。</summary>
    public void SelectDevice(string deviceId) {
        // 记录左侧列表选中的 RouteId，后续 CRUD 都挂到这台设备
        SelectedDeviceId = deviceId;
    }

    /// <summary>
    /// 未选设备时弹出提示并返回 false；
    /// 已选设备时返回 true，调用方可继续操作。
    /// </summary>
    public bool EnsureDeviceSelected() {
        // 已选设备则允许继续新增/批量添加
        if (!string.IsNullOrEmpty(SelectedDeviceId))
            return true;

        // 提示用户先选择设备
        RaiseInfo("提示", "请先选择左侧设备");
        return false;
    }

    /// <summary>
    /// 当前设备显示标题：名称 · 型号。
    /// 未选中设备时返回空字符串。
    /// </summary>
    public string GetSelectedDeviceTitle() {
        // 未选中则标题栏留空
        if (string.IsNullOrEmpty(SelectedDeviceId))
            return "";

        // 在设备列表中查找当前选中设备
        DeviceInfo d = _devices.Devices
            .FirstOrDefault(x => x != null && x.Id == SelectedDeviceId);
        if (d == null)
            return "";

        // 名称优先，无名称则用 Id；有型号时拼接
        string name = string.IsNullOrEmpty(d.Name) ? d.Id : d.Name;
        return string.IsNullOrEmpty(d.Model) ? name : (name + " · " + d.Model);
    }

    /// <summary>当前设备下的变量条数。</summary>
    public int CountCurrentVariables() {
        // 未选中设备时计数为 0
        if (string.IsNullOrEmpty(SelectedDeviceId))
            return 0;
        // 只统计挂在当前 RouteId 下的变量
        return _variables.Variables.Count(
            v => v != null && v.DeviceId == SelectedDeviceId);
    }

    /// <summary>
    /// 保存单条变量（新增或更新）。
    /// <paramref name="item"/> 的 DeviceId 若为空则补当前选中设备。
    /// 校验失败或操作异常时通过 <see cref="RequestShowInfo"/> 通知页面。
    /// </summary>
    /// <param name="item">待保存的变量。</param>
    /// <param name="isNew">true = 新增；false = 更新。</param>
    public void SaveVariable(VariableItem item, bool isNew) {
        // 空对象无法入库
        if (item == null) return;

        // 编辑面板未填 DeviceId 时补当前选中设备
        if (string.IsNullOrEmpty(item.DeviceId))
            item.DeviceId = SelectedDeviceId;

        // 仍无设备则无法确定读写目标路由
        if (string.IsNullOrEmpty(item.DeviceId)) {
            RaiseInfo("提示", "请先选择左侧设备");
            return;
        }

        try {
            // 新增或更新本地列表，并触发轮询集合重建
            if (isNew) _variables.Add(item);
            else       _variables.Update(item);

            // 通知页面刷新变量表
            RequestRefresh?.Invoke();
        } catch (Exception ex) {
            _log?.Warn("Variable", "保存失败: " + ex.Message);
            RaiseInfo("保存失败", ex.Message);
        }
    }

    /// <summary>删除指定 Id 的变量。</summary>
    public void DeleteVariable(string id) {
        // 空 Id 无法定位
        if (string.IsNullOrEmpty(id)) return;

        try {
            // 从本地列表移除并停止对应轮询任务
            _variables.Remove(id);
            RequestRefresh?.Invoke();
        } catch (Exception ex) {
            _log?.Warn("Variable", "删除失败: " + ex.Message);
            RaiseInfo("删除失败", ex.Message);
        }
    }

    /// <summary>批量添加变量，统一挂到当前选中设备。</summary>
    public void SaveBatch(IList<VariableItem> items) {
        // 无条目或未选设备则忽略
        if (items == null || string.IsNullOrEmpty(SelectedDeviceId)) return;

        try {
            foreach (VariableItem v in items) {
                if (v == null) continue;
                // 批量添加一律归属当前左侧选中设备
                v.DeviceId = SelectedDeviceId;
                _variables.Add(v);
            }
            RequestRefresh?.Invoke();
        } catch (Exception ex) {
            _log?.Warn("Variable", "批量添加失败: " + ex.Message);
            RaiseInfo("批量添加失败", ex.Message);
        }
    }

    /// <summary>
    /// 解析写入文本并调用协议写。
    /// 解析/通讯失败通过 <see cref="RequestShowInfo"/> 通知页面。
    /// </summary>
    /// <param name="variableId">目标变量 Id。</param>
    /// <param name="writeText">用户输入的写入文本。</param>
    public async Task WriteVariableAsync(string variableId, string writeText) {
        // 空 Id 无法定位变量
        if (string.IsNullOrWhiteSpace(variableId)) return;

        // 查找目标变量
        VariableItem variable = _variables.Variables
            .FirstOrDefault(v => v != null && v.Id == variableId);

        if (variable == null) {
            RaiseInfo("写入失败", "变量不存在");
            return;
        }

        // 按 DataType 把界面字符串解析为强类型值
        if (!ValueParser.TryParse(variable.DataType, writeText, out object value, out string parseError)) {
            RaiseInfo("写入失败", parseError ?? "格式不正确");
            return;
        }

        HostOperationResult result;
        try {
            // 经 gRPC WriteAsync 下发到 PLC
            result = await _variables.WriteAsync(variableId, value, CancellationToken.None)
                .ConfigureAwait(true);
        } catch (Exception ex) {
            RaiseInfo("写入失败", ex.Message);
            return;
        }

        // 写入失败时展示错误码和描述
        if (!result.Success)
            RaiseInfo(string.Format("写入失败 ({0})", result.ErrorCode), result.ErrorMessage);

        // 无论成功与否均刷新变量表（显示最新 LastValue / LastError）
        RequestRefresh?.Invoke();
    }

    // ============================================================================
    // 私有辅助
    // ============================================================================

    /// <summary>触发 RequestShowInfo 事件，通知页面弹出 Info 对话框。</summary>
    private void RaiseInfo(string title, string message) =>
        RequestShowInfo?.Invoke(title, message ?? "");
}
