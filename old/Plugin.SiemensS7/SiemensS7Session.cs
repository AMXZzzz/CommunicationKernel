using CommunicationDebuggingTools.Core.Tools;
using System;
using CommunicationDebuggingTools.Core;
using System.Globalization;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;

namespace Plugin.SiemensS7 {

    internal sealed class SiemensS7Session : IDisposable {
        private const int DEFAULT_PORT = 102;

        private TcpClient _tcp;
        private NetworkStream _stream;
        private int _timeoutMs = AppConfig.DefaultTimeoutMs;
        private bool _disposed;
        private int _pduRefField;
        private readonly SemaphoreSlim _io = new SemaphoreSlim(1, 1);

        public int Rack { get; private set; }
        public int Slot { get; private set; } = 1;

        public bool IsConnected {
            get {
                if (_tcp == null || _stream == null) return false;
                System.Net.Sockets.Socket s = _tcp.Client;
                if (s == null || !s.Connected) return false;
                try {
                    return !(s.Poll(0, System.Net.Sockets.SelectMode.SelectRead)
                             && s.Available == 0);
                } catch {
                    return false;
                }
            }
        }

        public int TimeoutMs {
            get => _timeoutMs;
            set => _timeoutMs = value < 500 ? 500 : value;
        }

        public void ApplySettingsJson (string json) {
            Rack = ExtraSettingsJsonHelper.GetInt(json, "rack", 0);
            Slot = ExtraSettingsJsonHelper.GetInt(json, "slot", 1);
            if (Rack < 0) Rack = 0;
            if (Slot < 0) Slot = 0;
        }

        // ════════════════════════════════════════════════
        //  连接：TCP → COTP → S7 Setup
        // ════════════════════════════════════════════════
        public async Task ConnectAsync (string ip, int port, int timeoutMs, CancellationToken ct) {
            Disconnect();
            if (string.IsNullOrWhiteSpace(ip))
                throw new ArgumentException("IP 为空");

            port = port > 0 ? port : DEFAULT_PORT;
            TimeoutMs = timeoutMs > 0 ? timeoutMs : AppConfig.DefaultTimeoutMs;
            _tcp = new TcpClient();

            var connectTask = _tcp.ConnectAsync(ip, port);
            var timeoutTask = Task.Delay(TimeoutMs, ct);
            if (await Task.WhenAny(connectTask, timeoutTask) != connectTask) {
                Disconnect();
                throw new TimeoutException("连接超时");
            }
            await connectTask;

            if (!_tcp.Connected || ct.IsCancellationRequested) {
                Disconnect();
                throw new InvalidOperationException("连接失败或已取消");
            }

            _stream = _tcp.GetStream();
            _pduRefField = 0;

            // ① COTP Connection Request → Confirm
            await SendTpktAsync(BuildCotpCR(Rack, Slot), ct).ConfigureAwait(false);
            byte[] cc = await ReadTpktAsync(ct).ConfigureAwait(false);
            if (cc == null || cc.Length < 2 || cc[1] != 0xD0)
                throw new Exception("COTP 握手失败：未收到 CC（0xD0）");

            // ② S7 Setup Communication
            await SendTpktAsync(WrapDt(BuildS7SetupJob(NextRef())), ct).ConfigureAwait(false);
            byte[] setupResp = await ReadTpktAsync(ct).ConfigureAwait(false);
            byte[] s7setup = ExtractS7(setupResp);
            if (s7setup == null || s7setup.Length < 13 || s7setup[1] != 0x03 || s7setup[12] != 0xF0)
                throw new Exception("S7 Setup Communication 失败");
        }

        public void Disconnect () {
            try { if (_stream != null) { _stream.Close(); _stream = null; } } catch { }
            try { if (_tcp != null) { _tcp.Close(); _tcp = null; } } catch { }
        }

        // ════════════════════════════════════════════════
        //  发送 S7 Job，返回 Ack-Data 的 S7 PDU
        // ════════════════════════════════════════════════
        public async Task<byte[]> TransactAsync (byte[] s7Job, CancellationToken ct) {
            if (!IsConnected) throw new InvalidOperationException("未连接");
            await _io.WaitAsync(ct).ConfigureAwait(false);
            try {
                await SendTpktAsync(WrapDt(s7Job), ct).ConfigureAwait(false);
                byte[] resp = await ReadTpktAsync(ct).ConfigureAwait(false);
                byte[] s7 = ExtractS7(resp);
                if (s7 == null)
                    throw new Exception("无效响应");
                return s7;
            } finally {
                _io.Release();
            }
        }

        public ushort NextRef () {
            return (ushort)Interlocked.Increment(ref _pduRefField);
        }

