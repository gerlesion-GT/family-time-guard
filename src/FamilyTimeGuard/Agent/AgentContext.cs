using System.Drawing.Drawing2D;
using System.Text;
using FamilyTimeGuard.Core;
using FamilyTimeGuard.Service;

namespace FamilyTimeGuard.Agent;

/// <summary>
/// The tray helper. Runs as the child, shows time left and warnings, and reports which window is in front
/// (so the Guard can tell when YouTube or a web game is being used).
/// </summary>
internal sealed class AgentContext : ApplicationContext
{
    private readonly NotifyIcon _tray;
    private readonly System.Windows.Forms.Timer _timer;
    private readonly Control _ui = new();
    private readonly ToastStack _toasts = new();
    private BedtimeForm? _bedtimeForm;
    private readonly ToolStripMenuItem _askItem;
    private bool _busy;
    private AgentResponse? _last;
    private int _failures;

    private static readonly Icon GreenIcon = MakeIcon(Color.FromArgb(46, 160, 67));
    private static readonly Icon AmberIcon = MakeIcon(Color.FromArgb(230, 160, 20));
    private static readonly Icon RedIcon = MakeIcon(Color.FromArgb(210, 60, 50));
    private static readonly Icon GreyIcon = MakeIcon(Color.Gray);

    public AgentContext()
    {
        _ui.CreateControl();

        var menu = new ContextMenuStrip();
        menu.Items.Add("My time today", null, (_, _) => ShowStatus());
        _askItem = new ToolStripMenuItem("Ask for more time...", null, (_, _) => ShowAskForTime()) { Visible = false };
        menu.Items.Add(_askItem);
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("Parent options...", null, (_, _) => ShowParentOptions());

        _tray = new NotifyIcon
        {
            Icon = GreyIcon,
            Text = "Family Time Guard",
            Visible = true,
            ContextMenuStrip = menu,
        };
        _tray.MouseClick += (_, e) => { if (e.Button == MouseButtons.Left) ShowStatus(); };

        _timer = new System.Windows.Forms.Timer { Interval = 2000 };
        _timer.Tick += async (_, _) => await TickAsync();
        _timer.Start();
        _ = TickAsync();
    }

    private async Task TickAsync()
    {
        if (_busy) return;
        _busy = true;
        try
        {
            var fg = ForegroundWindow.Get();
            var request = new AgentRequest { Type = "report", ForegroundTitle = fg.Title, ForegroundProcess = fg.ProcessName, IdleSeconds = Native.IdleSeconds() };
            var response = await Task.Run(() => PipeClientCall.Send(request));
            if (_last is null || _failures >= 3) AgentLog.Write($"Connected to the Guard (rules active: {response.Enforced}).");
            _failures = 0;
            Apply(response, fg);
        }
        catch (Exception ex)
        {
            if (++_failures == 3) AgentLog.Write("Cannot reach the Guard service: " + ex.Message);
            if (_failures >= 3)
            {
                _tray.Icon = GreyIcon;
                _tray.Text = "Family Time Guard (connecting...)";
            }
        }
        finally { _busy = false; }
    }

    private void Apply(AgentResponse r, ForegroundInfo fg)
    {
        _last = r;
        if (_dashboard is { IsDisposed: false }) _dashboard.UpdateFrom(r);
        if (!r.Enforced)
        {
            _tray.Visible = false;
            return;
        }
        _tray.Visible = true;
        _askItem.Visible = r.RemoteEnabled;

        var s = r.Status;
        if (s is not null)
        {
            var low = (s.GamesLeftSeconds is >= 0 and < 600) || (s.YouTubeLeftSeconds is >= 0 and < 600);
            var none = s.GamesLeftSeconds == 0 && s.YouTubeLeftSeconds == 0;
            _tray.Icon = s.Paused ? GreenIcon : s.Bedtime || none ? RedIcon : low ? AmberIcon : GreenIcon;
            var text = (s.ScreenLeftSeconds >= 0 ? $"Screen {Fmt(s.ScreenLeftSeconds)} · " : "") +
                       $"Games {Fmt(s.GamesLeftSeconds)} · YouTube {Fmt(s.YouTubeLeftSeconds)}";
            if (s.NextBedtime is not null) text += $" · Bed {s.NextBedtime}";
            if (s.Paused) text = "Rules paused by parent";
            _tray.Text = text.Length > 120 ? text[..120] : text;
        }

        foreach (var n in r.Notices)
        {
            if (n.Kind == Notice.Big) BigNotice.Show(n.Title, n.Message);
            else _toasts.Show(n.Title, n.Message);
        }

        if (r.CloseForegroundTab) ForegroundWindow.CloseTabIfStillInFront(fg);

        if (r.LogoffInSeconds is { } secs)
        {
            if (_bedtimeForm is null || _bedtimeForm.IsDisposed)
            {
                _bedtimeForm = new BedtimeForm(r.ChildName ?? "", s?.UnlockAt, r.LockTitle, r.LockMessage,
                    canAsk: r.RemoteEnabled && s?.ScreenTimeUp == true && r.LockTitle == "Screen time is over");
                _bedtimeForm.Show();
            }
            _bedtimeForm.UpdateCountdown(secs);
        }
        else if (_bedtimeForm is { IsDisposed: false })
        {
            _bedtimeForm.ForceClose();
            _bedtimeForm = null;
        }
    }

