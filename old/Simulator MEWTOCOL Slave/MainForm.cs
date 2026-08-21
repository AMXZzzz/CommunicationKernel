using System;
using System.Collections.Generic;
using System.Drawing;
using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Windows.Forms;

namespace MEWTOCOL_Slave {
    /// <summary>
    /// 简易 MEWTOCOL 从站：可改 DT/接点 + 日志；启动即监听。
    /// </summary>
    public class MainForm : Form {
        readonly Dictionary<int, ushort> _dt = new Dictionary<int, ushort>();
        readonly Dictionary<string, bool> _contacts = new Dictionary<string, bool>();
        readonly object _lock = new object();

        TcpListener _listener;
        volatile bool _running;
        string _station = "01";
        int _port = 9094;

        /// <summary>用户正在编辑表格时，禁止整表刷新，避免抢走焦点。</summary>
        volatile bool _uiEditing;

        TextBox _txtPort;
        TextBox _txtStation;
        Button _btnStart;
        Label _lblStatus;
        DataGridView _gridDt;
        DataGridView _gridContact;
        TextBox _log;
        TextBox _txtDtAddr;
        TextBox _txtDtVal;
        TextBox _txtCtKey;
        CheckBox _chkCtOn;
        System.Windows.Forms.Timer _uiTimer;

        public MainForm () {
            Text = "MEWTOCOL Slave 模拟器";
            Width = 920;
            Height = 640;
            StartPosition = FormStartPosition.CenterScreen;
            Font = new Font("Microsoft YaHei UI", 9F);

            BuildUi();
            SeedDefaults();
            RefreshDtGrid(force: true);
            RefreshContactGrid(force: true);

            // 启动后自动开监听
            Shown += (s, e) => {
                if (!_running)
                    StartServer();
            };
            FormClosing += (s, e) => StopServer();

            // 低频刷新（不在编辑中才刷），避免每条报文整表重建
            _uiTimer = new System.Windows.Forms.Timer { Interval = 800 };
            _uiTimer.Tick += (s, e) => {
                if (_uiEditing) return;
                if (_gridDt.IsCurrentCellInEditMode || _gridContact.IsCurrentCellInEditMode)
                    return;
                RefreshDtGrid(force: false);
                RefreshContactGrid(force: false);
            };
            _uiTimer.Start();
        }

