using FamilyTimeGuard.Core;
using FamilyTimeGuard.Service;

namespace FamilyTimeGuard.Agent;

internal static class Theme
{
    public static readonly Color Blue = Color.FromArgb(24, 52, 98);
    public static readonly Color LightText = Color.FromArgb(235, 240, 250);
    public static readonly Font Title = new("Segoe UI Semibold", 12f);
    public static readonly Font Body = new("Segoe UI", 10f);
    public static readonly Font Huge = new("Segoe UI Light", 40f);
    public static readonly Font Large = new("Segoe UI", 18f);
}

/// <summary>Small pop-up in the bottom-right corner that does not steal focus from a game.</summary>
internal sealed class ToastForm : Form
{
    public ToastForm(string title, string message)
    {
        FormBorderStyle = FormBorderStyle.None;
        ShowInTaskbar = false;
        TopMost = true;
        StartPosition = FormStartPosition.Manual;
        BackColor = Theme.Blue;
        ForeColor = Theme.LightText;
        Size = new Size(400, 120);
        Padding = new Padding(16);

        var titleLabel = new Label { Text = title, Font = Theme.Title, Dock = DockStyle.Top, Height = 30, ForeColor = Color.White };
        var body = new Label { Text = message, Font = Theme.Body, Dock = DockStyle.Fill };
        Controls.Add(body);
        Controls.Add(titleLabel);
        foreach (Control c in Controls) c.Click += (_, _) => Close();
        Click += (_, _) => Close();

        var timer = new System.Windows.Forms.Timer { Interval = 12000 };
        timer.Tick += (_, _) => { timer.Dispose(); Close(); };
        timer.Start();
    }

    protected override bool ShowWithoutActivation => true;

    protected override CreateParams CreateParams
    {
        get
        {
            const int WS_EX_TOPMOST = 0x8, WS_EX_TOOLWINDOW = 0x80, WS_EX_NOACTIVATE = 0x08000000;
            var cp = base.CreateParams;
            cp.ExStyle |= WS_EX_TOPMOST | WS_EX_TOOLWINDOW | WS_EX_NOACTIVATE;
            return cp;
        }
    }
}

internal sealed class ToastStack
{
    private readonly List<ToastForm> _open = [];

    public void Show(string title, string message)
    {
        _open.RemoveAll(f => f.IsDisposed);
        var form = new ToastForm(title, message);
        var area = Screen.PrimaryScreen?.WorkingArea ?? new Rectangle(0, 0, 1280, 720);
        var y = area.Bottom - form.Height - 12 - _open.Count * (form.Height + 10);
        form.Location = new Point(area.Right - form.Width - 12, Math.Max(area.Top, y));
        form.FormClosed += (_, _) => _open.Remove(form);
        _open.Add(form);
        form.Show();
    }
}

/// <summary>A clear message in the middle of the screen (e.g. "Game time is over").</summary>
internal static class BigNotice
{
    public static void Show(string title, string message)
    {
        var form = new Form
        {
            FormBorderStyle = FormBorderStyle.None,
            StartPosition = FormStartPosition.CenterScreen,
            Size = new Size(620, 260),
            BackColor = Theme.Blue,
            ForeColor = Theme.LightText,
            TopMost = true,
            ShowInTaskbar = false,
            Padding = new Padding(28),
        };
        var ok = new Button
        {
            Text = "OK", Dock = DockStyle.Bottom, Height = 44, FlatStyle = FlatStyle.Flat,
            BackColor = Color.White, ForeColor = Theme.Blue, Font = Theme.Title,
        };
        ok.Click += (_, _) => form.Close();
        form.Controls.Add(new Label { Text = message, Font = Theme.Body, Dock = DockStyle.Fill, Padding = new Padding(0, 10, 0, 10) });
        form.Controls.Add(ok);
        form.Controls.Add(new Label { Text = title, Font = Theme.Large, Dock = DockStyle.Top, Height = 46, ForeColor = Color.White });
        form.AcceptButton = ok;
        form.Show();
        form.Activate();
    }
}

/// <summary>Full-screen bedtime message with the log-off countdown.</summary>
internal sealed class BedtimeForm : Form
{
    private readonly Label _countdown;
    private bool _allowClose;