        // ════════════════════════════════════════════════
        //  PDU 构造辅助
        // ════════════════════════════════════════════════

        internal static byte[] BuildCotpCR (int rack, int slot) {
            byte calledLo = (byte)((rack << 5) | (slot & 0x1F));
            return new byte[] {
                0x11,                       // COTP 长度 = 17
                0xE0,                       // CR
                0x00, 0x00,                 // dst-ref
                0x00, 0x01,                 // src-ref
                0x00,                       // class 0
                0xC0, 0x01, 0x0A,           // TPDU size = 1024
                0xC1, 0x02, 0x01, 0x00,     // calling TSAP
                0xC2, 0x02, 0x01, calledLo  // called TSAP
            };
        }

        internal static byte[] BuildS7SetupJob (ushort pduRef) {
            return new byte[] {
                0x32, 0x01, 0x00, 0x00,
                (byte)(pduRef >> 8), (byte)(pduRef & 0xFF),
                0x00, 0x08,
                0x00, 0x00,
                0xF0, 0x00,
                0x00, 0x01,
                0x00, 0x01,
                0x01, 0xE0
            };
        }

        internal static byte[] WrapDt (byte[] s7) {
            byte[] p = new byte[3 + s7.Length];
            p[0] = 0x02; p[1] = 0xF0; p[2] = 0x80;
            Array.Copy(s7, 0, p, 3, s7.Length);
            return p;
        }

        internal static byte[] ExtractS7 (byte[] payload) {
            if (payload == null || payload.Length < 4 || payload[1] != 0xF0)
                return null;
            byte[] s7 = new byte[payload.Length - 3];
            Array.Copy(payload, 3, s7, 0, s7.Length);
            return s7;
        }

        // ════════════════════════════════════════════════
        //  TPKT 传输（真异步）
        // ════════════════════════════════════════════════
        async Task SendTpktAsync (byte[] payload, CancellationToken ct) {
            int total = payload.Length + 4;
            byte[] pkt = new byte[total];
            pkt[0] = 0x03;
            pkt[1] = 0x00;
            pkt[2] = (byte)(total >> 8);
            pkt[3] = (byte)(total & 0xFF);
            Array.Copy(payload, 0, pkt, 4, payload.Length);
            try {
                using (var linked = CancellationTokenSource.CreateLinkedTokenSource(ct)) {
                    linked.CancelAfter(TimeoutMs);
                    await _stream.WriteAsync(pkt, 0, pkt.Length, linked.Token).ConfigureAwait(false);
                    await _stream.FlushAsync(linked.Token).ConfigureAwait(false);
                }
            } catch (OperationCanceledException) {
                Disconnect();
                throw;
            } catch {
                Disconnect();
                throw;
            }
        }

        async Task<byte[]> ReadTpktAsync (CancellationToken ct) {
            byte[] hdr = await ReadExactAsync(4, ct).ConfigureAwait(false);
            if (hdr == null || hdr[0] != 0x03) return null;
            int payLen = ((hdr[2] << 8) | hdr[3]) - 4;
            return payLen > 0
                ? await ReadExactAsync(payLen, ct).ConfigureAwait(false)
                : new byte[0];
        }

        async Task<byte[]> ReadExactAsync (int count, CancellationToken ct) {
            byte[] buf = new byte[count];
            int read = 0;
            try {
                while (read < count) {
                    using (var linked = CancellationTokenSource.CreateLinkedTokenSource(ct)) {
                        linked.CancelAfter(TimeoutMs);
                        int n = await _stream.ReadAsync(buf, read, count - read, linked.Token)
                            .ConfigureAwait(false);
                        if (n == 0) {
                            Disconnect();
                            throw new Exception("连接已关闭");
                        }
                        read += n;
                    }
                }
                return buf;
            } catch (OperationCanceledException) {
                Disconnect();
                throw;
            } catch (Exception) {
                if (read < count)
                    Disconnect();
                throw;
            }
        }

        // ════════════════════════════════════════════════
        //  地址解析
        // ════════════════════════════════════════════════
        public static S7Address ParseAddress (string address) {
            if (string.IsNullOrWhiteSpace(address))
                throw new ArgumentException("地址为空");

            string a = address.Trim().ToUpperInvariant().Replace(" ", "");

            if (a.StartsWith("DB", StringComparison.Ordinal))
                return ParseDbAddress(a);

            if (a[0] == 'M' || a[0] == 'I' || a[0] == 'Q' ||
                a[0] == 'E' || a[0] == 'A' || a[0] == 'V')
                return ParseSimpleArea(a);

            throw new ArgumentException("无法解析的 S7 地址: " + address);
        }

