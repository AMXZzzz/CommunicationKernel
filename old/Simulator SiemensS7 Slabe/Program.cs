using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Sockets;
using System.Threading;

// ============================================================
//  西门子 S7 TCP Slave 模拟器  (ISO-on-TCP, 端口 102)
//  C# 7.3 / .NET Framework 4.7 兼容
//  新建「控制台应用(.NET Framework)」替换 Program.cs，不要加进 WPF 项目
// ============================================================
namespace S7Sim {

    class Program {

        // ── 存储区 ──────────────────────────────────────
        static readonly byte[] AreaM = new byte[4096];
        static readonly byte[] AreaI = new byte[256];
        static readonly byte[] AreaQ = new byte[256];
        static readonly byte[] AreaV = new byte[4096];   // V memory (S7-200 style)
        static readonly Dictionary<int, byte[]> DB = new Dictionary<int, byte[]>();

        // S7 区域代码
        const byte AREA_I  = 0x81;
        const byte AREA_Q  = 0x82;
        const byte AREA_M  = 0x83;
        const byte AREA_DB = 0x84;
        const byte AREA_V  = 0x87;

        // 传输尺寸（请求 item 字段）
        const byte TS_BIT   = 0x01;
        const byte TS_BYTE  = 0x02;
        const byte TS_WORD  = 0x04;
        const byte TS_DWORD = 0x06;
        const byte TS_REAL  = 0x08;

        // 传输尺寸（响应数据字段）
        const byte RTS_BIT  = 0x03;
        const byte RTS_BYTE = 0x04;   // Byte/Word/DWord/Real 统一用 0x04

        const int PORT = 10200;
        static TcpListener _listener;
        static volatile bool _running = true;

        static int TimeOut = -1; // 15 秒超时

        // ── 入口 ────────────────────────────────────────
        static void Main (string[] args) {
            for (int i = 1; i <= 20; i++) DB[i] = new byte[4096];

            // 初始化测试值：DB1.DBD0 = 3.14f (IEEE 754 大端 = 40 49 0F D8 … 用 π 演示)
            byte[] pi = BitConverter.GetBytes(3.14159f);
            DB[1][0] = pi[3]; DB[1][1] = pi[2]; DB[1][2] = pi[1]; DB[1][3] = pi[0];
            AreaM[0] = 0xAB;

            Console.Title = "S7 Simulator";
            Console.WriteLine("西门子 S7 TCP 模拟器   端口: " + PORT);
            Console.WriteLine("支持: DB/M/I/Q   Read(04h) / Write(05h)\n");

            Console.CancelKeyPress += OnCancel;

            _listener = new TcpListener(IPAddress.Any, PORT);
            _listener.Server.SetSocketOption(
                System.Net.Sockets.SocketOptionLevel.Socket,
                System.Net.Sockets.SocketOptionName.ReuseAddress, true);
            try {
                _listener.Start();
            } catch (System.Net.Sockets.SocketException ex) {
                Console.ForegroundColor = ConsoleColor.Red;
                Console.WriteLine("\n[错误] 无法绑定端口 " + PORT + ": " + ex.Message);
                Console.WriteLine("请尝试：");
                Console.WriteLine("  1. 以【管理员身份】运行本程序（端口 102 需要管理员权限）");
                Console.WriteLine("  2. 或修改 PORT 常量改用其他端口（如 10200）");
                Console.ResetColor();
                Console.WriteLine("\n按任意键退出...");
                Console.ReadKey();
                return;
            }
            Console.WriteLine("等待连接...\n");

            while (_running) {
                try {
                    TcpClient client = _listener.AcceptTcpClient();
                    Thread t = new Thread(ClientThread);
                    t.IsBackground = true;
                    t.Start(client);
                } catch { }
            }
        }

        static void OnCancel (object s, ConsoleCancelEventArgs e) {
            e.Cancel = true;
            _running = false;
            try { _listener.Stop(); } catch { }
        }

