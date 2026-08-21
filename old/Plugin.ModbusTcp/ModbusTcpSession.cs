using System;
using CommunicationDebuggingTools.Core;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using CommunicationDebuggingTools.Core.Enums;

namespace Plugin.ModbusTcp {
    /// <summary>
    /// Modbus TCP 会话底层：连接、MBAP、功能码与编解码。
    /// 仅供 <see cref="ModbusTcpProtocol"/> 使用。
    /// </summary>
    internal sealed class ModbusTcpSession : IDisposable {
        private TcpClient _tcp;
        private NetworkStream _stream;
        private byte _unitId = 1;
        private ushort _transactionId;
        private readonly SemaphoreSlim _io = new SemaphoreSlim(1, 1);
        private int _timeoutMs = AppConfig.DefaultTimeoutMs;
        private bool _disposed;

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

        public byte UnitId {
            get => _unitId;
            set => _unitId = value;
        }

        /// <summary>异步建立 TCP 连接。</summary>
        public async Task ConnectAsync (string ip, int port, int timeoutMs, CancellationToken ct) {
            Disconnect();
            if (string.IsNullOrWhiteSpace(ip))
                throw new ArgumentException("IP 为空");

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
        }

        public void Disconnect () {
            try { if (_stream != null) { _stream.Close(); _stream = null; } } catch { }
            try { if (_tcp != null) { _tcp.Close(); _tcp = null; } } catch { }
        }

        // -------------------- 功能码 --------------------

        /// <summary>读保持寄存器 FC03。</summary>
        public async Task<ushort[]> ReadHoldingRegistersAsync (int address, int count, CancellationToken ct) {
            EnsureConnected();
            if (count < 1 || count > 125)
                throw new ArgumentOutOfRangeException("count");

            byte[] pdu =
            {
                0x03,
                (byte)(address >> 8), (byte)(address & 0xFF),
                (byte)(count >> 8), (byte)(count & 0xFF)
            };

            byte[] resp = await SendAndReceiveAsync(pdu, ct).ConfigureAwait(false);
            CheckException(resp);

            if (resp[8] != count * 2)
                throw new Exception("返回字节数与请求不符");

            var regs = new ushort[count];
            for (int i = 0; i < count; i++) {
                int idx = 9 + i * 2;
                regs[i] = (ushort)((resp[idx] << 8) | resp[idx + 1]);
            }
            return regs;
        }

        /// <summary>写单寄存器 FC06。</summary>
        public async Task WriteSingleRegisterAsync (int address, ushort value, CancellationToken ct) {
            EnsureConnected();
            byte[] pdu =
            {
                0x06,
                (byte)(address >> 8), (byte)(address & 0xFF),
                (byte)(value >> 8), (byte)(value & 0xFF)
            };
            CheckException(await SendAndReceiveAsync(pdu, ct).ConfigureAwait(false));
        }

        /// <summary>写多寄存器 FC16。</summary>
        public async Task WriteMultipleRegistersAsync (int address, ushort[] values, CancellationToken ct) {
            EnsureConnected();
            if (values == null || values.Length < 1 || values.Length > 123)
                throw new ArgumentOutOfRangeException("values");

            int byteCount = values.Length * 2;
            var pdu = new byte[6 + byteCount];
            pdu[0] = 0x10;
            pdu[1] = (byte)(address >> 8);
            pdu[2] = (byte)(address & 0xFF);
            pdu[3] = (byte)(values.Length >> 8);
            pdu[4] = (byte)(values.Length & 0xFF);
            pdu[5] = (byte)byteCount;
            for (int i = 0; i < values.Length; i++) {
                pdu[6 + i * 2] = (byte)(values[i] >> 8);
                pdu[7 + i * 2] = (byte)(values[i] & 0xFF);
            }
            CheckException(await SendAndReceiveAsync(pdu, ct).ConfigureAwait(false));
        }

