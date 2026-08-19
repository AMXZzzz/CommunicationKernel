using System;
using System.Collections.Generic;
using CommunicationDebuggingTools.Core.Interfaces;
using CommunicationDebuggingTools.Core.Logging;
using CommunicationDebuggingTools.Core.Models;

namespace CommunicationDebuggingTools.Scenarios {

    /// <summary>
    /// 场景服务：变量批量添加。
    /// 属于上层可替换业务，仅依赖 Core 接口，可被 WPF/Web/移动端复用。
    /// </summary>
    public sealed class VariableBatchService {

        private readonly IVariableService _variables;
        private readonly IAppLogger _log;

        public VariableBatchService (IVariableService variables, IAppLogger log = null) {
            _variables = variables ?? throw new ArgumentNullException(nameof(variables));
            _log = log;
        }

        /// <summary>
        /// 将一组变量统一挂到指定设备并逐条添加。
        /// 返回成功添加条数；单条失败记日志并继续。
        /// </summary>
        public int AddBatch (string deviceId, IList<VariableItem> items) {
            if (string.IsNullOrEmpty(deviceId) || items == null)
                return 0;

            int added = 0;
            foreach (VariableItem v in items) {
                if (v == null) continue;
                v.DeviceId = deviceId;
                try {
                    _variables.Add(v);
                    added++;
                } catch (Exception ex) {
                    _log?.Warn("Batch", "批量添加单条失败: " + ex.Message);
                }
            }
            return added;
        }
    }
}
