using CommunicationDebuggingTools.Core;
using CommunicationDebuggingTools.Core.Enums;
using CommunicationDebuggingTools.Core.Interfaces;
using CommunicationDebuggingTools.Core.Models;
using CommunicationDebuggingTools.Core.Tools;
using System.Text;
using System;
using System.Globalization;
using System.Threading;
using System.Threading.Tasks;

namespace Plugin.SiemensS7 {
    /// <summary>
    /// Siemens S7 协议插件（ISO-on-TCP / 端口 102），真异步 I/O。
    /// 地址格式：DB1.DBX0.0 / DB1.DBW0 / M0.0 / MB0 / IW0 / QD0 等。
    /// </summary>
    [ProtocolName("Siemens S7")]
    public sealed class SiemensS7Protocol : IProtocol {

        private readonly SiemensS7Session _session = new SiemensS7Session();
        private bool _disposed;

        const byte TS_BIT = 0x01;
        const byte TS_BYTE = 0x02;
        const byte TS_WORD = 0x04;
        const byte TS_DWORD = 0x06;
        const byte TS_REAL = 0x08;

        const byte AREA_I = 0x81;
        const byte AREA_Q = 0x82;
        const byte AREA_M = 0x83;
        const byte AREA_DB = 0x84;
        const byte AREA_V = 0x87;

        public bool IsConnected => _session.IsConnected;