        void BuildUi () {
            var top = new Panel { Dock = DockStyle.Top, Height = 44, Padding = new Padding(8) };
            top.Controls.Add(new Label { Text = "端口", Left = 8, Top = 12, AutoSize = true });
            _txtPort = new TextBox { Text = "9094", Left = 44, Top = 8, Width = 70 };
            top.Controls.Add(_txtPort);
            top.Controls.Add(new Label { Text = "站号", Left = 130, Top = 12, AutoSize = true });
            _txtStation = new TextBox { Text = "01", Left = 168, Top = 8, Width = 40 };
            top.Controls.Add(_txtStation);
            _btnStart = new Button { Text = "停止", Left = 230, Top = 6, Width = 90, Height = 28 };
            _btnStart.Click += (s, e) => {
                if (_running) StopServer();
                else StartServer();
            };
            top.Controls.Add(_btnStart);
            _lblStatus = new Label {
                Text = "准备启动…", Left = 330, Top = 12, AutoSize = true,
                ForeColor = Color.Gray
            };
            top.Controls.Add(_lblStatus);
            Controls.Add(top);

            var split = new SplitContainer {
                Dock = DockStyle.Fill,
                Orientation = Orientation.Horizontal,
                SplitterDistance = 360
            };

            var mid = new SplitContainer {
                Dock = DockStyle.Fill,
                Orientation = Orientation.Vertical,
                SplitterDistance = 480
            };

            // DT
            var dtPanel = new Panel { Dock = DockStyle.Fill, Padding = new Padding(8) };
            var dtBar = new FlowLayoutPanel { Dock = DockStyle.Top, Height = 36, WrapContents = false };
            dtBar.Controls.Add(new Label { Text = "DT 寄存器", AutoSize = true, Margin = new Padding(0, 8, 8, 0) });
            _txtDtAddr = new TextBox { Width = 60, Text = "100" };
            _txtDtVal = new TextBox { Width = 80, Text = "0" };
            var btnDt = new Button { Text = "写入 DT", Width = 80, Height = 26 };
            btnDt.Click += (s, e) => ApplyDt();
            var btnDtRef = new Button { Text = "刷新", Width = 60, Height = 26 };
            btnDtRef.Click += (s, e) => RefreshDtGrid(force: true);
            dtBar.Controls.Add(new Label { Text = "地址", AutoSize = true, Margin = new Padding(8, 8, 4, 0) });
            dtBar.Controls.Add(_txtDtAddr);
            dtBar.Controls.Add(new Label { Text = "值", AutoSize = true, Margin = new Padding(8, 8, 4, 0) });
            dtBar.Controls.Add(_txtDtVal);
            dtBar.Controls.Add(btnDt);
            dtBar.Controls.Add(btnDtRef);
            _gridDt = MakeGrid();
            _gridDt.Columns.Add("Addr", "地址");
            _gridDt.Columns.Add("Dec", "十进制");
            _gridDt.Columns.Add("Hex", "十六进制");
            _gridDt.Columns[0].ReadOnly = true;
            _gridDt.Columns[0].Width = 80;
            _gridDt.Columns[1].Width = 100;
            _gridDt.Columns[2].Width = 100;
            _gridDt.Dock = DockStyle.Fill;
            _gridDt.CellBeginEdit += (s, e) => { _uiEditing = true; };
            _gridDt.CellEndEdit += GridDt_CellEndEdit;
            _gridDt.Leave += (s, e) => { if (!_gridDt.IsCurrentCellInEditMode) _uiEditing = false; };
            dtPanel.Controls.Add(_gridDt);
            dtPanel.Controls.Add(dtBar);
            mid.Panel1.Controls.Add(dtPanel);

            // 接点
            var ctPanel = new Panel { Dock = DockStyle.Fill, Padding = new Padding(8) };
            var ctBar = new FlowLayoutPanel { Dock = DockStyle.Top, Height = 36, WrapContents = false };
            ctBar.Controls.Add(new Label { Text = "接点 R", AutoSize = true, Margin = new Padding(0, 8, 8, 0) });
            _txtCtKey = new TextBox { Width = 70, Text = "00100" };
            _chkCtOn = new CheckBox { Text = "ON", AutoSize = true, Margin = new Padding(8, 8, 8, 0) };
            var btnCt = new Button { Text = "写入接点", Width = 80, Height = 26 };
            btnCt.Click += (s, e) => ApplyContact();
            var btnCtRef = new Button { Text = "刷新", Width = 60, Height = 26 };
            btnCtRef.Click += (s, e) => RefreshContactGrid(force: true);
            ctBar.Controls.Add(new Label { Text = "编号", AutoSize = true, Margin = new Padding(4, 8, 4, 0) });
            ctBar.Controls.Add(_txtCtKey);
            ctBar.Controls.Add(_chkCtOn);
            ctBar.Controls.Add(btnCt);
            ctBar.Controls.Add(btnCtRef);
            _gridContact = MakeGrid();
            _gridContact.Columns.Add("Key", "接点");
            _gridContact.Columns.Add("Val", "状态");
            _gridContact.Columns[0].ReadOnly = true;
            _gridContact.Columns[0].Width = 100;
            _gridContact.Columns[1].Width = 80;
            _gridContact.Dock = DockStyle.Fill;
            _gridContact.CellBeginEdit += (s, e) => { _uiEditing = true; };
            _gridContact.CellEndEdit += GridContact_CellEndEdit;
            _gridContact.Leave += (s, e) => { if (!_gridContact.IsCurrentCellInEditMode) _uiEditing = false; };
            // 双击接点行切换 ON/OFF，更省事
            _gridContact.CellDoubleClick += (s, e) => {
                if (e.RowIndex < 0) return;
                string key = Convert.ToString(_gridContact.Rows[e.RowIndex].Cells[0].Value) ?? "";
                if (key.StartsWith("R")) key = key.Substring(1);
                lock (_lock) {
                    bool cur = _contacts.ContainsKey(key) && _contacts[key];
                    _contacts[key] = !cur;
                }
                RefreshContactGrid(force: true);
            };
            ctPanel.Controls.Add(_gridContact);
            ctPanel.Controls.Add(ctBar);
            mid.Panel2.Controls.Add(ctPanel);

            split.Panel1.Controls.Add(mid);

            _log = new TextBox {
                Dock = DockStyle.Fill,
                Multiline = true,
                ScrollBars = ScrollBars.Vertical,
                ReadOnly = true,
                Font = new Font("Consolas", 9F),
                BackColor = Color.FromArgb(30, 30, 30),
                ForeColor = Color.Gainsboro
            };
            var logPanel = new Panel { Dock = DockStyle.Fill, Padding = new Padding(8) };
            logPanel.Controls.Add(_log);
            logPanel.Controls.Add(new Label {
                Text = "通讯日志", Dock = DockStyle.Top, Height = 22
            });
            split.Panel2.Controls.Add(logPanel);

            var host = new Panel { Dock = DockStyle.Fill };
            host.Controls.Add(split);
            Controls.Add(host);
            host.BringToFront();
            top.BringToFront();
        }