    public BedtimeForm(string childName, string? unlockAt, string? lockTitle = null, string? lockMessage = null, bool canAsk = false)
    {
        FormBorderStyle = FormBorderStyle.None;
        WindowState = FormWindowState.Maximized;
        TopMost = true;
        ShowInTaskbar = false;
        BackColor = Color.FromArgb(12, 20, 45);
        ForeColor = Theme.LightText;

        var layout = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 6 };
        layout.RowStyles.Add(new RowStyle(SizeType.Percent, 35));
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.RowStyles.Add(new RowStyle(SizeType.Percent, 65));

        Label Centered(string text, Font font) => new()
        {
            Text = text, Font = font, AutoSize = false, Dock = DockStyle.Fill, Height = font.Height + 24,
            TextAlign = ContentAlignment.MiddleCenter,
        };

        layout.Controls.Add(new Label(), 0, 0);
        var title = lockTitle ?? $"It's bedtime{(string.IsNullOrEmpty(childName) ? "" : ", " + childName)}.";
        layout.Controls.Add(Centered(title, Theme.Huge), 0, 1);
        _countdown = Centered("", Theme.Large);
        layout.Controls.Add(_countdown, 0, 2);
        layout.Controls.Add(Centered(lockMessage ?? (unlockAt is null ? "Sleep well!" : $"The computer unlocks at {unlockAt}. Sleep well!"), Theme.Large), 0, 3);

        // Buttons: shut down (always), and ask a parent for more time (screen time, when the phone remote is set up).
        var buttons = new FlowLayoutPanel { AutoSize = true, Anchor = AnchorStyles.None, Padding = new Padding(0, 24, 0, 0) };
        Button Big(string text) => new()
        {
            Text = text, AutoSize = true, Padding = new Padding(18, 8, 18, 8), Margin = new Padding(12),
            FlatStyle = FlatStyle.Flat, BackColor = Color.White, ForeColor = Theme.Blue, Font = Theme.Title,
        };
        var shutdown = Big("Shut down the computer");
        shutdown.Click += async (_, _) =>
        {
            shutdown.Enabled = false;
            var r = await Task.Run(() =>
            {
                try { return PipeClientCall.Send(new AgentRequest { Type = "shutdown" }, 4000); }
                catch (Exception ex) { return new AgentResponse { Ok = false, Error = ex.Message }; }
            });
            if (!r.Ok)
            {
                // Fall back to shutting down as the user (allowed for standard users on home PCs).
                try { System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("shutdown.exe", "/s /t 0") { CreateNoWindow = true, UseShellExecute = false }); }
                catch (Exception) { shutdown.Enabled = true; }
            }
        };
        buttons.Controls.Add(shutdown);
        if (canAsk)
        {
            var ask = Big("Ask for more time");
            ask.Click += (_, _) =>
            {
                using var f = new AskForTimeForm("screen");
                f.ShowDialog(this);
            };
            buttons.Controls.Add(ask);
        }
        layout.Controls.Add(buttons, 0, 4);
        layout.Controls.Add(new Label(), 0, 5);
        Controls.Add(layout);
    }

    public void UpdateCountdown(int seconds)
    {
        _countdown.Text = $"Save your work. This computer will log off in {seconds} seconds.";
        if (!Visible) Show();
        TopMost = true;
    }

    public void ForceClose()
    {
        _allowClose = true;
        Close();
    }

    protected override void OnFormClosing(FormClosingEventArgs e)
    {
        if (!_allowClose && e.CloseReason == CloseReason.UserClosing) e.Cancel = true;
        base.OnFormClosing(e);
    }
}

internal sealed class PinForm : Form
{
    private readonly TextBox _pin;
    public string Pin => _pin.Text;

    public PinForm()
    {
        Text = "Parent options";
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = MinimizeBox = false;
        StartPosition = FormStartPosition.CenterScreen;
        ClientSize = new Size(340, 150);
        Font = Theme.Body;
        TopMost = true;

        var label = new Label { Text = "Enter the parent PIN:", Location = new Point(20, 20), AutoSize = true };
        _pin = new TextBox { Location = new Point(20, 50), Width = 300, UseSystemPasswordChar = true, MaxLength = 12 };
        var ok = new Button { Text = "OK", Location = new Point(140, 95), Width = 85, Height = 32 };
        var cancel = new Button { Text = "Cancel", Location = new Point(235, 95), Width = 85, Height = 32, DialogResult = DialogResult.Cancel };
        var error = new Label { Location = new Point(20, 100), AutoSize = true, ForeColor = Color.Firebrick };
        ok.Click += async (_, _) =>
        {
            ok.Enabled = false;
            var r = await ParentActions.Send("verify", 0, Pin);
            ok.Enabled = true;
            if (r.Ok) { DialogResult = DialogResult.OK; Close(); }
            else { error.Text = r.Error ?? "Wrong PIN"; _pin.SelectAll(); _pin.Focus(); }
        };
        Controls.AddRange([label, _pin, ok, cancel, error]);
        AcceptButton = ok;
        CancelButton = cancel;
        error.BringToFront();
        ok.BringToFront();
        cancel.BringToFront();
    }
}

