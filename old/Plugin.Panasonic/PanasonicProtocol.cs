using System;
using System.Threading;
using System.Threading.Tasks;
using CommunicationDebuggingTools.Core;
using CommunicationDebuggingTools.Core.Enums;
using CommunicationDebuggingTools.Core.Interfaces;
using CommunicationDebuggingTools.Core.Models;
using CommunicationDebuggingTools.Core.Tools;

namespace Plugin.Panasonic {
    [ProtocolName("Panasonic MEWTOCOL")]
    public sealed class PanasonicProtocol : IProtocol {
        private readonly PanasonicSession _session = new PanasonicSession();
        private bool _disposed;

        public bool IsConnected => _session.IsConnected;

        public async Task<bool> ConnectAsync (
            ProtocolConnectionContext context,
            CancellationToken cancellationToken) {
            if (context == null)
                throw new ArgumentNullException(nameof(context));

            _session.Disconnect();
            if (string.IsNullOrWhiteSpace(context.Ip))
                return false;

            _session.SetStation(context.StationNo);

            try {
                int port = context.Port > 0 ? context.Port : 9094;
                int timeout = context.TimeoutMs > 0 ? context.TimeoutMs : AppConfig.DefaultTimeoutMs;
                await _session.ConnectAsync(context.Ip, port, timeout, cancellationToken)
                    .ConfigureAwait(false);
                return true;
            } catch {
                _session.Disconnect();
                return false;
            }
        }

        public void Disconnect () => _session.Disconnect();

        public async Task<ProtocolDataMessage> ReadAsync (
            ProtocolDataMessage request,
            CancellationToken cancellationToken) {
            if (request == null)
                throw new ArgumentNullException(nameof(request));
            if (!_session.IsConnected)
                return Fail(request, "未连接");

            try {
                cancellationToken.ThrowIfCancellationRequested();
                PanasonicAddress addr = PanasonicSession.ParseAddress(request.Address);

                if (addr.IsBit) {
                    string cmd = "RCS" + PanasonicSession.FormatContact(addr);
                    string resp = await _session.TransactAsync(cmd, cancellationToken)
                        .ConfigureAwait(false);
                    EnsureNoError(resp);
                    request.Value = ParseContactValue(resp);
                } else {
                    int wordCount = ProtocolCodecTools.WordsNeeded(
                        request.DataType, request.Length, request.StringEncoding);
                    if (wordCount < 1) wordCount = 1;

                    string body = "RD" + FormatDataRange(addr, wordCount);
                    string resp = await _session.TransactAsync(body, cancellationToken)
                        .ConfigureAwait(false);
                    EnsureNoError(resp);
                    ushort[] words = ParseDataWords(resp, wordCount);
                    request.Value = ProtocolCodecTools.FromWords(
                        words, request.DataType, request.WordOrder, request.ByteOrder,
                        request.Length, request.StringEncoding);
                }

                request.Success = true;
                request.Quality = DataQuality.Good;
                request.ErrorMessage = "";
                return request;
            } catch (OperationCanceledException) {
                return Fail(request, "已取消");
            } catch (Exception ex) {
                return Fail(request, ex.Message);
            }
        }

