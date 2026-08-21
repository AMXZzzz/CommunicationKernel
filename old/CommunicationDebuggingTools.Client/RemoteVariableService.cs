using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using CommunicationDebuggingTools.Contracts.V1;
using CommunicationDebuggingTools.Core.Enums;
using CommunicationDebuggingTools.Core.Interfaces;
using CommunicationDebuggingTools.Core.Models;

namespace CommunicationDebuggingTools.Client {

    /// <summary>
    /// IVariableService 的 gRPC 代理实现（Remote 模式）。
    /// Variables 集合由后台 WatchVariables 流实时更新。
    /// </summary>
    public sealed class RemoteVariableService : IVariableService, IDisposable {

        private readonly Engine.EngineClient _client;
        private readonly SynchronizationContext _ui;
        private CancellationTokenSource _watchCts;

        public ObservableCollection<VariableItem> Variables { get; } =
            new ObservableCollection<VariableItem>();

        public RemoteVariableService (EngineHostChannel channel, SynchronizationContext ui = null) {
            _client = channel.Client;
            _ui     = ui ?? SynchronizationContext.Current;
        }

        public void StopWatch () {
            _watchCts?.Cancel();
        }

        public void StartWatch () {
            _watchCts?.Cancel();
            _watchCts = new CancellationTokenSource();
            Task.Run(() => WatchLoopAsync(_watchCts.Token));
        }

        private async Task WatchLoopAsync (CancellationToken ct) {
            try {
                using var call = _client.WatchVariables(new Empty(), cancellationToken: ct);
                while (await call.ResponseStream.MoveNext(ct).ConfigureAwait(false)) {
                    VariableValueEvent evt = call.ResponseStream.Current;
                    Post(() => ApplyEvent(evt));
                }
            } catch (OperationCanceledException) { }
            catch { }
        }

        private void ApplyEvent (VariableValueEvent evt) {
            VariableItem v = Variables.FirstOrDefault(x => x.Id == evt.VariableId);
            if (v == null) return;
            v.LastValue = ParseValue(v, evt.LastValue);
            v.LastError = evt.LastError ?? "";
            if (Enum.TryParse(evt.Quality, true, out DataQuality q)) v.Quality = q;
        }

        // ── IVariableService 实现 ─────────────────────

        public void Load () => Refresh();

        public void Save () { }

        public void Add (VariableItem item) {
            var dto = ToDto(item);
            dto.Id = "";
            var resp = _client.UpsertVariable(new UpsertVariableRequest { Variable = dto });
            if (resp?.Variable != null && !string.IsNullOrEmpty(resp.Variable.Id))
                item.Id = resp.Variable.Id;
        }

        public void Update (VariableItem item) =>
            _client.UpsertVariable(new UpsertVariableRequest { Variable = ToDto(item) });

        public void Remove (string id) =>
            _client.DeleteVariable(new DeleteVariableRequest { Id = id });

        public async Task<OperationResult> ReadAsync (string variableId, CancellationToken ct) {
            try {
                var resp = await _client.ReadVariableAsync(
                    new ReadVariableRequest { Id = variableId }, cancellationToken: ct)
                    .ConfigureAwait(false);
                if (resp?.Variable != null) {
                    VariableItem v = Variables.FirstOrDefault(x => x.Id == variableId);
                    if (v != null) MergeValue(v, resp.Variable);
                }
                return resp?.Result?.Ok == true
                    ? OperationResult.Ok
                    : OperationResult.ProtocolError(resp?.Result?.Message ?? "读失败");
            } catch (OperationCanceledException) { return OperationResult.Cancelled; }
            catch (Exception ex)                 { return OperationResult.Fail(ex.Message, OperationErrorCode.ProtocolError); }
        }

        public async Task<OperationResult> WriteAsync (string variableId, object value, CancellationToken ct) {
            try {
                string raw = value != null ? System.Convert.ToString(value) : "";
                var resp = await _client.WriteVariableAsync(
                    new WriteVariableRequest { Id = variableId, Value = raw }, cancellationToken: ct)
                    .ConfigureAwait(false);
                return resp?.Result?.Ok == true
                    ? OperationResult.Ok
                    : OperationResult.ProtocolError(resp?.Result?.Message ?? "写失败");
            } catch (OperationCanceledException) { return OperationResult.Cancelled; }
            catch (Exception ex)                 { return OperationResult.Fail(ex.Message, OperationErrorCode.ProtocolError); }
        }

