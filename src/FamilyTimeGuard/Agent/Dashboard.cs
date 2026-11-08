using System.Drawing.Drawing2D;
using FamilyTimeGuard.Core;

namespace FamilyTimeGuard.Agent;

/// <summary>
/// The window that opens from the clock icon: time left for screen time, games and YouTube at a glance,
/// plus parent buttons (behind the PIN) to add time or pause the rules.
/// </summary>
internal sealed class DashboardForm : Form
{
    private static readonly TimeSpan UnlockFor = TimeSpan.FromMinutes(5);

    private readonly Label _title = new() { AutoSize = true, Font = new Font("Segoe UI Semibold", 16f), Margin = new Padding(0, 0, 0, 2) };
    private readonly Label _subtitle = new() { AutoSize = true, ForeColor = Color.DimGray, Margin = new Padding(0, 0, 0, 12) };
    private readonly Label _message = new() { AutoSize = true, MaximumSize = new Size(560, 0), Margin = new Padding(0, 8, 0, 0) };
    private readonly Button _unlock = new() { AutoSize = true, Text = "Unlock parent controls", Padding = new Padding(8, 4, 8, 4) };
    private readonly Button _pause = new() { AutoSize = true, Text = "Pause 1 hour", Enabled = false, Padding = new Padding(8, 4, 8, 4) };
    private readonly Button _resume = new() { AutoSize = true, Text = "Resume rules", Enabled = false, Padding = new Padding(8, 4, 8, 4) };
    private readonly Button _ask = new() { AutoSize = true, Text = "Ask for more time", Visible = false, Padding = new Padding(8, 4, 8, 4) };
    private readonly System.Windows.Forms.Timer _relock = new();
    private readonly List<Button> _parentButtons = [];
    private readonly Row _screen, _games, _youtube;

    private string? _pin;
    private DateTime _unlockedUntil;

    public DashboardForm()
    {
        Text = "Family Time Guard";
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = false;
        StartPosition = FormStartPosition.CenterScreen;
        AutoScaleMode = AutoScaleMode.Dpi;
        AutoScaleDimensions = new SizeF(96f, 96f);
        AutoSize = true;
        AutoSizeMode = AutoSizeMode.GrowAndShrink;
        Font = new Font("Segoe UI", 10f);
        BackColor = Color.White;
        Padding = new Padding(20);
        ShowInTaskbar = true;

        var root = new TableLayoutPanel { AutoSize = true, ColumnCount = 1, Dock = DockStyle.Fill };
        root.Controls.Add(_title);
        root.Controls.Add(_subtitle);

        _screen = new Row(this, "Screen time (total)", "screen", Color.FromArgb(52, 101, 196));
        _games = new Row(this, "Games", "games", Color.FromArgb(46, 160, 67));
        _youtube = new Row(this, "YouTube", "youtube", Color.FromArgb(200, 40, 40));
        root.Controls.Add(_screen.Panel);
        root.Controls.Add(_games.Panel);
        root.Controls.Add(_youtube.Panel);

        var bottom = new FlowLayoutPanel { AutoSize = true, Margin = new Padding(0, 14, 0, 0), WrapContents = false };
        bottom.Controls.AddRange([_unlock, _pause, _resume, _ask]);
        root.Controls.Add(bottom);
        root.Controls.Add(_message);
        Controls.Add(root);

        _unlock.Click += (_, _) => ToggleUnlock();
        _pause.Click += async (_, _) => await DoParentAction("pause", 60, _pause);
        _resume.Click += async (_, _) => await DoParentAction("resume", 0, _resume);
        _ask.Click += (_, _) => { using var f = new AskForTimeForm(); f.ShowDialog(this); };
        _parentButtons.AddRange([_pause, _resume]);

        _relock.Interval = 1000;
        _relock.Tick += (_, _) =>
        {
            if (_pin is null) return;
            if (DateTime.UtcNow >= _unlockedUntil) Lock("Parent controls locked again.");
            else _unlock.Text = $"Lock parent controls ({(int)(_unlockedUntil - DateTime.UtcNow).TotalMinutes + 1} min)";
        };
        _relock.Start();
        FormClosed += (_, _) => { _relock.Dispose(); _pin = null; };
    }

    /// <summary>Open with the PIN prompt straight away (from "Parent options...").</summary>
    public void PromptUnlock()
    {
        if (_pin is null) ToggleUnlock();
    }