        static DataGridView MakeGrid () {
            return new DataGridView {
                AllowUserToAddRows = false,
                AllowUserToDeleteRows = false,
                RowHeadersVisible = false,
                SelectionMode = DataGridViewSelectionMode.CellSelect,
                EditMode = DataGridViewEditMode.EditOnKeystrokeOrF2,
                AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.None,
                BackgroundColor = Color.White,
                MultiSelect = false
            };
        }

        void SeedDefaults () {
            lock (_lock) {
                _dt[100] = 0;
                _dt[200] = 0;
                _contacts["00100"] = false;
                _contacts["010A"] = false;
            }
        }

        void StartServer () {
            if (_running) return;
            int port;
            if (!int.TryParse(_txtPort.Text.Trim(), out port) || port <= 0) {
                MessageBox.Show("端口无效");
                return;
            }
            string st = (_txtStation.Text ?? "01").Trim().PadLeft(2, '0');
            if (st.Length > 2) st = st.Substring(0, 2);
            _station = st;
            _port = port;
            _running = true;
            _btnStart.Text = "停止";
            _lblStatus.Text = "监听 " + _port + "  站号 " + _station;
            _lblStatus.ForeColor = Color.ForestGreen;
            _txtPort.Enabled = false;
            _txtStation.Enabled = false;
            Thread t = new Thread(ServerLoop) { IsBackground = true };
            t.Start();
            AppendLog("系统", "已启动 端口=" + _port + " 站号=" + _station);
        }

        void StopServer () {
            _running = false;
            try { if (_listener != null) _listener.Stop(); } catch { }
            _listener = null;
            void Ui () {
                _btnStart.Text = "启动监听";
                _lblStatus.Text = "已停止";
                _lblStatus.ForeColor = Color.Gray;
                _txtPort.Enabled = true;
                _txtStation.Enabled = true;
            }
            if (IsHandleCreated) {
                try { BeginInvoke(new Action(Ui)); } catch { }
            } else {
                Ui();
            }
            AppendLog("系统", "已停止监听");
        }

        void ServerLoop () {
            try {
                _listener = new TcpListener(IPAddress.Any, _port);
                _listener.Start();
                while (_running) {
                    try {
                        if (!_listener.Pending()) {
                            Thread.Sleep(50);
                            continue;
                        }
                        TcpClient client = _listener.AcceptTcpClient();
                        AppendLog("连接", client.Client.RemoteEndPoint.ToString());
                        Thread ct = new Thread(() => HandleClient(client)) { IsBackground = true };
                        ct.Start();
                    } catch {
                        if (!_running) break;
                    }
                }
            } catch (Exception ex) {
                AppendLog("错误", ex.Message);
                StopServer();
            }
        }

        void HandleClient (TcpClient client) {
            try {
                NetworkStream stream = client.GetStream();
                stream.ReadTimeout = 30000;
                var buf = new byte[4096];
                var acc = new StringBuilder();
                while (_running && client.Connected) {
                    int n;
                    try { n = stream.Read(buf, 0, buf.Length); }
                    catch { break; }
                    if (n <= 0) break;
                    acc.Append(Encoding.ASCII.GetString(buf, 0, n));
                    string all = acc.ToString();
                    int cr;
                    while ((cr = all.IndexOf('\r')) >= 0) {
                        string frame = all.Substring(0, cr).Trim('\n');
                        all = all.Substring(cr + 1);
                        if (string.IsNullOrEmpty(frame)) continue;
                        AppendLog("收", frame);
                        string resp = ProcessFrame(frame);
                        if (resp != null) {
                            byte[] outb = Encoding.ASCII.GetBytes(resp + "\r");
                            stream.Write(outb, 0, outb.Length);
                            stream.Flush();
                            AppendLog("发", resp);
                            // 不在此整表刷新：交给定时器，避免打断编辑
                        }
                    }
                    acc.Clear();
                    acc.Append(all);
                }
            } catch (Exception ex) {
                AppendLog("会话", ex.Message);
            } finally {
                try { client.Close(); } catch { }
                AppendLog("连接", "已断开");
            }
        }

