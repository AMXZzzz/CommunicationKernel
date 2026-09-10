// -----------------------------------------------------------------------------
// 文件: Services/Shell/TrayLogForm.cs
// 层级: UI 层 — WebMaster 托盘
// 作用: 托盘「查看日志」弹出的小窗口，订阅 AppLogStore，不必开浏览器。
//
// 为什么不是 TextBox:
//   TextBox 整块只有一个前景色，INF / WRN / ERR 全都是同一片白字。
//   而这个窗口最主要的用途，就是在几百行流水里一眼找出那几条红的——
//   没有颜色，等于把唯一的线索抹掉了。改用 RichTextBox 按级别上色。
//
// 配色与 Web 端的日志页对齐（wwwroot/css/base.css 里的 .log-level 一族），
// 同一套东西在两处长得一样，不必重新认。
// -----------------------------------------------------------------------------

using System.Drawing;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Windows.Forms;

namespace CommunicationKernel.UI.WebMaster.Services;

/// <summary>深色日志窗。关窗口只是藏起来，进程仍在托盘。</summary>
[SupportedOSPlatform("windows")]
internal sealed class TrayLogForm : Form
{
    // =========================================================================
    // 配色 —— 取自 Web 端 base.css 的同名令牌
    // =========================================================================

    /// <summary>窗口底色，对应 --bg。</summary>
    private static readonly Color Bg = Color.FromArgb(0x16, 0x16, 0x16);

    /// <summary>工具条与状态条底色，对应 --surface。</summary>
    private static readonly Color Surface = Color.FromArgb(0x1a, 0x1b, 0x1e);

    /// <summary>按钮底色，对应 --surface3。</summary>
    private static readonly Color Surface3 = Color.FromArgb(0x26, 0x28, 0x2c);

    /// <summary>边框色，对应 --border。</summary>
    private static readonly Color BorderCol = Color.FromArgb(0x33, 0x36, 0x3b);

    /// <summary>正文色，对应 --text。</summary>
    private static readonly Color Fg = Color.FromArgb(0xe6, 0xe7, 0xe9);

    /// <summary>次要文字，对应 --text-muted。</summary>
    private static readonly Color Muted = Color.FromArgb(0xa7, 0xab, 0xb1);

    /// <summary>弱化文字（时间戳、DBG），对应 --text-dim。</summary>
    private static readonly Color Dim = Color.FromArgb(0x8b, 0x90, 0x96);

    /// <summary>INF 级别色，对应 --accent。</summary>
    private static readonly Color Info = Color.FromArgb(0x3d, 0x8b, 0xfd);

    /// <summary>WRN 级别色，对应 --warning-ink。</summary>
    private static readonly Color Warn = Color.FromArgb(0xe3, 0xb3, 0x41);

    /// <summary>ERR 级别色，对应 --danger-ink。</summary>
    private static readonly Color Err = Color.FromArgb(0xff, 0x7b, 0x72);

    // =========================================================================
    // 状态
    // =========================================================================

    /// <summary>日志来源。本窗只读它，不自己缓存历史。</summary>
    private readonly AppLogStore _logs;

    /// <summary>「在浏览器打开」的动作，由调用方注入——本窗不认识 IServer 或 URL 规则。</summary>
    private readonly Action _openWebLog;

    /// <summary>日志正文框。用 RichTextBox 而不是 TextBox，理由见文件头。</summary>
    private readonly RichTextBox _box;

    /// <summary>底部状态条：条数与跟随状态。</summary>
    private readonly Label _status;

    /// <summary>自动换行开关。</summary>
    private readonly Button _wrapBtn;

    /// <summary>
    /// 刷新节拍器。
    /// </summary>
    /// <remarks>
    /// 不在 Changed 事件里直接刷：日志可能每秒来几十条，逐条动文本框
    /// 会让窗口卡死。改成事件只置脏标记，由本计时器按固定节拍合并刷新。
    /// </remarks>
    private readonly System.Windows.Forms.Timer _timer;

    /// <summary>
    /// 自上次刷新以来是否有新日志。volatile：后台线程写、UI 线程读。
    /// </summary>
    private volatile bool _dirty = true;

    /// <summary>
    /// 已经画到哪一条了。
    /// </summary>
    /// <remarks>
    /// 按序号而不是条数记账：缓冲是环形的，满了之后每来一条就丢一条，
    /// 条数纹丝不动而内容已经换了——按条数追加会静默漏掉整段日志。
    /// </remarks>
    private long _renderedSeq;

    /// <summary>正文框里当前有多少行，用于判断何时该整体重画。</summary>
    private int _lines;