    private DashboardForm? _dashboard;

    /// <summary>Opens the dashboard (or brings it to the front).</summary>
    private DashboardForm ShowDashboard()
    {
        if (_dashboard is null || _dashboard.IsDisposed)
        {
            _dashboard = new DashboardForm();
            _dashboard.UpdateFrom(_last);
            _dashboard.Show();
        }
        else
        {
            if (_dashboard.WindowState == FormWindowState.Minimized) _dashboard.WindowState = FormWindowState.Normal;
            _dashboard.UpdateFrom(_last);
        }
        _dashboard.Activate();
        return _dashboard;
    }

    private void ShowStatus() => ShowDashboard();

    private void ShowAskForTime()
    {
        using var form = new AskForTimeForm();
        form.ShowDialog();
    }

    private void ShowParentOptions() => ShowDashboard().PromptUnlock();

    private static string Fmt(int seconds)
    {
        if (seconds < 0) return "unlimited";
        var m = (int)Math.Ceiling(seconds / 60.0);
        return m >= 60 ? $"{m / 60}h {m % 60:00}m" : $"{m} min";
    }

    private static string Of(int budget) => budget <= 0 ? "" : $"  (of {Fmt(budget)})";

    private static Icon MakeIcon(Color color)
    {
        using var bmp = new Bitmap(32, 32);
        using (var g = Graphics.FromImage(bmp))
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.Clear(Color.Transparent);
            using var brush = new SolidBrush(color);
            g.FillEllipse(brush, 2, 2, 28, 28);
            using var pen = new Pen(Color.White, 3);
            g.DrawLine(pen, 16, 16, 16, 7);   // clock hands
            g.DrawLine(pen, 16, 16, 23, 16);
        }
        return Icon.FromHandle(bmp.GetHicon());
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _timer.Dispose();
            _tray.Visible = false;
            _tray.Dispose();
            _ui.Dispose();
        }
        base.Dispose(disposing);
    }
}

internal sealed record ForegroundInfo(IntPtr Handle, string? Title, string? ProcessName);

internal static class ForegroundWindow
{
    public static ForegroundInfo Get()
    {
        var hwnd = Native.GetForegroundWindow();
        if (hwnd == IntPtr.Zero) return new ForegroundInfo(IntPtr.Zero, null, null);
        var len = Native.GetWindowTextLengthW(hwnd);
        var sb = new StringBuilder(Math.Clamp(len + 1, 1, 1024));
        Native.GetWindowTextW(hwnd, sb, sb.Capacity);
        Native.GetWindowThreadProcessId(hwnd, out var pid);
        var path = pid > 0 ? Native.GetProcessPath(pid) : null;
        return new ForegroundInfo(hwnd, sb.ToString(), path is null ? null : Path.GetFileName(path));
    }

    /// <summary>Closes the current browser tab (Ctrl+W), only if the same window and title are still in front.</summary>
    public static void CloseTabIfStillInFront(ForegroundInfo before)
    {
        var now = Get();
        if (now.Handle != before.Handle || now.Title != before.Title || string.IsNullOrEmpty(now.Title)) return;
        Native.keybd_event(Native.VK_CONTROL, 0, 0, UIntPtr.Zero);
        Native.keybd_event(Native.VK_W, 0, 0, UIntPtr.Zero);
        Native.keybd_event(Native.VK_W, 0, Native.KEYEVENTF_KEYUP, UIntPtr.Zero);
        Native.keybd_event(Native.VK_CONTROL, 0, Native.KEYEVENTF_KEYUP, UIntPtr.Zero);
    }
}

internal static class AgentLog
{
    public static void Write(string message)
    {
        try
        {
            File.AppendAllText(Path.Combine(Path.GetTempPath(), "FamilyTimeGuard-helper.log"),
                $"{DateTime.Now:yyyy-MM-dd HH:mm:ss} {message}{Environment.NewLine}");
        }
        catch (Exception) { }
    }
}