internal static class ParentActions
{
    public static async Task<AgentResponse> Send(string action, int minutes, string pin, RulesDto? rules = null)
    {
        try
        {
            return await Task.Run(() => PipeClientCall.Send(new AgentRequest
            {
                Type = "parent",
                Parent = new ParentCommand { Action = action, Minutes = minutes, Pin = pin, Rules = rules },
            }, 4000));
        }
        catch (Exception ex)
        {
            return new AgentResponse { Ok = false, Error = "Cannot reach the Guard service: " + ex.Message };
        }
    }
}

/// <summary>The child asks the parent (on their phone) for more time.</summary>
internal sealed class AskForTimeForm : Form
{
    public AskForTimeForm(string initialKind = "games")
    {
        Text = "Ask for more time";
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = MinimizeBox = false;
        StartPosition = FormStartPosition.CenterScreen;
        ClientSize = new Size(400, 300);
        Font = Theme.Body;
        TopMost = true;

        var y = 18;
        Controls.Add(new Label { Text = "What for?", Location = new Point(20, y), AutoSize = true, Font = Theme.Title });
        y += 32;
        var games = new RadioButton { Text = "Games", Location = new Point(24, y), AutoSize = true, Checked = true };
        var youtube = new RadioButton { Text = "YouTube", Location = new Point(140, y), AutoSize = true, Checked = initialKind == "youtube" };
        var screen = new RadioButton { Text = "Computer", AutoSize = true, Checked = initialKind == "screen" };
        if (initialKind != "games") games.Checked = false;
        var kindPanel = new Panel { Location = new Point(0, y - 4), Size = new Size(400, 32) };
        games.Location = new Point(24, 4); youtube.Location = new Point(130, 4); screen.Location = new Point(250, 4);
        kindPanel.Controls.AddRange([games, youtube, screen]);
        Controls.Add(kindPanel);
        y += 40;
        Controls.Add(new Label { Text = "How long?", Location = new Point(20, y), AutoSize = true, Font = Theme.Title });
        y += 32;
        var minutes = new ComboBox { Location = new Point(24, y), Width = 140, DropDownStyle = ComboBoxStyle.DropDownList };
        minutes.Items.AddRange(["15 minutes", "30 minutes", "60 minutes"]);
        minutes.SelectedIndex = 0;
        Controls.Add(minutes);
        y += 40;
        Controls.Add(new Label { Text = "Message (optional):", Location = new Point(20, y), AutoSize = true });
        y += 24;
        var note = new TextBox { Location = new Point(24, y), Width = 350, MaxLength = 150, PlaceholderText = "e.g. I finished my homework!" };
        Controls.Add(note);
        y += 40;
        var send = new Button { Text = "Send", Location = new Point(194, y), Size = new Size(85, 32) };
        var cancel = new Button { Text = "Close", Location = new Point(289, y), Size = new Size(85, 32), DialogResult = DialogResult.Cancel };
        var result = new Label { Location = new Point(20, y + 40), Size = new Size(360, 40), ForeColor = Color.DarkGreen };
        send.Click += async (_, _) =>
        {
            send.Enabled = false;
            var r = await Task.Run(() =>
            {
                try
                {
                    return PipeClientCall.Send(new AgentRequest
                    {
                        Type = "request-time",
                        Request = new TimeRequest
                        {
                            Kind = youtube.Checked ? "youtube" : screen.Checked ? "screen" : "games",
                            Minutes = minutes.SelectedIndex switch { 1 => 30, 2 => 60, _ => 15 },
                            Note = note.Text,
                        },
                    }, 4000);
                }
                catch (Exception ex) { return new AgentResponse { Ok = false, Error = "Cannot reach Family Time Guard: " + ex.Message }; }
            });
            result.ForeColor = r.Ok ? Color.DarkGreen : Color.Firebrick;
            result.Text = r.Ok ? r.Message : r.Error;
            if (!r.Ok) send.Enabled = true;
        };
        Controls.AddRange([send, cancel, result]);
        AcceptButton = send;
        CancelButton = cancel;
        ClientSize = new Size(400, y + 90);
    }
}

