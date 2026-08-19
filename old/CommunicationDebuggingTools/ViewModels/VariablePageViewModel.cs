using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using CommunicationDebuggingTools.Core.Interfaces;
using CommunicationDebuggingTools.Core.Models;
using CommunicationDebuggingTools.Core.Logging;
using CommunicationDebuggingTools.Core.Tools;

namespace CommunicationDebuggingTools.ViewModels {

    /// <summary>
    /// 变量配置页 ViewModel。
    /// 负责设备选中状态、变量 CRUD、批量添加、协议写入；
    /// 不碰 Popup / Visibility——由 <c>VariableConfigPage</c> 处理。
    /// </summary>
    public sealed class VariablePageViewModel : ViewModelBase {

        private readonly IVariableService _variables;
        private readonly IDeviceService _devices;
        private readonly IAppLogger _log;
        private readonly UiBusiness.VariableBatchService _batch;

        /// <summary>供页面给子控件属性注入。</summary>
        public IVariableService VariableService => _variables;

        /// <summary>供页面给子控件属性注入。</summary>
        public IDeviceService DeviceService => _devices;

        private string _selectedDeviceId;

        /// <summary>左侧当前选中的设备 Id。</summary>
        public string SelectedDeviceId {
            get => _selectedDeviceId;
            private set => SetField(ref _selectedDeviceId, value);
        }

        /// <summary>请求页面刷新变量表 + 左侧设备列表。</summary>
        public event Action RequestRefresh;

        /// <summary>请求页面弹出主题 Info 框：(标题, 正文)。</summary>
        public event Action<string, string> RequestShowInfo;

        public VariablePageViewModel (
            IVariableService variables,
            IDeviceService devices,
            UiBusiness.VariableBatchService batch = null,
            IAppLogger logger = null) {
            _variables = variables ?? throw new ArgumentNullException(nameof(variables));
            _devices = devices ?? throw new ArgumentNullException(nameof(devices));
            _batch = batch ?? new UiBusiness.VariableBatchService(variables, logger);
            _log = logger;
        }

        /// <summary>更新当前选中设备。</summary>
        public void SelectDevice (string deviceId) {
            SelectedDeviceId = deviceId;
        }

        /// <summary>未选设备时弹提示并返回 false。</summary>
        public bool EnsureDeviceSelected () {
            if (!string.IsNullOrEmpty(SelectedDeviceId))
                return true;
            RaiseInfo("提示", "请先选择左侧设备");
            return false;
        }

        /// <summary>当前设备显示标题：名称 · 型号。</summary>
        public string GetSelectedDeviceTitle () {
            if (string.IsNullOrEmpty(SelectedDeviceId))
                return "";

            DeviceInfo d = _devices.Devices
                .FirstOrDefault(x => x != null && x.Id == SelectedDeviceId);
            if (d == null)
                return "";

            string name = string.IsNullOrEmpty(d.Name) ? d.Id : d.Name;
            return string.IsNullOrEmpty(d.Model) ? name : (name + " · " + d.Model);
        }

        /// <summary>当前设备下变量条数。</summary>
        public int CountCurrentVariables () {
            if (string.IsNullOrEmpty(SelectedDeviceId))
                return 0;
            return _variables.Variables.Count(
                v => v != null && v.DeviceId == SelectedDeviceId);
        }

        /// <summary>
        /// 保存单条：新增或更新。
        /// <paramref name="item"/> 的 DeviceId 若为空则补当前选中设备。
        /// </summary>
        public void SaveVariable (VariableItem item, bool isNew) {
            if (item == null)
                return;

            if (string.IsNullOrEmpty(item.DeviceId))
                item.DeviceId = SelectedDeviceId;

            if (string.IsNullOrEmpty(item.DeviceId)) {
                RaiseInfo("提示", "请先选择左侧设备");
                return;
            }

            try {
                if (isNew)
                    _variables.Add(item);
                else
                    _variables.Update(item);

                RequestRefresh?.Invoke();
            } catch (Exception ex) {
                _log?.Warn("Variable", "保存失败: " + ex.Message);
                RaiseInfo("保存失败", ex.Message);
            }
        }

        /// <summary>删除指定 Id 的变量。</summary>
        public void DeleteVariable (string id) {
            if (string.IsNullOrEmpty(id))
                return;

            try {
                _variables.Remove(id);
                RequestRefresh?.Invoke();
            } catch (Exception ex) {
                _log?.Warn("Variable", "删除失败: " + ex.Message);
                RaiseInfo("删除失败", ex.Message);
            }
        }

        /// <summary>批量添加：委托场景层（Application.VariableBatchService）。</summary>
        public void SaveBatch (IList<VariableItem> items) {
            if (items == null || string.IsNullOrEmpty(SelectedDeviceId))
                return;

            try {
                _batch.AddBatch(SelectedDeviceId, items);
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
        public async Task WriteVariableAsync (string variableId, string writeText) {
            if (string.IsNullOrWhiteSpace(variableId))
                return;

            VariableItem variable = _variables.Variables
                .FirstOrDefault(v => v != null && v.Id == variableId);

            if (variable == null) {
                RaiseInfo("写入失败", "变量不存在");
                return;
            }

            if (!ValueParser.TryParse(variable.DataType, writeText, out object value, out string parseError)) {
                RaiseInfo("写入失败", parseError ?? "格式不正确");
                return;
            }

            OperationResult result;
            try {
                result = await _variables.WriteAsync(variableId, value, CancellationToken.None)
                    .ConfigureAwait(true);
            } catch (Exception ex) {
                RaiseInfo("写入失败", ex.Message);
                return;
            }

            if (!result.Success)
                // OperationResult 直接携带错误原因，无需再读 variable.LastError
                RaiseInfo($"写入失败 ({result.ErrorCode})", result.ErrorMessage);

            RequestRefresh?.Invoke();
        }

        private void RaiseInfo (string title, string message) =>
            RequestShowInfo?.Invoke(title, message ?? "");
    }
}