        // ════════════════════════════════════════════════
        //  客户端线程
        // ════════════════════════════════════════════════
        static void ClientThread (object state) {
            TcpClient client = (TcpClient)state;
            string remote = client.Client.RemoteEndPoint != null
                ? client.Client.RemoteEndPoint.ToString() : "?";
            W(ConsoleColor.Green, "[" + Now() + "] 已连接: " + remote + "\n");

            NetworkStream stream = client.GetStream();
            stream.ReadTimeout = TimeOut;

            try {
                // ① COTP 握手
                byte[] pkt1 = ReadTpkt(stream);
                if (pkt1 != null && pkt1.Length >= 2 && pkt1[1] == 0xE0) {
                    SendTpkt(stream, BuildCotpCC(pkt1));
                    Log("COTP", "CR→CC 握手完成");
                }

                // ② S7 Setup Communication
                byte[] pkt2 = ReadTpkt(stream);
                byte[] s7s  = ExtractS7(pkt2);
                if (s7s != null && s7s.Length > 10 && s7s[10] == 0xF0) {
                    ushort pduRef = BE16(s7s, 4);
                    SendTpkt(stream, WrapDt(BuildSetupAck(pduRef)));
                    Log("S7", "Setup Communication 完成（PDU size=960）");
                }

                // ③ 读写循环
                while (client.Connected) {
                    byte[] pkt = ReadTpkt(stream);
                    if (pkt == null) break;
                    byte[] s7 = ExtractS7(pkt);
                    if (s7 == null || s7.Length < 12 || s7[1] != 0x01) continue;

                    ushort pduRef2 = BE16(s7, 4);
                    byte   func    = s7[10];

                    byte[] resp = null;
                    if (func == 0x04) resp = HandleRead(s7, pduRef2);
                    else if (func == 0x05) resp = HandleWrite(s7, pduRef2);
                    else Log("S7", "未知功能码: 0x" + func.ToString("X2"));

                    if (resp != null) SendTpkt(stream, WrapDt(resp));
                    Console.WriteLine();
                }
            } catch (Exception ex) {
                W(ConsoleColor.DarkYellow, "[" + Now() + "] " + remote + " 异常: " + ex.Message + "\n");
            } finally {
                client.Close();
                W(ConsoleColor.DarkYellow, "[" + Now() + "] 断开: " + remote + "\n\n");
            }
        }

        // ════════════════════════════════════════════════
        //  Read Variable 0x04
        // ════════════════════════════════════════════════
        static byte[] HandleRead (byte[] s7, ushort pduRef) {
            int paramOff = 10;
            if (s7.Length < paramOff + 2) return null;

            int count = s7[paramOff + 1];
            Log("Read", "变量数=" + count);

            List<byte[]> items = new List<byte[]>();

            for (int i = 0; i < count; i++) {
                int off = paramOff + 2 + i * 12;
                if (off + 12 > s7.Length) { items.Add(ErrItem()); continue; }

                byte ts      = s7[off + 3];
                int  dbNum   = (s7[off + 6] << 8) | s7[off + 7];
                byte area    = s7[off + 8];
                int  bitAddr = (s7[off + 9] << 16) | (s7[off + 10] << 8) | s7[off + 11];
                int  byteOff = bitAddr >> 3;
                int  bitNum  = bitAddr & 0x07;

                byte[] mem = GetArea(area, dbNum);
                if (mem == null || byteOff >= mem.Length) {
                    Log("  " + AreaStr(area, dbNum), "地址越界 byte=" + byteOff);
                    items.Add(ErrItem());
                    continue;
                }

                byte[] data = ReadMem(mem, byteOff, bitNum, ts);
                items.Add(OkItem(ts, data));

                W(ConsoleColor.DarkGray, "           ");
                W(ConsoleColor.Cyan, AreaStr(area, dbNum) + "+" + byteOff);
                W(ConsoleColor.DarkGray, " [" + TsName(ts) + "] → " + HexStr(data) + "  " + ValStr(ts, data) + "\n");
            }

            return BuildReadAck(pduRef, count, items);
        }