        /// <summary>读线圈 FC01。</summary>
        public async Task<bool[]> ReadCoilsAsync (int address, int count, CancellationToken ct) {
            EnsureConnected();
            if (count < 1 || count > 2000)
                throw new ArgumentOutOfRangeException("count");

            byte[] pdu =
            {
                0x01,
                (byte)(address >> 8), (byte)(address & 0xFF),
                (byte)(count >> 8), (byte)(count & 0xFF)
            };

            byte[] resp = await SendAndReceiveAsync(pdu, ct).ConfigureAwait(false);
            CheckException(resp);

            var coils = new bool[count];
            for (int i = 0; i < count; i++)
                coils[i] = (resp[9 + i / 8] & (1 << (i % 8))) != 0;
            return coils;
        }

        /// <summary>写单线圈 FC05。</summary>
        public async Task WriteSingleCoilAsync (int address, bool value, CancellationToken ct) {
            EnsureConnected();
            byte[] pdu =
            {
                0x05,
                (byte)(address >> 8), (byte)(address & 0xFF),
                value ? (byte)0xFF : (byte)0x00,
                0x00
            };
            CheckException(await SendAndReceiveAsync(pdu, ct).ConfigureAwait(false));
        }

        // -------------------- 地址 --------------------

        /// <summary>0 基或 4xxxx 保持寄存器地址。</summary>
        public static int ParseAddress (string address) {
            if (string.IsNullOrWhiteSpace(address))
                throw new ArgumentException("地址为空");

            address = address.Trim().ToUpperInvariant();
            int n;
            if (address.Length >= 5 && address[0] == '4' && int.TryParse(address, out n) && n >= 40001)
                return n - 40001;
            if (int.TryParse(address, out n) && n >= 0)
                return n;
            throw new ArgumentException("无法解析的 Modbus 地址: " + address);
        }

        // -------------------- 数值 ↔ 寄存器 --------------------

        public static int RegistersToInt32 (ushort high, ushort low, bool highWordFirst) =>
            RegistersToValue2(high, low, highWordFirst, BitConverter.ToInt32);

        public static uint RegistersToUInt32 (ushort high, ushort low, bool highWordFirst) =>
            RegistersToValue2(high, low, highWordFirst, BitConverter.ToUInt32);

        public static float RegistersToFloat (ushort high, ushort low, bool highWordFirst) =>
            RegistersToValue2(high, low, highWordFirst, BitConverter.ToSingle);

        public static void Int32ToRegisters (int value, out ushort high, out ushort low, bool highWordFirst) =>
            ValueToRegisters2(BitConverter.GetBytes(value), out high, out low, highWordFirst);

        public static void UInt32ToRegisters (uint value, out ushort high, out ushort low, bool highWordFirst) =>
            ValueToRegisters2(BitConverter.GetBytes(value), out high, out low, highWordFirst);

        public static void FloatToRegisters (float value, out ushort high, out ushort low, bool highWordFirst) =>
            ValueToRegisters2(BitConverter.GetBytes(value), out high, out low, highWordFirst);

        public static long RegistersToInt64 (ushort[] regs, bool highWordFirst) =>
            RegistersToValue4(regs, highWordFirst, BitConverter.ToInt64);

        public static ulong RegistersToUInt64 (ushort[] regs, bool highWordFirst) =>
            RegistersToValue4(regs, highWordFirst, BitConverter.ToUInt64);

        public static double RegistersToDouble (ushort[] regs, bool highWordFirst) =>
            RegistersToValue4(regs, highWordFirst, BitConverter.ToDouble);

        public static void Int64ToRegisters (long value, ushort[] regs, bool highWordFirst) =>
            ValueToRegisters4(BitConverter.GetBytes(value), regs, highWordFirst);

        public static void UInt64ToRegisters (ulong value, ushort[] regs, bool highWordFirst) =>
            ValueToRegisters4(BitConverter.GetBytes(value), regs, highWordFirst);

