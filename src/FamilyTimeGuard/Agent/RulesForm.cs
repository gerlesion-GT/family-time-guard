using FamilyTimeGuard.Core;

namespace FamilyTimeGuard.Agent;

/// <summary>Parent form to change the daily limits and bedtimes (opened from the dashboard after the PIN).</summary>
internal sealed class RulesForm : Form
{
    private readonly string _pin;
    private readonly LimitBox _schoolScreen, _schoolGames, _schoolYouTube, _weekendScreen, _weekendGames, _weekendYouTube;
    private readonly DateTimePicker _schoolLock, _schoolUnlock, _weekendLock, _weekendUnlock;
    private readonly Label _result = new() { AutoSize = true, MaximumSize = new Size(560, 0), Margin = new Padding(0, 10, 0, 0) };

    public bool Saved { get; private set; }

    public RulesForm(string childName, RulesDto rules, string pin)
    {
        _pin = pin;
        Text = $"Daily limits{(string.IsNullOrEmpty(childName) ? "" : " – " + childName)}";
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = MinimizeBox = false;
        StartPosition = FormStartPosition.CenterParent;
        AutoScaleMode = AutoScaleMode.Dpi;
        AutoScaleDimensions = new SizeF(96f, 96f);
        AutoSize = true;
        AutoSizeMode = AutoSizeMode.GrowAndShrink;
        Font = new Font("Segoe UI", 10f);
        BackColor = Color.White;
        Padding = new Padding(20);

        var grid = new TableLayoutPanel { AutoSize = true, ColumnCount = 3 };
        grid.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        grid.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        grid.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));

        Label Head(string t) => new() { Text = t, AutoSize = true, UseMnemonic = false, Font = new Font("Segoe UI Semibold", 10.5f), Margin = new Padding(6, 10, 18, 4) };
        Label Name(string t) => new() { Text = t, AutoSize = true, Anchor = AnchorStyles.Left, Margin = new Padding(0, 8, 18, 4) };

        grid.Controls.Add(Head("Minutes per day"), 0, 0);
        grid.Controls.Add(Head("School days (Mon–Fri)"), 1, 0);
        grid.Controls.Add(Head("Weekends & holidays"), 2, 0);

        _schoolScreen = new LimitBox(rules.SchoolScreen); _weekendScreen = new LimitBox(rules.WeekendScreen);
        _schoolGames = new LimitBox(rules.SchoolGames); _weekendGames = new LimitBox(rules.WeekendGames);
        _schoolYouTube = new LimitBox(rules.SchoolYouTube); _weekendYouTube = new LimitBox(rules.WeekendYouTube);
        AddRow(grid, 1, Name("Screen time (total)"), _schoolScreen, _weekendScreen);
        AddRow(grid, 2, Name("Games"), _schoolGames, _weekendGames);
        AddRow(grid, 3, Name("YouTube"), _schoolYouTube, _weekendYouTube);

        grid.Controls.Add(Head("Bedtime"), 0, 4);
        grid.Controls.Add(Head("School nights (Sun–Thu)"), 1, 4);
        grid.Controls.Add(Head("Weekend nights (Fri–Sat)"), 2, 4);
        _schoolLock = Time(rules.SchoolLockAt); _weekendLock = Time(rules.WeekendLockAt);
        _schoolUnlock = Time(rules.SchoolUnlockAt); _weekendUnlock = Time(rules.WeekendUnlockAt);
        AddRow(grid, 5, Name("Computer locks at"), _schoolLock, _weekendLock);
        AddRow(grid, 6, Name("Unlocks next morning at"), _schoolUnlock, _weekendUnlock);

        var note = new Label
        {
            AutoSize = true, MaximumSize = new Size(560, 0), ForeColor = Color.DimGray, Margin = new Padding(0, 12, 0, 0),
            Text = "Screen time counts all computer use (homework too); when it is used up the computer locks like at bedtime. " +
                   "Games and YouTube are counted separately. Changes apply right away.",
        };

        var save = new Button { Text = "Save", AutoSize = true, Padding = new Padding(16, 4, 16, 4) };
        var cancel = new Button { Text = "Cancel", AutoSize = true, Padding = new Padding(10, 4, 10, 4), DialogResult = DialogResult.Cancel };
        var buttons = new FlowLayoutPanel { AutoSize = true, Margin = new Padding(0, 14, 0, 0), WrapContents = false };
        buttons.Controls.AddRange([save, cancel]);

        var root = new TableLayoutPanel { AutoSize = true, ColumnCount = 1, Dock = DockStyle.Fill };
        root.Controls.Add(grid);
        root.Controls.Add(note);
        root.Controls.Add(buttons);
        root.Controls.Add(_result);
        Controls.Add(root);
        AcceptButton = save;
        CancelButton = cancel;

        save.Click += async (_, _) =>
        {
            save.Enabled = false;
            var r = await ParentActions.Send("set-rules", 0, _pin, Collect());
            save.Enabled = true;
            _result.ForeColor = r.Ok ? Color.DarkGreen : Color.Firebrick;
            _result.Text = r.Ok ? r.Message : r.Error;
            if (r.Ok) Saved = true;
        };
    }

    private RulesDto Collect() => new()
    {
        SchoolScreen = _schoolScreen.Value, SchoolGames = _schoolGames.Value, SchoolYouTube = _schoolYouTube.Value,
        WeekendScreen = _weekendScreen.Value, WeekendGames = _weekendGames.Value, WeekendYouTube = _weekendYouTube.Value,
        SchoolLockAt = _schoolLock.Value.ToString("HH:mm"), SchoolUnlockAt = _schoolUnlock.Value.ToString("HH:mm"),
        WeekendLockAt = _weekendLock.Value.ToString("HH:mm"), WeekendUnlockAt = _weekendUnlock.Value.ToString("HH:mm"),
    };

    private static void AddRow(TableLayoutPanel grid, int row, Control name, Control school, Control weekend)
    {
        grid.Controls.Add(name, 0, row);
        grid.Controls.Add(school, 1, row);
        grid.Controls.Add(weekend, 2, row);
    }

    private static DateTimePicker Time(string hhmm)
    {
        TimeParse.TryParse(hhmm, out var t);
        return new DateTimePicker
        {
            Format = DateTimePickerFormat.Custom, CustomFormat = "HH:mm", ShowUpDown = true, Width = 90,
            Value = DateTime.Today + t, Margin = new Padding(6, 4, 18, 4),
        };
    }

    /// <summary>Minutes box with a "No limit" tick.</summary>
    private sealed class LimitBox : FlowLayoutPanel
    {
        private readonly NumericUpDown _minutes = new() { Minimum = 0, Maximum = 1440, Increment = 15, Width = 70, Margin = new Padding(0, 3, 4, 0) };
        private readonly CheckBox _noLimit = new() { Text = "No limit", AutoSize = true, Margin = new Padding(8, 5, 0, 0) };

        public LimitBox(int value)
        {
            AutoSize = true;
            WrapContents = false;
            Margin = new Padding(6, 2, 18, 2);
            _noLimit.Checked = value < 0;
            _minutes.Value = value < 0 ? 120 : Math.Min(value, 1440);
            _minutes.Enabled = value >= 0;
            _noLimit.CheckedChanged += (_, _) => _minutes.Enabled = !_noLimit.Checked;
            Controls.Add(_minutes);
            Controls.Add(new Label { Text = "min", AutoSize = true, Margin = new Padding(0, 6, 0, 0) });
            Controls.Add(_noLimit);
        }

        public int Value => _noLimit.Checked ? -1 : (int)_minutes.Value;
    }
}