        // ════════════════════════════════════════════════
        //  Write Variable 0x05
        // ════════════════════════════════════════════════
        static byte[] HandleWrite (byte[] s7, ushort pduRef) {
            int paramOff = 10;
            if (s7.Length < paramOff + 2) return null;

            int count    = s7[paramOff + 1];
            int paramLen = (s7[6] << 8) | s7[7];
            int dStart   = 10 + paramLen;
            Log("Write", "变量数=" + count);

            // 提取所有 item spec（每个 12 字节）
            byte[] iTs    = new byte[count];
            int[]  iDbNum = new int [count];
            byte[] iArea  = new byte[count];
            int[]  iOff   = new int [count];
            int[]  iBit   = new int [count];

            for (int i = 0; i < count; i++) {
                int p    = paramOff + 2 + i * 12;
                iTs[i] = s7[p + 3];
                iDbNum[i] = (s7[p + 6] << 8) | s7[p + 7];
                iArea[i] = s7[p + 8];
                int ba    = (s7[p + 9] << 16) | (s7[p + 10] << 8) | s7[p + 11];
                iOff[i] = ba >> 3;
                iBit[i] = ba & 0x07;
            }

            // 解析 data section，写入存储区
            byte[] retCodes = new byte[count];
            int dOff = dStart;

            for (int i = 0; i < count; i++) {
                if (dOff + 4 > s7.Length) { retCodes[i] = 0x05; continue; }

                byte rts     = s7[dOff + 1];
                int  bitLen  = (s7[dOff + 2] << 8) | s7[dOff + 3];
                int  byteLen = (rts == RTS_BIT) ? 1 : (bitLen + 7) / 8;
                dOff += 4;

                if (dOff + byteLen > s7.Length) { retCodes[i] = 0x05; continue; }

                byte[] data = new byte[byteLen];
                Array.Copy(s7, dOff, data, 0, byteLen);
                dOff += byteLen;
                if (byteLen % 2 != 0 && i < count - 1) dOff++; // 补齐字节（最后一项不补）

                byte[] mem = GetArea(iArea[i], iDbNum[i]);
                if (mem == null || iOff[i] >= mem.Length) {
                    retCodes[i] = 0x05;
                    continue;
                }

                WriteMem(mem, iOff[i], iBit[i], iTs[i], data);
                retCodes[i] = 0xFF;

                W(ConsoleColor.DarkGray, "           ");
                W(ConsoleColor.Yellow, AreaStr(iArea[i], iDbNum[i]) + "+" + iOff[i]);
                W(ConsoleColor.DarkGray, " [" + TsName(iTs[i]) + "] ← " + HexStr(data) + "  " + ValStr(iTs[i], data) + "\n");
            }

            return BuildWriteAck(pduRef, retCodes);
        }

        // ════════════════════════════════════════════════
        //  内存读写
        // ════════════════════════════════════════════════
        static byte[] ReadMem (byte[] mem, int byteOff, int bitNum, byte ts) {
            if (ts == TS_BIT) {
                bool b = byteOff < mem.Length && ((mem[byteOff] >> bitNum) & 1) == 1;
                return new byte[] { (byte)(b ? 1 : 0) };
            }
            int size = TsBytes(ts);
            byte[] r = new byte[size];
            for (int i = 0; i < size; i++)
                r[i] = (byteOff + i < mem.Length) ? mem[byteOff + i] : (byte)0;
            return r;
        }

        static void WriteMem (byte[] mem, int byteOff, int bitNum, byte ts, byte[] data) {
            if (ts == TS_BIT) {
                if (byteOff >= mem.Length || data.Length == 0) return;
                if (data[0] != 0) mem[byteOff] |= (byte)(1 << bitNum);
                else mem[byteOff] &= (byte)~(1 << bitNum);
                return;
            }
            int size = Math.Min(TsBytes(ts), data.Length);
            for (int i = 0; i < size; i++)
                if (byteOff + i < mem.Length) mem[byteOff + i] = data[i];
        }