        public static void DoubleToRegisters (double value, ushort[] regs, bool highWordFirst) =>
            ValueToRegisters4(BitConverter.GetBytes(value), regs, highWordFirst);

        private static T RegistersToValue2<T> (
            ushort high, ushort low, bool highWordFirst, Func<byte[], int, T> converter) {
            ushort[] ordered = highWordFirst
                ? new[] { high, low }
                : new[] { low, high };
            return converter(PackWords(ordered), 0);
        }

        private static T RegistersToValue4<T> (
            ushort[] regs, bool highWordFirst, Func<byte[], int, T> converter) {
            if (regs == null || regs.Length < 4)
                throw new ArgumentException("需要 4 个寄存器");

            ushort[] ordered = highWordFirst
                ? new[] { regs[0], regs[1], regs[2], regs[3] }
                : new[] { regs[3], regs[2], regs[1], regs[0] };
            return converter(PackWords(ordered), 0);
        }

        private static void ValueToRegisters2 (
            byte[] hostBytes, out ushort high, out ushort low, bool highWordFirst) {
            ushort[] w = UnpackWords(hostBytes, 2);
            if (highWordFirst) { high = w[0]; low = w[1]; } else { high = w[1]; low = w[0]; }
        }

        private static void ValueToRegisters4 (byte[] hostBytes, ushort[] regs, bool highWordFirst) {
            if (regs == null || regs.Length < 4)
                throw new ArgumentException("需要 4 个寄存器");

            ushort[] w = UnpackWords(hostBytes, 4);
            if (highWordFirst) {
                regs[0] = w[0]; regs[1] = w[1]; regs[2] = w[2]; regs[3] = w[3];
            } else {
                regs[0] = w[3]; regs[1] = w[2]; regs[2] = w[1]; regs[3] = w[0];
            }
        }

        private static byte[] PackWords (ushort[] ordered) {
            var bytes = new byte[ordered.Length * 2];
            for (int i = 0; i < ordered.Length; i++) {
                bytes[i * 2] = (byte)(ordered[i] >> 8);
                bytes[i * 2 + 1] = (byte)(ordered[i] & 0xFF);
            }
            if (BitConverter.IsLittleEndian)
                Array.Reverse(bytes);
            return bytes;
        }

        private static ushort[] UnpackWords (byte[] hostBytes, int wordCount) {
            byte[] bytes = (byte[])hostBytes.Clone();
            if (BitConverter.IsLittleEndian)
                Array.Reverse(bytes);

            var w = new ushort[wordCount];
            for (int i = 0; i < wordCount; i++)
                w[i] = (ushort)((bytes[i * 2] << 8) | bytes[i * 2 + 1]);
            return w;
        }

        // -------------------- 字符串字节序 --------------------

        public static byte[] RegistersToBytes (ushort[] regs, ByteOrder byteOrder) {
            var bytes = new byte[regs.Length * 2];
            for (int i = 0; i < regs.Length; i++) {
                if (byteOrder == ByteOrder.BigEndian) {
                    bytes[i * 2] = (byte)(regs[i] >> 8);
                    bytes[i * 2 + 1] = (byte)(regs[i] & 0xFF);
                } else {
                    bytes[i * 2] = (byte)(regs[i] & 0xFF);
                    bytes[i * 2 + 1] = (byte)(regs[i] >> 8);
                }
            }
            return bytes;
        }

        public static ushort[] BytesToRegisters (byte[] bytes, ByteOrder byteOrder) {
            int regCount = (bytes.Length + 1) / 2;
            var regs = new ushort[regCount];
            for (int i = 0; i < regCount; i++) {
                int bi = i * 2;
                byte b0 = bi < bytes.Length ? bytes[bi] : (byte)0;
                byte b1 = bi + 1 < bytes.Length ? bytes[bi + 1] : (byte)0;
                regs[i] = byteOrder == ByteOrder.BigEndian
                    ? (ushort)((b0 << 8) | b1)
                    : (ushort)((b1 << 8) | b0);
            }
            return regs;
        }