        public async Task ReadByDeviceAsync (string deviceId, CancellationToken ct) {
            var tasks = Variables
                .Where(v => v?.DeviceId == deviceId)
                .Select(v => ReadAsync(v.Id, ct));
            await Task.WhenAll(tasks).ConfigureAwait(false);
        }

        // ── 辅助 ─────────────────────────────────────

        private void Refresh () {
            try {
                var resp = _client.ListVariables(new ListVariablesRequest());
                if (resp == null) return;
                Variables.Clear();
                foreach (VariableDto dto in resp.Variables)
                    Variables.Add(FromDto(dto));
            } catch (Exception) {
                // Host 未启动或网络不可达：保持空列表
                Variables.Clear();
            }
        }

        private void Post (Action a) {
            if (_ui != null) _ui.Post(_ => a(), null);
            else             a();
        }

        private static VariableItem FromDto (VariableDto dto) {
            var v = new VariableItem {
                Id = dto.Id ?? "", DeviceId = dto.DeviceId ?? "", Name = dto.Name ?? "",
                Address = dto.Address ?? "", Length = dto.Length,
                Unit = dto.Unit ?? "", Category = dto.Category ?? "", Description = dto.Description ?? "",
                LastError = dto.LastError ?? ""
            };
            if (Enum.TryParse(dto.DataType, true, out VariableDataType dt)) v.DataType = dt;
            if (Enum.TryParse(dto.Access,   true, out VariableAccess   ac)) v.Access   = ac;
            if (Enum.TryParse(dto.Quality,  true, out DataQuality       q))  v.Quality  = q;
            v.LastValue = ParseValue(v, dto.LastValue);
            return v;
        }

        private static void MergeValue (VariableItem v, VariableDto dto) {
            v.LastValue = ParseValue(v, dto.LastValue);
            v.LastError = dto.LastError ?? "";
            if (Enum.TryParse(dto.Quality, true, out DataQuality q)) v.Quality = q;
        }

        private static VariableDto ToDto (VariableItem v) =>
            new VariableDto {
                Id = v.Id ?? "", DeviceId = v.DeviceId ?? "", Name = v.Name ?? "",
                Address = v.Address ?? "", DataType = v.DataType.ToString(),
                Access = v.Access.ToString(), Length = v.Length,
                Unit = v.Unit ?? "", Category = v.Category ?? "", Description = v.Description ?? ""
            };

        private static object ParseValue (VariableItem variable, string rawValue) {
            if (string.IsNullOrWhiteSpace(rawValue)) {
                return null;
            }

            string text = rawValue.Trim();
            if (variable == null) {
                return text;
            }

            switch (variable.DataType) {
                case VariableDataType.Bool:
                    if (bool.TryParse(text, out bool b)) return b;
                    if (text == "1" || text.Equals("on", StringComparison.OrdinalIgnoreCase) || text.Equals("yes", StringComparison.OrdinalIgnoreCase)) return true;
                    if (text == "0" || text.Equals("off", StringComparison.OrdinalIgnoreCase) || text.Equals("no", StringComparison.OrdinalIgnoreCase)) return false;
                    return text;
                case VariableDataType.Int16:
                    if (short.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out short i16)) return i16;
                    return text;
                case VariableDataType.UInt16:
                    if (ushort.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out ushort u16)) return u16;
                    return text;
                case VariableDataType.Int32:
                    if (int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out int i32)) return i32;
                    return text;
                case VariableDataType.UInt32:
                    if (uint.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out uint u32)) return u32;
                    return text;
                case VariableDataType.Int64:
                    if (long.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out long i64)) return i64;
                    return text;
                case VariableDataType.UInt64:
                    if (ulong.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out ulong u64)) return u64;
                    return text;
                case VariableDataType.Float:
                    if (float.TryParse(text, NumberStyles.Float | NumberStyles.AllowThousands, CultureInfo.InvariantCulture, out float f)) return f;
                    return text;
                case VariableDataType.Double:
                    if (double.TryParse(text, NumberStyles.Float | NumberStyles.AllowThousands, CultureInfo.InvariantCulture, out double d)) return d;
                    return text;
                default:
                    return text;
            }
        }

        public void Dispose () {
            _watchCts?.Cancel();
            _watchCts?.Dispose();
            _watchCts = null;
        }
    }
}
