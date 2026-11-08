using FamilyTimeGuard.Core;

namespace FamilyTimeGuard.Tests;

public class ScreenTimeTests
{
    private static readonly DateTime MondayAfternoon = new(2026, 11, 9, 16, 0, 0);

    private static (GuardConfig, ChildProfile, UsageDay) Setup(int screenMinutes = 120)
    {
        var cfg = GuardConfig.CreateDefault();
        var chip = cfg.FindChildByName("Chip")!;
        chip.SchoolDayBudget.ScreenMinutes = screenMinutes;
        return (cfg, chip, new UsageDay { Child = "Chip", Date = "2026-11-09" });
    }

    private static Observation Homework(int idle = 0) => new(false, "Essay.docx - Word", "WINWORD.EXE", true, false, idle);
    private static Observation Video(int idle) => new(false, "Cats - YouTube - Google Chrome", "chrome.exe", true, false, idle);

    [Fact]
    public void Off_by_default()
    {
        var cfg = GuardConfig.CreateDefault();
        var chip = cfg.FindChildByName("Chip")!;
        var usage = new UsageDay { ScreenSeconds = 99_999 };
        var r = RuleEngine.Tick(cfg, chip, usage, Homework(), MondayAfternoon, TimeSpan.FromSeconds(2), false);
        Assert.False(r.ScreenTimeUp);
        Assert.False(r.Bedtime);
        Assert.Equal(-1, r.ScreenLeftSeconds);
    }

    [Fact]
    public void Homework_counts_toward_screen_time_but_idle_does_not()
    {
        var (cfg, chip, usage) = Setup();
        RuleEngine.Tick(cfg, chip, usage, Homework(idle: 10), MondayAfternoon, TimeSpan.FromSeconds(2), false);
        Assert.Equal(2, usage.ScreenSeconds);
        RuleEngine.Tick(cfg, chip, usage, Homework(idle: 600), MondayAfternoon, TimeSpan.FromSeconds(2), false);
        Assert.Equal(2, usage.ScreenSeconds); // away from the computer
        RuleEngine.Tick(cfg, chip, usage, Video(idle: 600), MondayAfternoon, TimeSpan.FromSeconds(2), false);
        Assert.Equal(4, usage.ScreenSeconds); // watching a video without touching the mouse still counts
    }

    [Fact]
    public void Used_up_screen_time_locks_like_bedtime_once()
    {
        var (cfg, chip, usage) = Setup(screenMinutes: 60);
        usage.ScreenSeconds = 60 * 60 - 1;
        var r = RuleEngine.Tick(cfg, chip, usage, Homework(), MondayAfternoon, TimeSpan.FromSeconds(2), false);
        Assert.True(r.ScreenTimeUp);
        Assert.True(r.Bedtime);
        Assert.Contains("screen-used-up", r.Events);

        r = RuleEngine.Tick(cfg, chip, usage, Homework(), MondayAfternoon, TimeSpan.FromSeconds(2), false);
        Assert.True(r.Bedtime);
        Assert.Empty(r.Events);
        Assert.False(r.CountingGames || r.CountingYouTube || r.CountingScreen);
    }

    [Fact]
    public void Warnings_before_screen_time_runs_out()
    {
        var (cfg, chip, usage) = Setup(screenMinutes: 60);
        usage.ScreenSeconds = 60 * 60 - 14 * 60;
        var r = RuleEngine.Tick(cfg, chip, usage, Homework(), MondayAfternoon, TimeSpan.FromSeconds(2), false);
        Assert.Contains(r.Notices, n => n.Title.Contains("15 minutes of screen time"));
        usage.ScreenSeconds = 60 * 60 - 50;
        r = RuleEngine.Tick(cfg, chip, usage, Homework(), MondayAfternoon, TimeSpan.FromSeconds(2), false);
        Assert.Contains(r.Notices, n => n.Kind == Notice.Big && n.Title.Contains("1 minute"));
    }

    [Fact]
    public void Bonus_and_pause_unlock()
    {
        var (cfg, chip, usage) = Setup(screenMinutes: 60);
        usage.ScreenSeconds = 60 * 60;
        Assert.True(RuleEngine.Tick(cfg, chip, usage, Homework(), MondayAfternoon, TimeSpan.Zero, false).Bedtime);
        Assert.False(RuleEngine.Tick(cfg, chip, usage, Homework(), MondayAfternoon, TimeSpan.Zero, paused: true).Bedtime);
        RuleEngine.AddBonus(usage, "screen", 30);
        var r = RuleEngine.Tick(cfg, chip, usage, Homework(), MondayAfternoon, TimeSpan.Zero, false);
        Assert.False(r.Bedtime);
        Assert.Equal(30 * 60, r.ScreenLeftSeconds);
    }

    [Fact]
    public void Commands_and_reports()
    {
        var c = Assert.IsType<SetBudgetCommand>(RemoteCommandParser.Parse("/set school screen 180", ["Chip"]));
        Assert.Equal("screen", c.Kind);
        Assert.Equal("screen", Assert.IsType<BonusCommand>(RemoteCommandParser.Parse("/bonus pc 30", ["Chip"])).Kind);

        var cfg = GuardConfig.CreateDefault();
        var chip = cfg.FindChildByName("Chip")!;
        var msg = RemoteConfigEditor.Apply(cfg, chip, c);
        Assert.Equal(180, chip.SchoolDayBudget.ScreenMinutes);
        Assert.Contains("locks until tomorrow", msg);
        Assert.Contains("screen 3h", RemoteConfigEditor.DescribeRules(cfg, chip));

        var usage = new UsageDay { Date = "2026-11-09", ScreenSeconds = 100 * 60 };
        Assert.Contains("Screen time: 1h 40m used, 1h 20m left", Reports.Status(chip, null, usage, chip.SchoolDayBudget, true, false, false));
        Assert.Contains("Screen time: 1h 40m of 3h", Reports.Today(chip, usage, chip.SchoolDayBudget, []));
    }
}