        static int TsBytes (byte ts) {
            switch (ts) {
                case TS_WORD: return 2;
                case TS_DWORD: case TS_REAL: return 4;
                default: return 1;
            }
        }

        static byte[] GetArea (byte area, int dbNum) {
            switch (area) {
                case AREA_M: return AreaM;
                case AREA_I: return AreaI;
                case AREA_Q: return AreaQ;
                case AREA_V: return AreaV;
                case AREA_DB:
                    if (!DB.ContainsKey(dbNum)) DB[dbNum] = new byte[4096];
                    return DB[dbNum];
                default: return null;
            }
        }

        // ════════════════════════════════════════════════
        //  PDU 构造
        // ════════════════════════════════════════════════
        static byte[] BuildReadAck (ushort pduRef, int count, List<byte[]> items) {
            byte[] param = new byte[] { 0x04, (byte)count };

            List<byte> dataBuf = new List<byte>();
            for (int i = 0; i < items.Count; i++) {
                byte[] item = items[i];
                dataBuf.AddRange(item);
                int dataBytes = item.Length - 4;
                if (dataBytes % 2 != 0 && i < items.Count - 1)
                    dataBuf.Add(0x00); // 填充
            }
            return BuildAckData(pduRef, param, dataBuf.ToArray());
        }

        static byte[] BuildWriteAck (ushort pduRef, byte[] retCodes) {
            return BuildAckData(pduRef, new byte[] { 0x05, (byte)retCodes.Length }, retCodes);
        }

        static byte[] BuildAckData (ushort pduRef, byte[] param, byte[] data) {
            byte[] s7 = new byte[12 + param.Length + data.Length];
            s7[0] = 0x32; s7[1] = 0x03;                     // magic, Ack-Data
            s7[2] = 0x00; s7[3] = 0x00;                     // reserved
            s7[4] = (byte)(pduRef >> 8);
            s7[5] = (byte)(pduRef & 0xFF);
            s7[6] = (byte)(param.Length >> 8);
            s7[7] = (byte)(param.Length & 0xFF);
            s7[8] = (byte)(data.Length >> 8);
            s7[9] = (byte)(data.Length & 0xFF);
            s7[10] = 0x00; s7[11] = 0x00;                     // no error
            Array.Copy(param, 0, s7, 12, param.Length);
            Array.Copy(data, 0, s7, 12 + param.Length, data.Length);
            return s7;
        }

        static byte[] OkItem (byte ts, byte[] data) {
            byte rts    = (ts == TS_BIT) ? RTS_BIT : RTS_BYTE;
            int  bitLen = (ts == TS_BIT) ? 1 : data.Length * 8;
            byte[] item = new byte[4 + data.Length];
            item[0] = 0xFF;
            item[1] = rts;
            item[2] = (byte)(bitLen >> 8);
            item[3] = (byte)(bitLen & 0xFF);
            Array.Copy(data, 0, item, 4, data.Length);
            return item;
        }

        static byte[] ErrItem () { return new byte[] { 0x05, 0x00, 0x00, 0x00 }; }

        static byte[] BuildSetupAck (ushort pduRef) {
            return BuildAckData(pduRef,
                new byte[] { 0xF0, 0x00, 0x00, 0x01, 0x00, 0x01, 0x03, 0xC0 },
                new byte[0]);
        }

        static byte[] BuildCotpCC (byte[] cr) {
            byte[] cc = (byte[])cr.Clone();
            cc[1] = 0xD0;                         // CC
            cc[2] = cr[4]; cc[3] = cr[5];         // dst ← src
            cc[4] = cr[2]; cc[5] = cr[3];         // src ← dst
            return cc;
        }

        static byte[] WrapDt (byte[] s7) {
            byte[] p = new byte[3 + s7.Length];
            p[0] = 0x02; p[1] = 0xF0; p[2] = 0x80;
            Array.Copy(s7, 0, p, 3, s7.Length);
            return p;
        }

