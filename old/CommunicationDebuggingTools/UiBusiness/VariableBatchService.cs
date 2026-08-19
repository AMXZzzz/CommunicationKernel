using System;
using System.Collections.Generic;
using CommunicationDebuggingTools.Core.Interfaces;
using CommunicationDebuggingTools.Core.Logging;
using CommunicationDebuggingTools.Core.Models;

namespace CommunicationDebuggingTools.UiBusiness {

    /// <summary>
    /// WPF UI 层业务服务：变量批量添加。
    /// </summary>
    public sealed class VariableBatchService {

        private readonly IVariableService _variables;
        private readonly IAppLogger _log;

        public VariableBatchService (IVariableService variables, IAppLogger log = null) {
            _variables = variables ?? throw new ArgumentNullException(nameof(variables));
            _log = log;
        }

        public int AddBatch (string deviceId, IList<VariableItem> items) {
            if (string.IsNullOrEmpty(deviceId) || items == null)
                return 0;

            int added = 0;
            foreach (VariableItem v in items) {
                if (v == null)
                    continue;
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