        string ProcessFrame (string frame) {
            if (frame.Length < 8 || frame[0] != '%')
                return MakeError(_station, "0121");

            string station = frame.Substring(1, 2);
            if (station != _station && station != "EE")
                return null;

            int sharp = frame.IndexOf('#');
            if (sharp < 0 || frame.Length < sharp + 3)
                return MakeError(station, "0121");

            string payload = frame.Substring(1, frame.Length - 3);
            string bccRecv = frame.Substring(frame.Length - 2);
            if (!string.Equals(bccRecv, Bcc(payload), StringComparison.OrdinalIgnoreCase))
                return MakeError(station, "0120");

            string cmd = frame.Substring(sharp + 1, frame.Length - sharp - 3);
            try {
                if (cmd.StartsWith("RD", StringComparison.Ordinal))
                    return DoReadData(station, cmd);
                if (cmd.StartsWith("WD", StringComparison.Ordinal))
                    return DoWriteData(station, cmd);
                if (cmd.StartsWith("RCS", StringComparison.Ordinal))
                    return DoReadContact(station, cmd);
                if (cmd.StartsWith("WCS", StringComparison.Ordinal))
                    return DoWriteContact(station, cmd);
                return MakeError(station, "0122");
            } catch {
                return MakeError(station, "0121");
            }
        }

        string DoReadData (string station, string cmd) {
            int start, end;
            if (!TryParseDataRange(cmd, 2, out start, out end))
                return MakeError(station, "0121");
            int count = end - start + 1;
            if (count < 1 || count > 500)
                return MakeError(station, "0323");
            var data = new StringBuilder();
            lock (_lock) {
                for (int i = 0; i < count; i++)
                    data.Append(SwapBytes(GetDt(start + i)).ToString("X4"));
            }
            return MakeNormal(station, "RD" + data);
        }

        string DoWriteData (string station, string cmd) {
            int start, end;
            if (!TryParseDataRange(cmd, 2, out start, out end))
                return MakeError(station, "0121");
            int count = end - start + 1;
            int dataPos = 2 + 12;
            if (cmd.Length < dataPos + count * 4)
                return MakeError(station, "0121");
            string hex = cmd.Substring(dataPos);
            lock (_lock) {
                for (int i = 0; i < count; i++) {
                    ushort wire;
                    if (!ushort.TryParse(hex.Substring(i * 4, 4), NumberStyles.HexNumber,
                            CultureInfo.InvariantCulture, out wire))
                        return MakeError(station, "0121");
                    _dt[start + i] = SwapBytes(wire);
                }
            }
            AppendLog("WD", "DT" + start + ".." + end);
            return MakeNormal(station, "WD");
        }

        string DoReadContact (string station, string cmd) {
            if (cmd.Length < 8) return MakeError(station, "0121");
            string key = cmd.Substring(3, 5).ToUpperInvariant();
            bool val;
            lock (_lock) { val = _contacts.ContainsKey(key) && _contacts[key]; }
            return MakeNormal(station, "RC" + (val ? "1" : "0"));
        }

        string DoWriteContact (string station, string cmd) {
            if (cmd.Length < 9) return MakeError(station, "0121");
            string key = cmd.Substring(3, 5).ToUpperInvariant();
            char v = cmd[8];
            if (v != '0' && v != '1') return MakeError(station, "0121");
            lock (_lock) { _contacts[key] = v == '1'; }
            AppendLog("WCS", key + "=" + (v == '1' ? "ON" : "OFF"));
            return MakeNormal(station, "WC");
        }