    /// <summary>
    /// 正文框里最多留多少行。
    /// </summary>
    /// <remarks>
    /// 超过就丢掉旧的、从最近 <see cref="KeepLines"/> 条重画一次。
    /// RichTextBox 逐行删首行的代价比整体重设还高，攒够了一次性来更划算。
    /// 要翻更早的记录去浏览器的日志页，那边有 2000 条缓冲和筛选。
    /// </remarks>
    private const int MaxLines = 1200;

    /// <summary>整体重画时保留的条数。</summary>
    private const int KeepLines = 500;

    /// <summary>时间戳列宽（<c>HH:mm:ss.fff</c> 加一个空格）。</summary>
    private const int TimeCols = 13;

    /// <summary>级别列宽（三字母加一个空格）。</summary>
    private const int LevelCols = 4;

    /// <summary>分类列宽。超出的截断加省略号，保证正文列不会被某个长名字顶歪。</summary>
    private const int CategoryCols = 19;

    /// <summary>正文列距左边的像素数，折行时续行缩进到这里。</summary>
    /// <remarks>
    /// 按字体实测而不是写死像素：等宽字在不同 DPI 与字号下宽度不同，
    /// 写死的话续行会和正文列错开一截，比不缩进更难看。
    /// </remarks>
    private readonly int _msgIndent;

    // =========================================================================
    // 构造
    // =========================================================================

    /// <param name="logs">日志缓冲。</param>
    /// <param name="openWebLog">
    /// 点「在浏览器打开」时执行的动作。由调用方注入而非本窗自己拼 URL——
    /// 端口是运行期才确定的，本窗不该知道 Kestrel 绑到了哪里。
    /// </param>
    public TrayLogForm(AppLogStore logs, Action openWebLog)
    {
        _logs = logs;
        _openWebLog = openWebLog;

        Text = "通讯日志 — CommunicationKernel Web";
        // 日志正文常带完整路径，820 宽时几乎每行都要截断
        Width = 1040;
        Height = 620;
        StartPosition = FormStartPosition.CenterScreen;
        MinimumSize = new Size(560, 320);
        BackColor = Bg;
        ForeColor = Fg;
        Font = new Font("Segoe UI", 9f);

        _box = new RichTextBox
        {
            Dock = DockStyle.Fill,
            ReadOnly = true,
            BorderStyle = BorderStyle.None,
            BackColor = Bg,
            ForeColor = Fg,
            Font = new Font("Consolas", 9.5f),
            // 关掉自动识别链接：日志里满是路径与 IP，识别一遍纯属浪费，
            // 而且会把它们画成刺眼的蓝色下划线
            DetectUrls = false,
            WordWrap = true,
            ScrollBars = RichTextBoxScrollBars.Both,
        };

        // 正文列的起点：前三列都是定宽，等宽字下直接按字符数量出来。
        // 用 NoPadding，否则 MeasureText 会替你多加一圈边距，缩进就偏了
        _msgIndent = TextRenderer.MeasureText(
            new string('0', TimeCols + LevelCols + CategoryCols),
            _box.Font,
            new Size(int.MaxValue, int.MaxValue),
            TextFormatFlags.NoPadding).Width;

        _wrapBtn = MakeButton("自动换行 ✓", (_, _) => ToggleWrap());

        FlowLayoutPanel bar = new()
        {
            Dock = DockStyle.Top,
            Height = 42,
            Padding = new Padding(10, 7, 10, 5),
            BackColor = Surface,
        };
        bar.Controls.Add(MakeButton("清空", (_, _) => _logs.Clear()));
        bar.Controls.Add(MakeButton("在浏览器打开", (_, _) => _openWebLog()));
        bar.Controls.Add(_wrapBtn);
        bar.Controls.Add(MakeButton("关闭", (_, _) => Hide()));

        _status = new Label
        {
            Dock = DockStyle.Bottom,
            Height = 24,
            BackColor = Surface,
            ForeColor = Dim,
            Padding = new Padding(12, 5, 12, 0),
            Text = string.Empty,
        };

        // 添加顺序即停靠优先级：先加的贴外圈。Fill 必须最先加，
        // 否则它会占满剩余空间后把后加的挤出可视区
        Controls.Add(_box);
        Controls.Add(_status);
        Controls.Add(bar);

        _timer = new System.Windows.Forms.Timer { Interval = 400 };
        _timer.Tick += (_, _) =>
        {
            if (!_dirty) return;
            _dirty = false;
            Refresh_Incremental();
        };

        _logs.Changed += OnLogChanged;
        FormClosing += (_, e) =>
        {
            if (e.CloseReason == CloseReason.UserClosing)
            {
                e.Cancel = true;
                Hide();
            }
        };
        Shown += (_, _) =>
        {
            _timer.Start();
            RebuildAll();
        };
    }

