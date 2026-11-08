using FamilyTimeGuard.Core;

namespace FamilyTimeGuard.Agent;

/// <summary>Draws the dashboard and lock screen with sample data into PNG files (used by the automated build to check the layout).</summary>
internal static class UiPreview
{
    public static int Render(string folder)
    {
        Directory.CreateDirectory(folder);
        Application.EnableVisualStyles();
        Application.SetCompatibleTextRenderingDefault(false);

        var sample = new AgentResponse
        {
            Enforced = true, ChildName = "Chip", RemoteEnabled = true,
            Status = new StatusInfo
            {
                ScreenBudgetSeconds = 180 * 60, ScreenUsedSeconds = 125 * 60, ScreenLeftSeconds = 55 * 60, BonusScreenMinutes = 0,
                GamesBudgetSeconds = 60 * 60, GamesUsedSeconds = 52 * 60, GamesLeftSeconds = 8 * 60, BonusGamesMinutes = 15,
                YouTubeBudgetSeconds = 30 * 60, YouTubeUsedSeconds = 30 * 60, YouTubeLeftSeconds = 0,
                NextBedtime = "21:00", CountingGames = true,
            },
        };
        using (var dash = new DashboardForm { StartPosition = FormStartPosition.Manual, Location = new Point(-4000, -4000) })
        {
            dash.UpdateFrom(sample);
            Save(dash, Path.Combine(folder, "dashboard.png"));
        }

        sample.Status!.ScreenBudgetSeconds = -1;
        sample.Status.ScreenLeftSeconds = -1;
        using (var dash = new DashboardForm { StartPosition = FormStartPosition.Manual, Location = new Point(-4000, -4000) })
        {
            dash.UpdateFrom(sample);
            Save(dash, Path.Combine(folder, "dashboard-no-screen-limit.png"));
        }

        using (var lockForm = new BedtimeForm("Chip", null, "Screen time is over",
                   "You have used all your computer time for today, Chip. Please shut down the computer.", canAsk: true)
               { WindowState = FormWindowState.Normal, StartPosition = FormStartPosition.Manual, Location = new Point(-4000, -4000), Size = new Size(1280, 720) })
        {
            lockForm.UpdateCountdown(42);
            Save(lockForm, Path.Combine(folder, "lock-screen.png"));
            lockForm.ForceClose();
        }
        Console.WriteLine("Saved previews to " + Path.GetFullPath(folder));
        return 0;
    }

    private static void Save(Form form, string path)
    {
        form.Show();
        for (var i = 0; i < 10; i++) { Application.DoEvents(); Thread.Sleep(30); }
        using var bmp = new Bitmap(form.Width, form.Height);
        form.DrawToBitmap(bmp, new Rectangle(0, 0, form.Width, form.Height));
        bmp.Save(path, System.Drawing.Imaging.ImageFormat.Png);
        form.Hide();
    }
}