        public static Encoding ToEncoding (StringEncodingKind kind) {
            switch (kind) {
                case StringEncodingKind.Ascii: return Encoding.ASCII;
                case StringEncodingKind.Utf16Le: return Encoding.Unicode;
                case StringEncodingKind.Utf16Be: return Encoding.BigEndianUnicode;
                case StringEncodingKind.DefaultAnsi: return Encoding.Default;
                default: return Encoding.UTF8;
            }
        }

        // -------------------- MBAP --------------------

        private void EnsureConnected () {
            if (!IsConnected)
                throw new InvalidOperationException("未连接");
        }

        private async Task<byte[]> SendAndReceiveAsync (byte[] pdu, CancellationToken ct) {
            await _io.WaitAsync(ct).ConfigureAwait(false);
            try {
                EnsureConnected();
                _transactionId++;
                ushort tid = _transactionId;
                int len = 1 + pdu.Length;
                var frame = new byte[7 + pdu.Length];
                frame[0] = (byte)(tid >> 8);
                frame[1] = (byte)(tid & 0xFF);
                frame[2] = 0x00;
                frame[3] = 0x00;
                frame[4] = (byte)(len >> 8);
                frame[5] = (byte)(len & 0xFF);
                frame[6] = _unitId;
                Buffer.BlockCopy(pdu, 0, frame, 7, pdu.Length);

                await WriteAllAsync(frame, ct).ConfigureAwait(false);

                byte[] header = await ReadExactAsync(7, ct).ConfigureAwait(false);
                int pduLen = ((header[4] << 8) | header[5]) - 1;
                if (pduLen < 2)
                    throw new Exception("PDU 长度非法");

                byte[] body = await ReadExactAsync(pduLen, ct).ConfigureAwait(false);
                var resp = new byte[7 + pduLen];
                Buffer.BlockCopy(header, 0, resp, 0, 7);
                Buffer.BlockCopy(body, 0, resp, 7, pduLen);
                return resp;
            } finally {
                _io.Release();
            }
        }

        private async Task WriteAllAsync (byte[] data, CancellationToken ct) {
            try {
                using (var linked = CancellationTokenSource.CreateLinkedTokenSource(ct)) {
                    linked.CancelAfter(TimeoutMs);
                    await _stream.WriteAsync(data, 0, data.Length, linked.Token).ConfigureAwait(false);
                    await _stream.FlushAsync(linked.Token).ConfigureAwait(false);
                }
            } catch (OperationCanceledException) {
                // 超时/取消后强拆，避免半开连接
                Disconnect();
                throw;
            } catch {
                Disconnect();
                throw;
            }
        }

        private async Task<byte[]> ReadExactAsync (int size, CancellationToken ct) {
            var buf = new byte[size];
            int offset = 0;
            try {
                while (offset < size) {
                    using (var linked = CancellationTokenSource.CreateLinkedTokenSource(ct)) {
                        linked.CancelAfter(TimeoutMs);
                        int n = await _stream.ReadAsync(buf, offset, size - offset, linked.Token)
                            .ConfigureAwait(false);
                        if (n <= 0) {
                            Disconnect();
                            throw new Exception("连接已断开或读超时");
                        }
                        offset += n;
                    }
                }
                return buf;
            } catch (OperationCanceledException) {
                Disconnect();
                throw;
            } catch (Exception) {
                if (offset < size)
                    Disconnect();
                throw;
            }
        }

        private static void CheckException (byte[] resp) {
            if (resp == null || resp.Length < 9)
                throw new Exception("响应无效");
            if ((resp[7] & 0x80) != 0)
                throw new Exception(string.Format("Modbus 异常码: 0x{0:X2}", resp[8]));
        }

        public void Dispose () {
            if (_disposed) return;
            _disposed = true;
            Disconnect();
            try { _io.Dispose(); } catch { }
        }
    }
}