using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using CommunicationDebuggingTools.Core.Interfaces;
using CommunicationDebuggingTools.Core.Logging;
using CommunicationDebuggingTools.Core.Models;

namespace CommunicationDebuggingTools.UiBusiness {

    public sealed class BulkWriteResult {
        public string DeviceId { get; set; }
        public string VariableId { get; set; }
        public bool Success { get; set; }
        public string Error { get; set; }
    }

    /// <summary>
    /// WPF UI 层业务服务：按变量名一键批量写入。
    /// </summary>
    public sealed class BulkWriteService {

        private readonly IDeviceService _devices;
        private readonly IVariableService _variables;
        private readonly IAppLogger _log;

        public BulkWriteService (
            IDeviceService devices,
            IVariableService variables,
            IAppLogger log = null) {
            _devices = devices ?? throw new ArgumentNullException(nameof(devices));
            _variables = variables ?? throw new ArgumentNullException(nameof(variables));
            _log = log;
        }

        public async Task<IReadOnlyList<BulkWriteResult>> WriteToAllDevicesAsync (
            string variableName,
            object value,
            CancellationToken ct = default) {

            var results = new List<BulkWriteResult>();
            if (string.IsNullOrWhiteSpace(variableName))
                return results;

            List<VariableItem> targets = _variables.Variables
                .Where(v => v != null && string.Equals(v.Name, variableName, StringComparison.OrdinalIgnoreCase))
                .ToList();

            foreach (VariableItem v in targets) {
                ct.ThrowIfCancellationRequested();
                var item = new BulkWriteResult { DeviceId = v.DeviceId, VariableId = v.Id };
                try {
                    OperationResult r = await _variables.WriteAsync(v.Id, value, ct).ConfigureAwait(false);
                    item.Success = r.Success;
                    item.Error = r.Success ? null : r.ErrorMessage;
                } catch (OperationCanceledException) {
                    throw;
                } catch (Exception ex) {
                    item.Success = false;
                    item.Error = ex.Message;
                    _log?.Warn("BulkWrite", "写入失败 device=" + v.DeviceId + ": " + ex.Message);
                }
                results.Add(item);
            }

            _log?.Info("BulkWrite", "批量写入 " + variableName + "：" +
                results.Count(x => x.Success) + "/" + results.Count + " 成功");
            return results;
        }
    }
}