        static bool TryParseDataRange (string cmd, int cmdLen, out int start, out int end) {
            start = end = 0;
            if (cmd.Length < cmdLen + 12) return false;
            if (cmd[cmdLen] != 'D' && cmd[cmdLen] != 'W') return false;
            if (cmd[cmdLen + 6] != cmd[cmdLen]) return false;
            if (!int.TryParse(cmd.Substring(cmdLen + 1, 5), NumberStyles.Integer,
                    CultureInfo.InvariantCulture, out start)) return false;
            if (!int.TryParse(cmd.Substring(cmdLen + 7, 5), NumberStyles.Integer,
                    CultureInfo.InvariantCulture, out end)) return false;
            return end >= start;
        }

        ushort GetDt (int addr) {
            ushort v;
            return _dt.TryGetValue(addr, out v) ? v : (ushort)0;
        }

        static ushort SwapBytes (ushort w) {
            return (ushort)((w << 8) | (w >> 8));
        }

        static string MakeNormal (string station, string body) {
            string c = station + "$" + body;
            return "%" + c + Bcc(c);
        }

        static string MakeError (string station, string code) {
            string c = station + "!" + code;
            return "%" + c + Bcc(c);
        }

        static string Bcc (string s) {
            byte x = 0;
            foreach (char ch in s) x ^= (byte)ch;
            return x.ToString("X2");
        }

        void ApplyDt () {
            int addr;
            int val;
            if (!int.TryParse(_txtDtAddr.Text.Trim(), out addr)) {
                MessageBox.Show("地址无效");
                return;
            }
            string raw = _txtDtVal.Text.Trim();
            if (raw.StartsWith("0x", StringComparison.OrdinalIgnoreCase)) {
                if (!int.TryParse(raw.Substring(2), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out val)) {
                    MessageBox.Show("值无效");
                    return;
                }
            } else if (!int.TryParse(raw, out val)) {
                MessageBox.Show("值无效");
                return;
            }
            lock (_lock) { _dt[addr] = (ushort)(val & 0xFFFF); }
            RefreshDtGrid(force: true);
            AppendLog("UI", "DT" + addr + " = " + (val & 0xFFFF));
        }

        void ApplyContact () {
            string key = (_txtCtKey.Text ?? "").Trim().ToUpperInvariant();
            if (key.Length == 0) return;
            if (key.Length < 5) key = key.PadLeft(5, '0');
            if (key.Length > 5) key = key.Substring(key.Length - 5);
            lock (_lock) { _contacts[key] = _chkCtOn.Checked; }
            RefreshContactGrid(force: true);
            AppendLog("UI", "R" + key + " = " + (_chkCtOn.Checked ? "ON" : "OFF"));
        }

        void GridDt_CellEndEdit (object sender, DataGridViewCellEventArgs e) {
            try {
                if (e.RowIndex < 0) return;
                var row = _gridDt.Rows[e.RowIndex];
                string addrText = Convert.ToString(row.Cells[0].Value) ?? "";
                if (addrText.StartsWith("DT", StringComparison.OrdinalIgnoreCase))
                    addrText = addrText.Substring(2);
                int addr;
                if (!int.TryParse(addrText, out addr)) return;

                string text = Convert.ToString(row.Cells[e.ColumnIndex].Value) ?? "0";
                int val;
                if (e.ColumnIndex == 2) {
                    text = text.Replace("0x", "").Replace("0X", "").Trim();
                    if (!int.TryParse(text, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out val))
                        return;
                } else if (e.ColumnIndex == 1) {
                    if (!int.TryParse(text.Trim(), out val)) return;
                } else return;

                lock (_lock) { _dt[addr] = (ushort)(val & 0xFFFF); }
                // 只更新当前行显示，不全表清
                row.Cells[1].Value = ((ushort)(val & 0xFFFF)).ToString();
                row.Cells[2].Value = "0x" + ((ushort)(val & 0xFFFF)).ToString("X4");
                AppendLog("UI", "DT" + addr + " = " + (val & 0xFFFF));
            } finally {
                _uiEditing = false;
            }
        }