        public async Task<bool> ConnectAsync (
            ProtocolConnectionContext context,
            CancellationToken cancellationToken) {
            if (context == null)
                throw new ArgumentNullException(nameof(context));

            _session.Disconnect();
            if (string.IsNullOrWhiteSpace(context.Ip))
                return false;

            _session.ApplySettingsJson(context.ExtraSettingsJson);

            try {
                int port = context.Port > 0 ? context.Port : 102;
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
            if (request == null) throw new ArgumentNullException("request");
            if (!_session.IsConnected) return Fail(request, "未连接");

            try {
                cancellationToken.ThrowIfCancellationRequested();
                S7Address addr = SiemensS7Session.ParseAddress(request.Address);
                byte ts = GetTransportSize(request.DataType, addr);
                int elemCount = GetElementCount(request.DataType, request.Length, request.StringEncoding);

                byte[] job = BuildReadJob(addr, ts, elemCount, _session.NextRef());
                byte[] resp = await _session.TransactAsync(job, cancellationToken)
                    .ConfigureAwait(false);

                byte[] raw = ParseReadResponse(resp, ts, elemCount);
                request.Value = FromS7Bytes(raw, request.DataType, request.StringEncoding);
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
            if (request == null) throw new ArgumentNullException("request");
            if (!_session.IsConnected) return Fail(request, "未连接");

            try {
                cancellationToken.ThrowIfCancellationRequested();
                S7Address addr = SiemensS7Session.ParseAddress(request.Address);
                byte ts = GetTransportSize(request.DataType, addr);
                byte[] data = ToS7Bytes(
                    request.Value, request.DataType, request.Length, request.StringEncoding);

                byte[] job = BuildWriteJob(addr, ts, data, _session.NextRef());
                byte[] resp = await _session.TransactAsync(job, cancellationToken)
                    .ConfigureAwait(false);
                ParseWriteResponse(resp);

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

        static byte[] BuildReadJob (S7Address addr, byte ts, int elemCount, ushort pduRef) {
            byte areaCode = AreaCode(addr);
            int bitAddr = addr.ByteOffset * 8 + (addr.Bit >= 0 ? addr.Bit : 0);

            return new byte[] {
                0x32, 0x01, 0x00, 0x00,
                (byte)(pduRef >> 8), (byte)(pduRef & 0xFF),
                0x00, 0x0E,
                0x00, 0x00,
                0x04,
                0x01,
                0x12, 0x0A, 0x10,
                ts,
                (byte)(elemCount >> 8), (byte)(elemCount & 0xFF),
                (byte)(addr.DbNumber >> 8), (byte)(addr.DbNumber & 0xFF),
                areaCode,
                (byte)(bitAddr >> 16), (byte)(bitAddr >> 8), (byte)(bitAddr & 0xFF)
            };
        }

        static byte[] BuildWriteJob (S7Address addr, byte ts, byte[] data, ushort pduRef) {
            byte areaCode = AreaCode(addr);
            int bitAddr = addr.ByteOffset * 8 + (addr.Bit >= 0 ? addr.Bit : 0);

            byte rts = (ts == TS_BIT) ? (byte)0x03 : (byte)0x04;
            int bitLen = (ts == TS_BIT) ? 1 : data.Length * 8;
            int elemCount = GetWriteElementCount(ts, data.Length);
            int pLen = 14;
            int dLen = 4 + data.Length;

            byte[] pdu = new byte[10 + pLen + dLen];
            int i = 0;

            pdu[i++] = 0x32; pdu[i++] = 0x01; pdu[i++] = 0x00; pdu[i++] = 0x00;
            pdu[i++] = (byte)(pduRef >> 8); pdu[i++] = (byte)(pduRef & 0xFF);
            pdu[i++] = (byte)(pLen >> 8); pdu[i++] = (byte)(pLen & 0xFF);
            pdu[i++] = (byte)(dLen >> 8); pdu[i++] = (byte)(dLen & 0xFF);
            pdu[i++] = 0x05; pdu[i++] = 0x01;
            pdu[i++] = 0x12; pdu[i++] = 0x0A; pdu[i++] = 0x10;
            pdu[i++] = ts;
            pdu[i++] = (byte)(elemCount >> 8); pdu[i++] = (byte)(elemCount & 0xFF);
            pdu[i++] = (byte)(addr.DbNumber >> 8); pdu[i++] = (byte)(addr.DbNumber & 0xFF);
            pdu[i++] = areaCode;
            pdu[i++] = (byte)(bitAddr >> 16); pdu[i++] = (byte)(bitAddr >> 8); pdu[i++] = (byte)(bitAddr & 0xFF);
            pdu[i++] = 0x00;
            pdu[i++] = rts;
            pdu[i++] = (byte)(bitLen >> 8); pdu[i++] = (byte)(bitLen & 0xFF);
            Array.Copy(data, 0, pdu, i, data.Length);
            return pdu;
        }

        static byte[] ParseReadResponse (byte[] s7, byte ts, int elemCount) {
            if (s7 == null || s7.Length < 14)
                throw new Exception("Read 响应过短");
            if (s7[1] != 0x03)
                throw new Exception("非 Ack-Data");
            if (s7[10] != 0x00 || s7[11] != 0x00)
                throw new Exception("S7 错误 errClass=0x" + s7[10].ToString("X2")
                    + " errCode=0x" + s7[11].ToString("X2"));

            int paramLen = (s7[6] << 8) | s7[7];
            int dOff = 12 + paramLen;

            if (dOff + 4 > s7.Length) throw new Exception("Read 数据段不足");

            byte rc = s7[dOff];
            if (rc != 0xFF)
                throw new Exception("读取失败，返回码 0x" + rc.ToString("X2"));

            byte rts = s7[dOff + 1];
            int bitLen = (s7[dOff + 2] << 8) | s7[dOff + 3];
            int byteLen;
            if (rts == 0x03) byteLen = 1;
            else if (ts == TS_REAL || ts == TS_DWORD) byteLen = 4 * elemCount;
            else byteLen = (bitLen + 7) / 8;

            if (dOff + 4 + byteLen > s7.Length)
                throw new Exception("Read 数据字节不足（需 " + byteLen + " 字节）");

            byte[] data = new byte[byteLen];
            Array.Copy(s7, dOff + 4, data, 0, byteLen);
            return data;
        }

        static void ParseWriteResponse (byte[] s7) {
            if (s7 == null || s7.Length < 12)
                throw new Exception("Write 响应过短");
            if (s7[1] != 0x03)
                throw new Exception("非 Ack-Data");
            if (s7[10] != 0x00 || s7[11] != 0x00)
                throw new Exception("S7 错误 errClass=0x" + s7[10].ToString("X2")
                    + " errCode=0x" + s7[11].ToString("X2"));

            int paramLen = (s7[6] << 8) | s7[7];
            int dOff = 12 + paramLen;
            if (dOff >= s7.Length) throw new Exception("Write 响应无数据");

            byte rc = s7[dOff];
            if (rc != 0xFF)
                throw new Exception("写入失败，返回码 0x" + rc.ToString("X2"));
        }

        static object FromS7Bytes (byte[] d, VariableDataType dt, StringEncodingKind encoding) {
            switch (dt) {
                case VariableDataType.Bool:
                    return d[0] != 0x00;
                case VariableDataType.Int16:
                    return (short)((d[0] << 8) | d[1]);
                case VariableDataType.UInt16:
                    return (ushort)((d[0] << 8) | d[1]);
                case VariableDataType.Int32:
                    return (d[0] << 24) | (d[1] << 16) | (d[2] << 8) | d[3];
                case VariableDataType.UInt32:
                    return (uint)((d[0] << 24) | (d[1] << 16) | (d[2] << 8) | d[3]);
                case VariableDataType.Float: {
                    byte[] le = new byte[] { d[3], d[2], d[1], d[0] };
                    return BitConverter.ToSingle(le, 0);
                }
                case VariableDataType.Double: {
                    byte[] le = new byte[] { d[7], d[6], d[5], d[4], d[3], d[2], d[1], d[0] };
                    return BitConverter.ToDouble(le, 0);
                }
                case VariableDataType.String: {
                    // 连续字节区原始串（非 S7 STRING 头）；去尾部 0x00
                    Encoding enc = ProtocolCodecTools.ResolveEncoding(encoding);
                    int end = d.Length;
                    while (end > 0 && d[end - 1] == 0) end--;
                    return end <= 0 ? "" : enc.GetString(d, 0, end);
                }
                default:
                    return d;
            }
        }

        static byte[] ToS7Bytes (
            object value, VariableDataType dt, int length, StringEncodingKind encoding) {
            switch (dt) {
                case VariableDataType.Bool:
                    return new byte[] { (byte)(ToBool(value) ? 1 : 0) };
                case VariableDataType.Int16: {
                    short s = (short)ToInt64(value);
                    return new byte[] { (byte)(s >> 8), (byte)(s & 0xFF) };
                }
                case VariableDataType.UInt16: {
                    ushort u = (ushort)ToInt64(value);
                    return new byte[] { (byte)(u >> 8), (byte)(u & 0xFF) };
                }
                case VariableDataType.Int32: {
                    int v = (int)ToInt64(value);
                    return new byte[] {
                        (byte)(v >> 24), (byte)(v >> 16), (byte)(v >> 8), (byte)(v & 0xFF) };
                }
                case VariableDataType.UInt32: {
                    uint u = (uint)ToInt64(value);
                    return new byte[] {
                        (byte)(u >> 24), (byte)(u >> 16), (byte)(u >> 8), (byte)(u & 0xFF) };
                }
                case VariableDataType.Float: {
                    byte[] le = BitConverter.GetBytes(ToFloat(value));
                    return new byte[] { le[3], le[2], le[1], le[0] };
                }
                case VariableDataType.Double: {
                    byte[] le = BitConverter.GetBytes(ToDouble(value));
                    return new byte[] { le[7], le[6], le[5], le[4], le[3], le[2], le[1], le[0] };
                }
                case VariableDataType.String: {
                    Encoding enc = ProtocolCodecTools.ResolveEncoding(encoding);
                    string s = value != null ? value.ToString() : "";
                    int maxChars = length > 0 ? length : s.Length;
                    if (maxChars < 1) maxChars = 1;
                    if (s.Length > maxChars) s = s.Substring(0, maxChars);
                    byte[] raw = enc.GetBytes(s);
                    int maxBytes = Math.Min(enc.GetMaxByteCount(maxChars), 240);
                    byte[] buf = new byte[maxBytes];
                    int copy = Math.Min(raw.Length, maxBytes);
                    Array.Copy(raw, 0, buf, 0, copy);
                    return buf;
                }
                default:
                    throw new Exception("不支持的数据类型写入: " + dt);
            }
        }

        /// <summary>读请求元素个数（字节/字/双字）。</summary>
        static int GetElementCount (VariableDataType dt, int length, StringEncodingKind encoding) {
            switch (dt) {
                case VariableDataType.Double:
                    return 2;
                case VariableDataType.String: {
                    int maxChars = length > 0 ? length : 1;
                    Encoding enc = ProtocolCodecTools.ResolveEncoding(encoding);
                    int bytes = Math.Min(enc.GetMaxByteCount(maxChars), 240);
                    return bytes < 1 ? 1 : bytes;
                }
                default:
                    return 1;
            }
        }

        static int GetWriteElementCount (byte ts, int dataLength) {
            if (ts == TS_BIT) return 1;
            if (ts == TS_BYTE) return Math.Max(1, dataLength);
            if (ts == TS_WORD) return Math.Max(1, dataLength / 2);
            if (ts == TS_DWORD || ts == TS_REAL) return Math.Max(1, dataLength / 4);
            return 1;
        }

        static byte GetTransportSize (VariableDataType dt, S7Address addr) {
            switch (dt) {
                case VariableDataType.Bool: return TS_BIT;
                case VariableDataType.Int16:
                case VariableDataType.UInt16: return TS_WORD;
                case VariableDataType.Int32:
                case VariableDataType.UInt32: return TS_DWORD;
                case VariableDataType.Float: return TS_REAL;
                case VariableDataType.Double: return TS_DWORD;
                case VariableDataType.String: return TS_BYTE;
                default:
                    switch (addr.Size) {
                        case S7TransportSize.Bit: return TS_BIT;
                        case S7TransportSize.Byte: return TS_BYTE;
                        case S7TransportSize.Word: return TS_WORD;
                        case S7TransportSize.DWord: return TS_DWORD;
                        default: return TS_WORD;
                    }
            }
        }

        static byte AreaCode (S7Address addr) {
            switch (addr.Area) {
                case 'D': return AREA_DB;
                case 'I': return AREA_I;
                case 'Q': return AREA_Q;
                case 'M': return AREA_M;
                case 'V': return AREA_V;
                default: throw new Exception("不支持的区域: " + addr.Area);
            }
        }

        static bool ToBool (object v) {
            if (v is bool b) return b;
            if (v == null) return false;
            string s = v.ToString().Trim();
            if (s == "1" || s.Equals("true", StringComparison.OrdinalIgnoreCase)) return true;
            if (s == "0" || s.Equals("false", StringComparison.OrdinalIgnoreCase)) return false;
            long n;
            if (long.TryParse(s, out n)) return n != 0;
            return false;
        }

        static long ToInt64 (object v) {
            if (v is long l) return l;
            if (v is int i) return i;
            if (v is short s) return s;
            if (v is ushort us) return us;
            if (v is uint u) return u;
            if (v is float f) return (long)f;
            if (v is double d) return (long)d;
            long r; double dr;
            if (long.TryParse(v != null ? v.ToString() : "", out r)) return r;
            if (double.TryParse(v != null ? v.ToString() : "", NumberStyles.Any,
                    CultureInfo.InvariantCulture, out dr)) return (long)dr;
            return 0;
        }

        static float ToFloat (object v) {
            if (v is float f) return f;
            if (v is double d) return (float)d;
            float r;
            if (float.TryParse(v != null ? v.ToString() : "", NumberStyles.Any,
                    CultureInfo.InvariantCulture, out r)) return r;
            return 0f;
        }

        static double ToDouble (object v) {
            if (v is double d) return d;
            if (v is float f) return f;
            double r;
            if (double.TryParse(v != null ? v.ToString() : "", NumberStyles.Any,
                    CultureInfo.InvariantCulture, out r)) return r;
            return 0.0;
        }

        static ProtocolDataMessage Fail (ProtocolDataMessage req, string msg) {
            req.Success = false;
            req.Quality = DataQuality.Bad;
            req.ErrorMessage = msg ?? "";
            return req;
        }

        public async Task<bool> PingAsync (CancellationToken cancellationToken) {
            if (!IsConnected) return false;
            try {
                cancellationToken.ThrowIfCancellationRequested();
                S7Address addr = SiemensS7Session.ParseAddress("MB0");
                byte[] job = BuildReadJob(addr, TS_BYTE, 1, _session.NextRef());
                byte[] resp = await _session.TransactAsync(job, cancellationToken)
                    .ConfigureAwait(false);
                return resp != null && resp.Length > 1 && resp[1] == 0x03;
            } catch {
                return false;
            }
        }

        public void Dispose () {
            if (_disposed) return;
            _disposed = true;
            _session.Dispose();
        }
    }
}