    /// <summary>
    /// 停表并退订日志事件。
    /// </summary>
    /// <remarks>
    /// 退订不能省：AppLogStore 是进程内单例，生命周期远长于本窗口。
    /// 漏退会让已释放的窗体继续收事件，且每开一次日志窗就泄漏一个。
    /// </remarks>
    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _timer.Stop();
            _timer.Dispose();
            _logs.Changed -= OnLogChanged;
        }
        base.Dispose(disposing);
    }

    /// <summary>只置脏标记，不直接刷新——刷新由计时器合并执行，见 <see cref="_timer"/>。</summary>
    private void OnLogChanged() => _dirty = true;

    // =========================================================================
    // 渲染
    // =========================================================================

    /// <summary>
    /// 追加自上次以来的新日志。
    /// </summary>
    /// <remarks>
    /// 增量追加而不是每次重建整篇，有两个理由：
    /// <list type="bullet">
    ///   <item>重建几百行带颜色的文本每 400ms 一次，窗口会肉眼可见地卡；</item>
    ///   <item>更要紧的是<b>滚动位置</b>。重建会把视口甩回某处，
    ///         人正盯着一条可疑记录读到一半就被冲走了。</item>
    /// </list>
    /// 被清空（序号回退）或行数超限时才整体重画。
    /// </remarks>
    private void Refresh_Incremental()
    {
        IReadOnlyList<AppLogEntry> all = _logs.Snapshot();

        // 清空之后新一轮的序号仍在递增，但快照里最后一条的序号会小于已画到的位置
        if (all.Count == 0 || all[^1].Seq < _renderedSeq)
        {
            RebuildAll();
            return;
        }

        if (_lines > MaxLines)
        {
            RebuildAll();
            return;
        }

        int from = 0;
        while (from < all.Count && all[from].Seq <= _renderedSeq) from++;
        if (from >= all.Count)
        {
            UpdateStatus(all.Count);
            return;
        }

        bool follow = IsAtBottom();
        SetRedraw(false);
        try
        {
            for (int i = from; i < all.Count; i++) AppendEntry(all[i]);
        }
        finally
        {
            SetRedraw(true);
        }

        _renderedSeq = all[^1].Seq;
        if (follow) ScrollToEnd();
        UpdateStatus(all.Count);
    }

    /// <summary>从最近 <see cref="KeepLines"/> 条整体重画。</summary>
    private void RebuildAll()
    {
        IReadOnlyList<AppLogEntry> all = _logs.Snapshot();
        int start = all.Count > KeepLines ? all.Count - KeepLines : 0;

        SetRedraw(false);
        try
        {
            _box.Clear();
            _lines = 0;
            for (int i = start; i < all.Count; i++) AppendEntry(all[i]);
        }
        finally
        {
            SetRedraw(true);
        }

        _renderedSeq = all.Count > 0 ? all[^1].Seq : 0;
        ScrollToEnd();
        UpdateStatus(all.Count);
    }

    /// <summary>
    /// 画一条日志：时间 / 级别 / 分类 / 正文，四段各自上色。
    /// </summary>
    /// <remarks>
    /// 级别与分类都定宽，等宽字体下上下行天然对齐，扫读时级别列成一条直线。
    /// 分类原来根本没画出来——只有时间、级别、正文，
    /// 于是「这条是谁记的」全靠正文自己带前缀，不带的就无从判断。
    /// <para>
    /// 只有 WRN / ERR 的正文跟着级别上色。INF 是绝大多数，给它上色等于没上色；
    /// DBG 整条压暗，它是给排查用的细节，不该和正常流水抢注意力。
    /// </para>
    /// </remarks>
    private void AppendEntry(AppLogEntry e)
    {
        Color lvl = LevelColor(e.Level);

        // 折行后的续行缩进到正文列。不缩的话续行顶到最左边，
        // 和一条新记录的开头长得一模一样——而日志里满是长到必然折行的路径，
        // 一屏下来根本数不清到底有几条。
        _box.SelectionHangingIndent = _msgIndent;

        Append(e.Timestamp.ToString("HH:mm:ss.fff") + " ", Dim);
        Append(e.LevelText.PadRight(4), lvl);

        // 分类为空时也把这一列的位置占住：不占的话正文会前移 19 格，
        // 而悬挂缩进是固定的，续行反倒比首行还靠右
        Append(Clip(e.Category, CategoryCols - 1).PadRight(CategoryCols), Muted);

        Color body = e.LevelText switch
        {
            "ERR" => Err,
            "WRN" => Warn,
            "DBG" => Dim,
            _ => Fg,
        };
        Append(e.Message + Environment.NewLine, body);

        // 正文自带换行时一条会占多行，按实际换行数记账，否则限长判断会失准
        _lines += 1 + CountNewLines(e.Message);
    }

    /// <summary>往正文框尾部追加一段带色文本。</summary>
    private void Append(string text, Color color)
    {
        _box.SelectionStart = _box.TextLength;
        _box.SelectionLength = 0;
        _box.SelectionColor = color;
        _box.AppendText(text);
    }

    /// <summary>级别对应的颜色。</summary>
    private static Color LevelColor(LogLevel level) => level switch
    {
        LogLevel.Warning => Warn,
        LogLevel.Error or LogLevel.Critical => Err,
        LogLevel.Debug or LogLevel.Trace => Dim,
        _ => Info,
    };

    /// <summary>把过长的分类名截断，保证那一列不会被某个长名字撑歪。</summary>
    private static string Clip(string s, int max) =>
        s.Length <= max ? s : s[..(max - 1)] + "…";

    /// <summary>数一段文本里有几个换行。</summary>
    private static int CountNewLines(string s)
    {
        int n = 0;
        foreach (char c in s)
            if (c == '\n') n++;
        return n;
    }

    /// <summary>刷新底部状态条。</summary>
    private void UpdateStatus(int total)
    {
        string wrap = _box.WordWrap ? "自动换行" : "不换行";
        string follow = IsAtBottom() ? "跟随中" : "已暂停跟随（滚到底恢复）";

        _status.Text = $"缓冲 {total} / {AppLogStore.Capacity} 条 · 窗内 {_lines} 行 · {wrap} · {follow}";
    }

    /// <summary>切换自动换行。</summary>
    /// <remarks>
    /// 日志正文常带完整路径，不换行时右边一大截看不见；
    /// 但换行会打乱前三列的对齐，扫「哪几条是红的」时反而更费劲。
    /// 两种都有人要，做成开关而不是替谁做主。
    /// </remarks>
    private void ToggleWrap()
    {
        _box.WordWrap = !_box.WordWrap;
        _wrapBtn.Text = _box.WordWrap ? "自动换行 ✓" : "自动换行";
        ScrollToEnd();
        UpdateStatus(_logs.Snapshot().Count);
    }

    /// <summary>视口是不是已经贴着底部。</summary>
    /// <remarks>
    /// 只有贴底时才自动跟随新日志。人往上翻去读某一条时，
    /// 新日志把视口拽回底部是这类窗口最恼人的行为。
    /// </remarks>
    private bool IsAtBottom()
    {
        if (_box.TextLength == 0) return true;

        int lastVisible = _box.GetCharIndexFromPosition(
            new Point(2, _box.ClientSize.Height - 2));

        // 留一行余量：贴底时最后一行可能只露出一半，严格相等会判成"没贴底"
        return lastVisible >= _box.TextLength - 2;
    }

    /// <summary>滚到底部。</summary>
    private void ScrollToEnd()
    {
        _box.SelectionStart = _box.TextLength;
        _box.SelectionLength = 0;
        _box.ScrollToCaret();
    }

    // =========================================================================
    // 重绘抑制
    // =========================================================================

    /// <summary>Win32 <c>WM_SETREDRAW</c>。</summary>
    private const int WmSetRedraw = 0x000B;

    [DllImport("user32.dll", CharSet = CharSet.Auto)]
    private static extern IntPtr SendMessage(IntPtr hWnd, int msg, IntPtr wParam, IntPtr lParam);

    /// <summary>
    /// 批量追加期间关掉重绘。
    /// </summary>
    /// <remarks>
    /// 每 <c>AppendText</c> 一段就重绘一次，一次刷新几十段时窗口会明显闪。
    /// RichTextBox 没有 BeginUpdate，只能发 <c>WM_SETREDRAW</c>；
    /// 重新打开后必须手动 <c>Invalidate</c>，否则新内容不会画出来。
    /// </remarks>
    private void SetRedraw(bool on)
    {
        // 判的是正文框自己的句柄：窗体句柄已建、子控件还没建的时候取 _box.Handle
        // 会强制把它建出来，那正是这里要避开的
        if (!_box.IsHandleCreated) return;

        SendMessage(_box.Handle, WmSetRedraw, on ? 1 : 0, IntPtr.Zero);
        if (on)
        {
            _box.Invalidate();
            _box.Update();
        }
    }

    /// <summary>造一个与深色背景配套的扁平按钮。</summary>
    /// <remarks>WinForms 按钮默认是系统浅色样式，放在深色窗上会白得刺眼。</remarks>
    private static Button MakeButton(string text, EventHandler onClick)
    {
        Button b = new()
        {
            Text = text,
            AutoSize = true,
            FlatStyle = FlatStyle.Flat,
            BackColor = Surface3,
            ForeColor = Fg,
            Margin = new Padding(0, 0, 8, 0),
            Padding = new Padding(10, 3, 10, 3),
            Cursor = Cursors.Hand,
        };
        b.FlatAppearance.BorderColor = BorderCol;
        b.FlatAppearance.MouseOverBackColor = Color.FromArgb(0x32, 0x35, 0x3a);
        b.Click += onClick;
        return b;
    }
}
