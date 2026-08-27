// -----------------------------------------------------------------------------
// 文件: Services/TrayLogForm.cs
// 层级: UI 层 — WebMaster 托盘
// 作用: 托盘「查看日志」弹出的小窗口，订阅 AppLogStore，不必开浏览器。
// -----------------------------------------------------------------------------

using System.Drawing;
using System.Runtime.Versioning;
using System.Text;
using System.Windows.Forms;

namespace CommunicationKernel.UI.WebMaster.Services;

/// <summary>深色日志窗。关窗口只是藏起来，进程仍在托盘。</summary>
[SupportedOSPlatform("windows")]
internal sealed class TrayLogForm : Form
{
    /// <summary>日志来源。本窗只读它，不自己缓存历史。</summary>
    private readonly AppLogStore _logs;
    /// <summary>「在浏览器打开」的动作，由调用方注入——本窗不认识 IServer 或 URL 规则。</summary>
    private readonly Action _openWebLog;
    /// <summary>日志正文框，只读多行。</summary>
    private readonly TextBox _box;
    /// <summary>
    /// 刷新节拍器。
    /// </summary>
    /// <remarks>
    /// 不在 Changed 事件里直接刷：日志可能每秒来几十条，逐条重建整个文本框
    /// 会让窗口卡死。改成事件只置脏标记，由本计时器按固定节拍合并刷新。
    /// </remarks>
    private readonly System.Windows.Forms.Timer _timer;
    /// <summary>
    /// 自上次刷新以来是否有新日志。volatile：后台线程写、UI 线程读。
    /// </summary>
    private volatile bool _dirty = true;

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
        Width = 820;
        Height = 480;
        StartPosition = FormStartPosition.CenterScreen;
        MinimumSize = new Size(480, 280);
        BackColor = Color.FromArgb(11, 18, 32);
        ForeColor = Color.FromArgb(226, 232, 240);
        Font = new Font("Segoe UI", 9f);

        Button clear = MakeButton("清空", (_, _) => _logs.Clear());
        Button web = MakeButton("在浏览器打开", (_, _) => _openWebLog());
        Button close = MakeButton("关闭", (_, _) => Hide());

        FlowLayoutPanel bar = new()
        {
            Dock = DockStyle.Top,
            Height = 40,
            Padding = new Padding(8, 6, 8, 4),
            BackColor = Color.FromArgb(15, 23, 42),
        };
        bar.Controls.Add(clear);
        bar.Controls.Add(web);
        bar.Controls.Add(close);

        _box = new TextBox
        {
            Dock = DockStyle.Fill,
            Multiline = true,
            ReadOnly = true,
            ScrollBars = ScrollBars.Both,
            WordWrap = false,
            BorderStyle = BorderStyle.None,
            BackColor = Color.FromArgb(11, 18, 32),
            ForeColor = Color.FromArgb(226, 232, 240),
            Font = new Font("Consolas", 9f),
        };

        Controls.Add(_box);
        Controls.Add(bar);

        _timer = new System.Windows.Forms.Timer { Interval = 400 };
        _timer.Tick += (_, _) =>
        {
            if (!_dirty) return;
            _dirty = false;
            Reload();
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
            _dirty = true;
            _timer.Start();
            Reload();
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

    /// <summary>
    /// 重建日志正文并滚到底部。
    /// </summary>
    /// <remarks>
    /// 只渲染最近 400 条：缓冲上限是 2000 条，全量拼进 TextBox 在每次刷新时
    /// 都要重排整个文本，窗口会明显卡顿。要看更早的记录去浏览器的日志页。
    /// StringBuilder 预分配按每条约 80 字符估，省掉反复扩容。
    /// </remarks>
    private void Reload()
    {
        IReadOnlyList<AppLogEntry> all = _logs.Snapshot();
        int start = all.Count > 400 ? all.Count - 400 : 0;
        StringBuilder sb = new(Math.Min(all.Count, 400) * 80);
        for (int i = start; i < all.Count; i++)
        {
            AppLogEntry e = all[i];
            sb.Append(e.Timestamp.ToString("HH:mm:ss.fff"))
              .Append(' ')
              .Append(e.LevelText)
              .Append(' ')
              .Append(e.Message)
              .AppendLine();
        }
        _box.Text = sb.ToString();
        _box.SelectionStart = _box.TextLength;
        _box.ScrollToCaret();
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
            BackColor = Color.FromArgb(30, 41, 59),
            ForeColor = Color.FromArgb(226, 232, 240),
            Margin = new Padding(0, 0, 8, 0),
            Padding = new Padding(8, 2, 8, 2),
        };
        b.FlatAppearance.BorderColor = Color.FromArgb(51, 65, 85);
        b.Click += onClick;
        return b;
    }
}