        static byte[] ExtractS7 (byte[] payload) {
            if (payload == null || payload.Length < 4 || payload[1] != 0xF0) return null;
            byte[] s7 = new byte[payload.Length - 3];
            Array.Copy(payload, 3, s7, 0, s7.Length);
            return s7;
        }

        // ════════════════════════════════════════════════
        //  TPKT 读写
        // ════════════════════════════════════════════════
        static byte[] ReadTpkt (NetworkStream stream) {
            byte[] hdr = ReadExact(stream, 4);
            if (hdr == null || hdr[0] != 0x03) return null;
            int payLen = ((hdr[2] << 8) | hdr[3]) - 4;
            return payLen > 0 ? ReadExact(stream, payLen) : new byte[0];
        }

        static void SendTpkt (NetworkStream stream, byte[] payload) {
            int total = payload.Length + 4;
            byte[] pkt = new byte[total];
            pkt[0] = 0x03; pkt[1] = 0x00;
            pkt[2] = (byte)(total >> 8);
            pkt[3] = (byte)(total & 0xFF);
            Array.Copy(payload, 0, pkt, 4, payload.Length);
            stream.Write(pkt, 0, pkt.Length);
            stream.Flush();
        }

        static byte[] ReadExact (NetworkStream stream, int count) {
            byte[] buf  = new byte[count];
            int    read = 0;
            while (read < count) {
                int n = stream.Read(buf, read, count - read);
                if (n == 0) return null;
                read += n;
            }
            return buf;
        }

        static ushort BE16 (byte[] b, int off) { return (ushort)((b[off] << 8) | b[off + 1]); }

        // ════════════════════════════════════════════════
        //  打印辅助
        // ════════════════════════════════════════════════
        static string AreaStr (byte area, int dbNum) {
            switch (area) {
                case AREA_DB: return "DB" + dbNum;
                case AREA_M: return "M";
                case AREA_I: return "I";
                case AREA_Q: return "Q";
                case AREA_V: return "V";
                default: return "0x" + area.ToString("X2");
            }
        }

        static string TsName (byte ts) {
            switch (ts) {
                case TS_BIT: return "Bit";
                case TS_BYTE: return "Byte";
                case TS_WORD: return "Word";
                case TS_DWORD: return "DWord";
                case TS_REAL: return "Real";
                default: return "0x" + ts.ToString("X2");
            }
        }

        static string HexStr (byte[] d) {
            System.Text.StringBuilder sb = new System.Text.StringBuilder();
            foreach (byte b in d) sb.Append(b.ToString("X2")).Append(' ');
            return sb.ToString().TrimEnd();
        }

        static string ValStr (byte ts, byte[] d) {
            if (d == null || d.Length == 0) return "";
            try {
                switch (ts) {
                    case TS_BIT:
                        return "= " + (d[0] != 0 ? "TRUE" : "FALSE");
                    case TS_BYTE:
                        return "= " + d[0] + " (0x" + d[0].ToString("X2") + ")";
                    case TS_WORD:
                        int w = (d[0] << 8) | d[1];
                        return "= " + w + "  有符号:" + (short)w;
                    case TS_DWORD:
                        uint u = (uint)((d[0] << 24) | (d[1] << 16) | (d[2] << 8) | d[3]);
                        return "= " + u + "  有符号:" + (int)u;
                    case TS_REAL:
                        byte[] rb = new byte[] { d[3], d[2], d[1], d[0] };
                        return "= " + BitConverter.ToSingle(rb, 0).ToString("G7");
                    default:
                        return "";
                }
            } catch { return ""; }
        }

        static void Log (string label, string msg) {
            W(ConsoleColor.DarkGray, "           " + label.PadRight(8) + ": " + msg + "\n");
        }

        static void W (ConsoleColor color, string text) {
            Console.ForegroundColor = color;
            Console.Write(text);
            Console.ResetColor();
        }

        static string Now () { return DateTime.Now.ToString("HH:mm:ss.fff"); }
    }
}