    public void UpdateFrom(AgentResponse? r)
    {
        if (IsDisposed) return;
        var s = r?.Status;
        _title.Text = string.IsNullOrEmpty(r?.ChildName) ? "My time today" : $"Hi {r.ChildName}! Here's your time today";
        _ask.Visible = r?.RemoteEnabled == true;
        if (s is null)
        {
            _subtitle.Text = "Connecting to Family Time Guard...";
            return;
        }

        var parts = new List<string> { DateTime.Now.ToString("dddd d MMMM") };
        if (s.ParentLocked) parts.Add("locked by a parent");
        else if (s.ScreenTimeUp) parts.Add("screen time is used up");
        else if (s.Bedtime) parts.Add($"bedtime – unlocks at {s.UnlockAt}");
        else if (s.NextBedtime is not null) parts.Add($"bedtime tonight {s.NextBedtime}");
        if (s.Paused) parts.Add($"rules paused{(s.PausedUntil is null ? "" : " until " + s.PausedUntil)}");
        else if (s.CountingGames) parts.Add("counting game time now");
        else if (s.CountingYouTube) parts.Add("counting YouTube time now");
        _subtitle.Text = string.Join("  ·  ", parts);

        _screen.Update(s.ScreenUsedSeconds, s.ScreenBudgetSeconds, s.ScreenLeftSeconds, s.BonusScreenMinutes,
            noLimitText: "no daily limit");
        _games.Update(s.GamesUsedSeconds, s.GamesBudgetSeconds, s.GamesLeftSeconds, s.BonusGamesMinutes);
        _youtube.Update(s.YouTubeUsedSeconds, s.YouTubeBudgetSeconds, s.YouTubeLeftSeconds, s.BonusYouTubeMinutes);
    }

    private void ToggleUnlock()
    {
        if (_pin is not null) { Lock("Parent controls locked."); return; }
        using var pinForm = new PinForm();
        if (pinForm.ShowDialog(this) != DialogResult.OK) return;
        _pin = pinForm.Pin;
        _unlockedUntil = DateTime.UtcNow + UnlockFor;
        foreach (var b in _parentButtons) b.Enabled = true;
        _unlock.Text = "Lock parent controls (5 min)";
        ShowMessage("Parent controls unlocked for 5 minutes.", ok: true);
    }

    private void Lock(string message)
    {
        _pin = null;
        foreach (var b in _parentButtons) b.Enabled = false;
        _unlock.Text = "Unlock parent controls";
        ShowMessage(message, ok: true);
    }

    private async Task DoParentAction(string action, int minutes, Button source)
    {
        if (_pin is null) { ToggleUnlock(); if (_pin is null) return; }
        source.Enabled = false;
        var r = await ParentActions.Send(action, minutes, _pin!);
        source.Enabled = _pin is not null;
        ShowMessage(r.Ok ? r.Message ?? "Done." : r.Error ?? "Something went wrong.", r.Ok);
        if (!r.Ok && r.Error?.Contains("PIN", StringComparison.OrdinalIgnoreCase) == true) Lock(r.Error);
        else _unlockedUntil = DateTime.UtcNow + UnlockFor; // stay unlocked while being used
    }

    private void ShowMessage(string text, bool ok)
    {
        _message.ForeColor = ok ? Color.DarkGreen : Color.Firebrick;
        _message.Text = text;
    }

    private static string Fmt(int seconds)
    {
        if (seconds < 0) return "no limit";
        var m = (int)Math.Ceiling(seconds / 60.0);
        return m >= 60 ? (m % 60 == 0 ? $"{m / 60}h" : $"{m / 60}h {m % 60:00}m") : $"{m} min";
    }

    /// <summary>One line on the dashboard: name, coloured bar, numbers and the +15/+30/+60 buttons.</summary>
    private sealed class Row
    {
        public readonly TableLayoutPanel Panel;
        private readonly Label _left = new() { AutoSize = true, Font = new Font("Segoe UI Semibold", 12f), Anchor = AnchorStyles.Left };
        private readonly Label _detail = new() { AutoSize = true, ForeColor = Color.DimGray, Anchor = AnchorStyles.Left };
        private readonly Bar _bar;

