using System;
using System.Globalization;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using CommunicationDebuggingTools.Core;

namespace Plugin.Panasonic {
    /// <summary>
    /// 松下 MEWTOCOL-COM 会话：站号、地址解析、真异步 TCP 收发。
    /// 帧：% + 站号(2位十六进制) + # + 命令 + BCC(2位) + CR
    /// <para>
    /// 超时策略：CancelAfter 触发后强制 <see cref="Disconnect"/>，
    /// 避免 .NET Framework 上 ReadAsync 取消不彻底导致半开连接。
    /// </para>
    /// </summary>
    internal sealed class PanasonicSession : IDisposable {
        private const int DEFAULT_PORT = 9094;
        private const int MaxLineChars = 4096;

        private TcpClient _tcp;
        private NetworkStream _stream;
        private int _timeoutMs = AppConfig.DefaultTimeoutMs;
        private bool _disposed;
        private readonly SemaphoreSlim _io = new SemaphoreSlim(1, 1);

        /// <summary>读缓冲残留（多字节读入后未消费的字节）。</summary>
        private readonly byte[] _rxBuf = new byte[512];
        private int _rxLen;
        private int _rxPos;

        public int Station { get; private set; } = 1;

        public bool IsConnected {
            get {
                if (_tcp == null || _stream == null) return false;
                Socket s = _tcp.Client;
                if (s == null || !s.Connected) return false;
                try {
                    return !(s.Poll(0, SelectMode.SelectRead) && s.Available == 0);
                } catch {
                    return false;
                }
            }
        }

        public int TimeoutMs {
            get { return _timeoutMs; }
            set { _timeoutMs = value < 500 ? 500 : value; }
        }

        public async Task ConnectAsync (string ip, int port, int timeoutMs, CancellationToken ct) {
            Disconnect();
            if (string.IsNullOrWhiteSpace(ip))
                throw new ArgumentException("IP 为空");

            if (port <= 0)
                port = DEFAULT_PORT;

            TimeoutMs = timeoutMs > 0 ? timeoutMs : AppConfig.DefaultTimeoutMs;
            _tcp = new TcpClient();

            Task connectTask = _tcp.ConnectAsync(ip, port);
            Task timeoutTask = Task.Delay(TimeoutMs, ct);
            if (await Task.WhenAny(connectTask, timeoutTask).ConfigureAwait(false) != connectTask) {
                Disconnect();
                throw new TimeoutException("连接超时");
            }
            await connectTask.ConfigureAwait(false);

            if (!_tcp.Connected || ct.IsCancellationRequested) {
                Disconnect();
                throw new InvalidOperationException("连接失败或已取消");
            }

            _stream = _tcp.GetStream();
            ClearRx();
        }

        public void Disconnect () {
            ClearRx();
            try { if (_stream != null) { _stream.Close(); _stream = null; } } catch { }
            try { if (_tcp != null) { _tcp.Close(); _tcp = null; } } catch { }
        }

        public void SetStation (int station) {
            if (station < 0) station = 0;
            if (station > 99) station = 99;
            Station = station;
        }

        /// <summary>真异步：发送命令正文，返回到 CR 的 ASCII 响应（不含 CR）。</summary>
        public async Task<string> TransactAsync (string commandBody, CancellationToken ct) {
            if (string.IsNullOrEmpty(commandBody))
                throw new ArgumentException("命令为空");
            if (!IsConnected)
                throw new InvalidOperationException("未连接");

            string payload = Station.ToString("X2") + "#" + commandBody;
            string frame = "%" + payload + CalcBcc(payload) + "\r";
            byte[] send = Encoding.ASCII.GetBytes(frame);

            await _io.WaitAsync(ct).ConfigureAwait(false);
            try {
                if (!IsConnected)
                    throw new InvalidOperationException("未连接");
                // 新事务前清空残留，避免上一帧粘包干扰
                ClearRx();
                await WriteAllAsync(send, ct).ConfigureAwait(false);
                return await ReadLineCrAsync(ct).ConfigureAwait(false);
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
                Disconnect();
                throw;
            } catch (Exception) {
                Disconnect();
                throw;
            }
        }

        /// <summary>
        /// 缓冲读取至 CR：整块 ReadAsync，从本地环形残留中拼行。
        /// 避免逐字节系统调用；超时/取消后强制断连。
        /// </summary>
        private async Task<string> ReadLineCrAsync (CancellationToken ct) {
            var sb = new StringBuilder(64);
            int guard = 0;
            while (guard++ < 256) {
                // 先消费残留
                while (_rxPos < _rxLen) {
                    byte b = _rxBuf[_rxPos++];
                    if (b == (byte)'\r')
                        return sb.ToString();
                    if (b == (byte)'\n')
                        continue;
                    sb.Append((char)b);
                    if (sb.Length > MaxLineChars)
                        throw new Exception("响应过长");
                }

                // 再从网络补数据
                try {
                    using (var linked = CancellationTokenSource.CreateLinkedTokenSource(ct)) {
                        linked.CancelAfter(TimeoutMs);
                        int n = await _stream.ReadAsync(_rxBuf, 0, _rxBuf.Length, linked.Token)
                            .ConfigureAwait(false);
                        if (n <= 0) {
                            Disconnect();
                            throw new Exception("连接已断开或读超时");
                        }
                        _rxPos = 0;
                        _rxLen = n;
                    }
                } catch (OperationCanceledException) {
                    Disconnect();
                    throw;
                } catch (Exception) {
                    Disconnect();
                    throw;
                }
            }
            throw new Exception("响应无 CR 终止");
        }

