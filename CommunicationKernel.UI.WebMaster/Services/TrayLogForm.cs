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
    private readonly AppLogStore _logs;
    private readonly Action _openWebLog;
    private readonly TextBox _box;
    private readonly System.Windows.Forms.Timer _timer;
    private volatile bool _dirty = true;

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

    private void OnLogChanged() => _dirty = true;

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