        public Row(DashboardForm owner, string name, string kind, Color color)
        {
            Panel = new TableLayoutPanel { AutoSize = true, ColumnCount = 2, RowCount = 3, Margin = new Padding(0, 6, 0, 6) };
            Panel.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 340));
            Panel.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            var nameLabel = new Label { Text = name, AutoSize = true, Font = new Font("Segoe UI Semibold", 10.5f), Margin = new Padding(0, 0, 0, 2) };
            _bar = new Bar(color) { Width = 330, Height = 14, Margin = new Padding(0, 2, 0, 4) };

            var buttons = new FlowLayoutPanel { AutoSize = true, WrapContents = false, Anchor = AnchorStyles.Left, Margin = new Padding(8, 0, 0, 0) };
            foreach (var m in new[] { 15, 30, 60 })
            {
                var b = new Button { Text = $"+{m}", Width = 52, Height = 30, Enabled = false, Margin = new Padding(2) };
                b.Click += async (_, _) => await owner.DoParentAction($"bonus-{kind}", m, b);
                owner._parentButtons.Add(b);
                buttons.Controls.Add(b);
            }

            Panel.Controls.Add(nameLabel, 0, 0);
            Panel.Controls.Add(_bar, 0, 1);
            Panel.Controls.Add(buttons, 1, 1);
            var numbers = new FlowLayoutPanel { AutoSize = true, WrapContents = false, Margin = new Padding(0) };
            numbers.Controls.AddRange([_left, _detail]);
            Panel.Controls.Add(numbers, 0, 2);
            Panel.SetColumnSpan(numbers, 2);
        }

        public void Update(int usedSeconds, int budgetSeconds, int leftSeconds, int bonusMinutes, string noLimitText = "no limit")
        {
            var extra = bonusMinutes > 0 ? $" (incl. +{bonusMinutes} min extra)" : "";
            if (budgetSeconds < 0)
            {
                _left.Text = $"{Fmt(usedSeconds)} used";
                _detail.Text = $"  ·  {noLimitText}";
                _left.ForeColor = Color.Black;
                _bar.Set(0, unlimited: true);
                return;
            }
            var fraction = budgetSeconds == 0 ? 0 : Math.Clamp(leftSeconds / (double)budgetSeconds, 0, 1);
            _left.Text = leftSeconds <= 0 ? "Used up" : $"{Fmt(leftSeconds)} left";
            _left.ForeColor = leftSeconds <= 0 ? Color.Firebrick : leftSeconds < 600 ? Color.DarkOrange : Color.Black;
            _detail.Text = $"  ·  used {Fmt(usedSeconds)} of {Fmt(budgetSeconds)}{extra}";
            _bar.Set(fraction, unlimited: false);
        }
    }

    /// <summary>A rounded bar showing how much time is left.</summary>
    private sealed class Bar : Control
    {
        private readonly Color _color;
        private double _fraction;
        private bool _unlimited;

        public Bar(Color color)
        {
            _color = color;
            DoubleBuffered = true;
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
        }

        public void Set(double fraction, bool unlimited)
        {
            if (Math.Abs(fraction - _fraction) < 0.001 && unlimited == _unlimited) return;
            _fraction = fraction;
            _unlimited = unlimited;
            Invalidate();
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.Clear(Parent?.BackColor ?? Color.White);
            var r = new Rectangle(0, 0, Width - 1, Height - 1);
            using (var bg = new SolidBrush(Color.FromArgb(232, 234, 238)))
                FillRounded(g, bg, r);
            if (_unlimited) return;
            var fill = _fraction <= 0 ? 0 : Math.Max(Height, (int)(r.Width * _fraction));
            if (fill <= 0) return;
            var color = _fraction < 0.15 ? Color.FromArgb(210, 60, 50) : _fraction < 0.35 ? Color.FromArgb(230, 150, 20) : _color;
            using var brush = new SolidBrush(color);
            FillRounded(g, brush, new Rectangle(0, 0, fill, r.Height));
        }

        private static void FillRounded(Graphics g, Brush brush, Rectangle r)
        {
            var d = Math.Min(r.Height, r.Width);
            if (d <= 0) return;
            using var path = new GraphicsPath();
            path.AddArc(r.X, r.Y, d, d, 90, 180);
            path.AddArc(r.Right - d, r.Y, d, d, 270, 180);
            path.CloseFigure();
            g.FillPath(brush, path);
        }
    }
}