        static S7Address ParseDbAddress (string a) {
            int dot1 = a.IndexOf('.');
            if (dot1 < 3) throw new ArgumentException("DB 地址格式错误: " + a);

            int dbNumber;
            if (!int.TryParse(a.Substring(2, dot1 - 2), out dbNumber) || dbNumber < 1)
                throw new ArgumentException("DB 号无效: " + a);

            string rest = a.Substring(dot1 + 1);

            if (rest.StartsWith("DBX", StringComparison.Ordinal)) {
                string[] parts = rest.Substring(3).Split('.');
                if (parts.Length != 2) throw new ArgumentException("位地址需为 DBn.DBXbyte.bit");
                int b, bit;
                if (!int.TryParse(parts[0], out b) || !int.TryParse(parts[1], out bit))
                    throw new ArgumentException("位地址数字无效");
                if (bit < 0 || bit > 7) throw new ArgumentException("位号须为 0–7");
                return S7Address.DbBit(dbNumber, b, bit);
            }
            if (rest.StartsWith("DBB", StringComparison.Ordinal))
                return S7Address.DbByte(dbNumber, ParseOffset(rest.Substring(3)));
            if (rest.StartsWith("DBW", StringComparison.Ordinal))
                return S7Address.DbWord(dbNumber, ParseOffset(rest.Substring(3)));
            if (rest.StartsWith("DBD", StringComparison.Ordinal))
                return S7Address.DbDWord(dbNumber, ParseOffset(rest.Substring(3)));

            throw new ArgumentException("不支持的 DB 子类型: " + a);
        }

        static S7Address ParseSimpleArea (string a) {
            char area = a[0];
            if (area == 'E') area = 'I';
            if (area == 'A') area = 'Q';

            string body = a.Substring(1);
            if (body.Contains(".")) {
                string[] parts = body.Split('.');
                int b, bit;
                if (!int.TryParse(parts[0], out b) || !int.TryParse(parts[1], out bit))
                    throw new ArgumentException("区位地址无效: " + a);
                if (bit < 0 || bit > 7) throw new ArgumentException("位号须为 0–7");
                return S7Address.AreaBit(area, b, bit);
            }
            if (body.StartsWith("B", StringComparison.Ordinal))
                return S7Address.AreaByte(area, ParseOffset(body.Substring(1)));
            if (body.StartsWith("W", StringComparison.Ordinal))
                return S7Address.AreaWord(area, ParseOffset(body.Substring(1)));
            if (body.StartsWith("D", StringComparison.Ordinal))
                return S7Address.AreaDWord(area, ParseOffset(body.Substring(1)));

            return S7Address.AreaByte(area, ParseOffset(body));
        }

        static int ParseOffset (string s) {
            int v;
            if (!int.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture, out v) || v < 0)
                throw new ArgumentException("偏移无效: " + s);
            return v;
        }

        public void Dispose () {
            if (_disposed) return;
            _disposed = true;
            Disconnect();
            try { _io.Dispose(); } catch { }
        }
    }

    internal struct S7Address {
        public char Area;
        public int DbNumber;
        public int ByteOffset;
        public int Bit;
        public S7TransportSize Size;

        public static S7Address DbBit (int db, int b, int bit) =>
            new S7Address { Area = 'D', DbNumber = db, ByteOffset = b, Bit = bit, Size = S7TransportSize.Bit };
        public static S7Address DbByte (int db, int b) =>
            new S7Address { Area = 'D', DbNumber = db, ByteOffset = b, Bit = -1, Size = S7TransportSize.Byte };
        public static S7Address DbWord (int db, int b) =>
            new S7Address { Area = 'D', DbNumber = db, ByteOffset = b, Bit = -1, Size = S7TransportSize.Word };
        public static S7Address DbDWord (int db, int b) =>
            new S7Address { Area = 'D', DbNumber = db, ByteOffset = b, Bit = -1, Size = S7TransportSize.DWord };

        public static S7Address AreaBit (char a, int b, int bit) =>
            new S7Address { Area = a, ByteOffset = b, Bit = bit, Size = S7TransportSize.Bit };
        public static S7Address AreaByte (char a, int b) =>
            new S7Address { Area = a, ByteOffset = b, Bit = -1, Size = S7TransportSize.Byte };
        public static S7Address AreaWord (char a, int b) =>
            new S7Address { Area = a, ByteOffset = b, Bit = -1, Size = S7TransportSize.Word };
        public static S7Address AreaDWord (char a, int b) =>
            new S7Address { Area = a, ByteOffset = b, Bit = -1, Size = S7TransportSize.DWord };
    }

    internal enum S7TransportSize { Bit, Byte, Word, DWord }
}