        private void ClearRx () {
            _rxPos = 0;
            _rxLen = 0;
        }

        public static string CalcBcc (string payload) {
            byte x = 0;
            for (int i = 0; i < payload.Length; i++)
                x ^= (byte)payload[i];
            return x.ToString("X2");
        }

        public static string FormatContact (PanasonicAddress addr) {
            if (addr.Area == PanasonicArea.R) {
                // 字号 + 十六进制位号：R + 字(3位) + 位(1位十六进制)
                // 例：R10A → R010A，R1A → R001A
                if (addr.BitIndex >= 0)
                    return "R" + addr.Index.ToString("D3") + addr.BitIndex.ToString("X");

                // 纯十进制接点：R + 5 位
                // 例：R100 → R00100
                return "R" + addr.Index.ToString("D5");
            }
            if (addr.Area == PanasonicArea.X)
                return "X" + addr.Index.ToString("D5");
            if (addr.Area == PanasonicArea.Y)
                return "Y" + addr.Index.ToString("D5");
            throw new ArgumentException("非触点区域");
        }

        public static string FormatDataAddr (PanasonicAddress addr) {
            char code = addr.Area == PanasonicArea.WR ? 'W' : 'D';
            return code.ToString() + addr.Index.ToString("D5");
        }

        public static PanasonicAddress ParseAddress (string address) {
            if (string.IsNullOrWhiteSpace(address))
                throw new ArgumentException("地址为空");

            string a = address.Trim().ToUpperInvariant().Replace(" ", "");
            if (a.Length < 2)
                throw new ArgumentException("地址过短: " + address);

            char p0 = a[0];
            if (p0 == 'X')
                return ParseNumbered(a, 1, PanasonicArea.X, true);
            if (p0 == 'Y')
                return ParseNumbered(a, 1, PanasonicArea.Y, true);
            if (p0 == 'R')
                return ParseR(a);
            if (a.StartsWith("DT", StringComparison.Ordinal))
                return ParseNumbered(a, 2, PanasonicArea.DT, false);
            if (a.StartsWith("WR", StringComparison.Ordinal))
                return ParseNumbered(a, 2, PanasonicArea.WR, false);
            if (p0 == 'D')
                return ParseNumbered(a, 1, PanasonicArea.DT, false);
            if (p0 == 'W')
                return ParseNumbered(a, 1, PanasonicArea.WR, false);

            throw new ArgumentException("无法解析的地址: " + address);
        }

        private static PanasonicAddress ParseR (string a) {
            string body = a.Substring(1);
            if (body.Length == 0)
                throw new ArgumentException("R 地址为空");

            char last = body[body.Length - 1];
            if ((last >= 'A' && last <= 'F') || (last >= 'a' && last <= 'f')) {
                if (body.Length < 2)
                    throw new ArgumentException("R 位地址格式无效: " + a);

                string wordPart = body.Substring(0, body.Length - 1);
                for (int i = 0; i < wordPart.Length; i++) {
                    if (!char.IsDigit(wordPart[i]))
                        throw new ArgumentException("R 字号非法: " + a);
                }

                int word;
                if (!int.TryParse(wordPart, NumberStyles.Integer, CultureInfo.InvariantCulture, out word) || word < 0)
                    throw new ArgumentException("R 字号无效: " + a);

                int bit = Convert.ToInt32(last.ToString(), 16);
                return new PanasonicAddress {
                    Area = PanasonicArea.R,
                    Index = word,
                    BitIndex = bit,
                    IsBit = true
                };
            }

            for (int i = 0; i < body.Length; i++) {
                if (!char.IsDigit(body[i]))
                    throw new ArgumentException("R 地址非法: " + a);
            }

            int index;
            if (!int.TryParse(body, NumberStyles.Integer, CultureInfo.InvariantCulture, out index) || index < 0)
                throw new ArgumentException("R 地址无效: " + a);

            return new PanasonicAddress {
                Area = PanasonicArea.R,
                Index = index,
                BitIndex = -1,
                IsBit = true
            };
        }

        private static PanasonicAddress ParseNumbered (
            string a, int prefixLen, PanasonicArea area, bool isBit) {
            string body = a.Substring(prefixLen);
            int index;
            if (!int.TryParse(body, NumberStyles.Integer, CultureInfo.InvariantCulture, out index) || index < 0)
                throw new ArgumentException("地址编号无效: " + a);

            return new PanasonicAddress {
                Area = area,
                Index = index,
                BitIndex = -1,
                IsBit = isBit
            };
        }

        public void Dispose () {
            if (_disposed) return;
            _disposed = true;
            Disconnect();
            try { _io.Dispose(); } catch { }
        }
    }

    internal enum PanasonicArea {
        X, Y, R, DT, WR
    }

    internal struct PanasonicAddress {
        public PanasonicArea Area;
        public int Index;
        public int BitIndex;
        public bool IsBit;
    }
}