        void GridContact_CellEndEdit (object sender, DataGridViewCellEventArgs e) {
            try {
                if (e.RowIndex < 0 || e.ColumnIndex != 1) return;
                var row = _gridContact.Rows[e.RowIndex];
                string key = Convert.ToString(row.Cells[0].Value) ?? "";
                if (key.StartsWith("R")) key = key.Substring(1);
                string v = (Convert.ToString(row.Cells[1].Value) ?? "").Trim().ToUpperInvariant();
                bool on = v == "ON" || v == "1" || v == "TRUE";
                lock (_lock) { _contacts[key] = on; }
                row.Cells[1].Value = on ? "ON" : "OFF";
                AppendLog("UI", "R" + key + " = " + (on ? "ON" : "OFF"));
            } finally {
                _uiEditing = false;
            }
        }

        void RefreshDtGrid (bool force) {
            if (InvokeRequired) {
                BeginInvoke(new Action(() => RefreshDtGrid(force)));
                return;
            }
            if (!force) {
                if (_uiEditing || _gridDt.IsCurrentCellInEditMode)
                    return;
            }

            List<KeyValuePair<int, ushort>> snap;
            lock (_lock) {
                snap = new List<KeyValuePair<int, ushort>>(_dt);
            }
            snap.Sort((a, b) => a.Key.CompareTo(b.Key));

            // 差量更新：地址列只读，尽量保留选中
            int sel = _gridDt.CurrentCell != null ? _gridDt.CurrentCell.RowIndex : -1;
            int col = _gridDt.CurrentCell != null ? _gridDt.CurrentCell.ColumnIndex : 1;

            while (_gridDt.Rows.Count > snap.Count)
                _gridDt.Rows.RemoveAt(_gridDt.Rows.Count - 1);
            for (int i = 0; i < snap.Count; i++) {
                string addr = "DT" + snap[i].Key;
                string dec = snap[i].Value.ToString();
                string hex = "0x" + snap[i].Value.ToString("X4");
                if (i >= _gridDt.Rows.Count) {
                    _gridDt.Rows.Add(addr, dec, hex);
                } else {
                    var row = _gridDt.Rows[i];
                    if (!Equals(row.Cells[0].Value, addr)) row.Cells[0].Value = addr;
                    if (!Equals(row.Cells[1].Value, dec)) row.Cells[1].Value = dec;
                    if (!Equals(row.Cells[2].Value, hex)) row.Cells[2].Value = hex;
                }
            }
            if (sel >= 0 && sel < _gridDt.Rows.Count && col >= 0 && col < _gridDt.Columns.Count) {
                try { _gridDt.CurrentCell = _gridDt.Rows[sel].Cells[col]; } catch { }
            }
        }

        void RefreshContactGrid (bool force) {
            if (InvokeRequired) {
                BeginInvoke(new Action(() => RefreshContactGrid(force)));
                return;
            }
            if (!force) {
                if (_uiEditing || _gridContact.IsCurrentCellInEditMode)
                    return;
            }

            List<KeyValuePair<string, bool>> snap;
            lock (_lock) {
                snap = new List<KeyValuePair<string, bool>>(_contacts);
            }
            snap.Sort((a, b) => string.CompareOrdinal(a.Key, b.Key));

            int sel = _gridContact.CurrentCell != null ? _gridContact.CurrentCell.RowIndex : -1;

            while (_gridContact.Rows.Count > snap.Count)
                _gridContact.Rows.RemoveAt(_gridContact.Rows.Count - 1);
            for (int i = 0; i < snap.Count; i++) {
                string key = "R" + snap[i].Key;
                string val = snap[i].Value ? "ON" : "OFF";
                if (i >= _gridContact.Rows.Count) {
                    _gridContact.Rows.Add(key, val);
                } else {
                    var row = _gridContact.Rows[i];
                    if (!Equals(row.Cells[0].Value, key)) row.Cells[0].Value = key;
                    if (!Equals(row.Cells[1].Value, val)) row.Cells[1].Value = val;
                }
            }
            if (sel >= 0 && sel < _gridContact.Rows.Count) {
                try { _gridContact.CurrentCell = _gridContact.Rows[sel].Cells[1]; } catch { }
            }
        }

        void AppendLog (string tag, string msg) {
            string line = DateTime.Now.ToString("HH:mm:ss.fff") + "  [" + tag + "] " + msg + Environment.NewLine;
            if (!IsHandleCreated) return;
            try {
                BeginInvoke(new Action(() => {
                    _log.AppendText(line);
                    if (_log.TextLength > 50000)
                        _log.Text = _log.Text.Substring(_log.TextLength - 40000);
                }));
            } catch { }
        }
    }
}