        public async Task<ProtocolDataMessage> WriteAsync (
            ProtocolDataMessage request,
            CancellationToken cancellationToken) {
            if (request == null)
                throw new ArgumentNullException(nameof(request));
            if (!_session.IsConnected)
                return Fail(request, "未连接");

            try {
                cancellationToken.ThrowIfCancellationRequested();
                PanasonicAddress addr = PanasonicSession.ParseAddress(request.Address);

                if (addr.IsBit) {
                    bool bit = ProtocolCodecTools.ToBool(request.Value);
                    string cmd = "WCS" + PanasonicSession.FormatContact(addr) + (bit ? "1" : "0");
                    string resp = await _session.TransactAsync(cmd, cancellationToken)
                        .ConfigureAwait(false);
                    EnsureNoError(resp);
                } else {
                    ushort[] words = ProtocolCodecTools.ToWords(
                        request.Value, request.DataType, request.Length,
                        request.WordOrder, request.ByteOrder, request.StringEncoding);

                    var sb = new System.Text.StringBuilder();
                    sb.Append("WD").Append(FormatDataRange(addr, words.Length));
                    for (int i = 0; i < words.Length; i++)
                        sb.Append(ProtocolCodecTools.SwapBytes(words[i]).ToString("X4"));

                    string wdResp = await _session.TransactAsync(sb.ToString(), cancellationToken)
                        .ConfigureAwait(false);
                    EnsureNoError(wdResp);
                }

                request.Success = true;
                request.Quality = DataQuality.Good;
                request.ErrorMessage = "";
                return request;
            } catch (OperationCanceledException) {
                return Fail(request, "已取消");
            } catch (Exception ex) {
                return Fail(request, ex.Message);
            }
        }

        public async Task<bool> PingAsync (CancellationToken cancellationToken) {
            if (!IsConnected)
                return false;
            try {
                cancellationToken.ThrowIfCancellationRequested();
                string resp = await _session.TransactAsync("RDD00000D00000", cancellationToken)
                    .ConfigureAwait(false);
                return !string.IsNullOrEmpty(resp) && resp.IndexOf('!') < 0;
            } catch {
                return false;
            }
        }

        private static string FormatDataRange (PanasonicAddress addr, int wordCount) {
            if (wordCount < 1) wordCount = 1;
            char code = addr.Area == PanasonicArea.WR ? 'W' : 'D';
            int start = addr.Index;
            int end = start + wordCount - 1;
            return code + start.ToString("D5") + code + end.ToString("D5");
        }

        private static void EnsureNoError (string resp) {
            if (string.IsNullOrEmpty(resp))
                throw new Exception("空响应");
            int bang = resp.IndexOf('!');
            if (bang >= 0) {
                string code = resp.Length >= bang + 5
                    ? resp.Substring(bang, 5)
                    : resp.Substring(bang);
                throw new Exception("MEWTOCOL 错误: " + code);
            }
        }

        private static bool ParseContactValue (string resp) {
            int i = resp.IndexOf("$RC", StringComparison.OrdinalIgnoreCase);
            if (i < 0)
                throw new Exception("触点响应无效: " + resp);
            int p = i + 3;
            while (p < resp.Length && (resp[p] == ' ' || resp[p] == '\r'))
                p++;
            if (p >= resp.Length)
                throw new Exception("触点响应无数据");
            return resp[p] == '1';
        }

        private static ushort[] ParseDataWords (string resp, int wordCount) {
            int idx = resp.IndexOf("$RD", StringComparison.OrdinalIgnoreCase);
            string data = idx >= 0 ? resp.Substring(idx + 3) : resp;
            var hex = new System.Text.StringBuilder();
            for (int i = 0; i < data.Length; i++) {
                char c = data[i];
                if ((c >= '0' && c <= '9') || (c >= 'A' && c <= 'F') || (c >= 'a' && c <= 'f'))
                    hex.Append(c);
            }
            string h = hex.ToString();
            int need = wordCount * 4;
            if (h.Length >= need + 2)
                h = h.Substring(0, need);
            if (h.Length < need)
                throw new Exception("数据字不足: " + resp);

            ushort[] words = new ushort[wordCount];
            for (int i = 0; i < wordCount; i++) {
                ushort raw = ushort.Parse(h.Substring(i * 4, 4), System.Globalization.NumberStyles.HexNumber);
                words[i] = ProtocolCodecTools.SwapBytes(raw);
            }
            return words;
        }

        private static ProtocolDataMessage Fail (ProtocolDataMessage req, string msg) {
            req.Success = false;
            req.Quality = DataQuality.Bad;
            req.ErrorMessage = msg ?? "";
            return req;
        }

        public void Dispose () {
            if (_disposed) return;
            _disposed = true;
            _session.Dispose();
        }
    }
}