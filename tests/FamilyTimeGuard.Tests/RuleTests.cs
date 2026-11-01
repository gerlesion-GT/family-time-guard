using FamilyTimeGuard.Core;

namespace FamilyTimeGuard.Tests;

public class ScheduleTests
{
    private static readonly GuardConfig Cfg = GuardConfig.CreateDefault();
    private static ChildProfile Chip => Cfg.FindChildByName("Chip")!;     // school nights 21:00-06:30, weekend nights 22:00-07:00
    private static ChildProfile Boong => Cfg.FindChildByName("Boong")!;   // school nights 22:00-06:30, weekend nights 23:00-07:00

    // 2026-11-02 is a Monday.
    private static DateTime At(int day, int h, int m) => new(2026, 11, day, h, m, 0);

    [Theory]
    [InlineData(2, 20, 59, false)]  // Monday evening, before Chip's 21:00
    [InlineData(2, 21, 0, true)]
    [InlineData(2, 23, 30, true)]
    [InlineData(3, 0, 30, true)]    // midnight gaming: locked
    [InlineData(3, 6, 29, true)]
    [InlineData(3, 6, 30, false)]   // Tuesday morning unlock
    [InlineData(6, 21, 30, false)]  // Friday night is a weekend night (22:00)
    [InlineData(6, 22, 0, true)]
    [InlineData(7, 6, 45, true)]    // Saturday morning: unlock 07:00
    [InlineData(7, 7, 0, false)]
    [InlineData(8, 21, 30, true)]   // Sunday night is a school night (21:00)
    [InlineData(9, 6, 45, false)]   // Monday morning unlock 06:30
    public void Chip_bedtime(int day, int h, int m, bool expected) =>
        Assert.Equal(expected, Schedule.IsBedtime(Cfg, Chip, At(day, h, m)));

    [Fact]
    public void Boong_has_later_bedtime() =>
        Assert.False(Schedule.IsBedtime(Cfg, Boong, At(2, 21, 30)));

    [Fact]
    public void Next_bedtime_and_unlock_time()
    {
        Assert.Equal(At(2, 21, 0), Schedule.NextBedtime(Cfg, Chip, At(2, 15, 0)));
        Assert.Equal(At(6, 22, 0), Schedule.NextBedtime(Cfg, Chip, At(6, 15, 0)));
        Assert.Null(Schedule.NextBedtime(Cfg, Chip, At(2, 22, 0)));
        Assert.Equal(At(3, 6, 30), Schedule.UnlockTime(Cfg, Chip, At(2, 22, 0)));
        Assert.Equal(At(7, 7, 0), Schedule.UnlockTime(Cfg, Chip, At(7, 1, 0)));
        Assert.Null(Schedule.UnlockTime(Cfg, Chip, At(2, 15, 0)));
    }

    [Fact]
    public void Lock_time_after_midnight_is_supported()
    {
        var cfg = GuardConfig.CreateDefault();
        var c = cfg.FindChildByName("Boong")!;
        c.WeekendNights = new NightRule { LockAt = "00:30", UnlockAt = "07:00" };
        Assert.False(Schedule.IsBedtime(cfg, c, At(7, 0, 15)));  // Friday night, 00:15 Saturday: still allowed
        Assert.True(Schedule.IsBedtime(cfg, c, At(7, 0, 30)));
        Assert.Equal(At(7, 0, 30), Schedule.NextBedtime(cfg, c, At(6, 23, 0)));
    }

    [Fact]
    public void Holidays_use_weekend_rules()
    {
        var cfg = GuardConfig.CreateDefault();
        cfg.HolidayDates.Add("2026-11-03"); // a Tuesday
        var chip = cfg.FindChildByName("Chip")!;
        Assert.Equal(120, Schedule.BudgetFor(cfg, chip, new DateOnly(2026, 11, 3)).GamesMinutes);
        Assert.False(Schedule.IsBedtime(cfg, chip, At(2, 21, 30))); // night before the holiday = weekend night
    }
}

public class RuleEngineTests
{
    private static readonly DateTime MondayAfternoon = new(2026, 11, 2, 16, 0, 0);

    private static (GuardConfig, ChildProfile, UsageDay) Setup()
    {
        var cfg = GuardConfig.CreateDefault();
        var chip = cfg.FindChildByName("Chip")!; // 45 min games, 30 min YouTube on school days
        return (cfg, chip, new UsageDay { Child = "Chip", Date = "2026-11-02" });
    }

    private static Observation Playing => new(true, "Minecraft", "Minecraft.Windows.exe", true, false);
    private static Observation WatchingYouTube => new(false, "Funny cats - YouTube - Google Chrome", "chrome.exe", true, false);
    private static Observation Homework => new(false, "Essay.docx - Word", "WINWORD.EXE", true, false);

    [Fact]
    public void Counts_game_time_and_closes_games_when_used_up()
    {
        var (cfg, chip, usage) = Setup();
        usage.GamesSeconds = 45 * 60 - 5;
        var r = RuleEngine.Tick(cfg, chip, usage, Playing, MondayAfternoon, TimeSpan.FromSeconds(2), false);
        Assert.True(r.CountingGames);
        Assert.False(r.KillGames);
        Assert.Contains(r.Notices, n => n.Title.Contains("2 minutes"));

        r = RuleEngine.Tick(cfg, chip, usage, Playing, MondayAfternoon, TimeSpan.FromSeconds(4), false);
        Assert.Equal(0, r.GamesLeftSeconds);
        Assert.True(r.KillGames);
        Assert.Contains(r.Notices, n => n.Kind == Notice.Big);

        // Notice is shown only once, but games keep getting closed.
        r = RuleEngine.Tick(cfg, chip, usage, Playing, MondayAfternoon, TimeSpan.FromSeconds(2), false);
        Assert.True(r.KillGames);
        Assert.Empty(r.Notices);
    }

    [Fact]
    public void Homework_does_not_use_time()
    {
        var (cfg, chip, usage) = Setup();
        var r = RuleEngine.Tick(cfg, chip, usage, Homework, MondayAfternoon, TimeSpan.FromSeconds(2), false);
        Assert.False(r.CountingGames);
        Assert.False(r.CountingYouTube);
        Assert.Equal(0, usage.GamesSeconds + usage.YouTubeSeconds);
    }

    [Fact]
    public void YouTube_is_counted_then_blocked()
    {
        var (cfg, chip, usage) = Setup();
        var r = RuleEngine.Tick(cfg, chip, usage, WatchingYouTube, MondayAfternoon, TimeSpan.FromSeconds(2), false);
        Assert.True(r.CountingYouTube);
        Assert.False(r.BlockYouTube);

        usage.YouTubeSeconds = 30 * 60;
        r = RuleEngine.Tick(cfg, chip, usage, WatchingYouTube, MondayAfternoon, TimeSpan.FromSeconds(2), false);
        Assert.True(r.BlockYouTube);
        Assert.True(r.CloseForegroundTab);
    }

    [Fact]
    public void Web_games_count_as_games()
    {
        var (cfg, chip, usage) = Setup();
        var obs = new Observation(false, "Subway Surfers - Play on Poki - Google Chrome", "chrome.exe", true, false);
        var r = RuleEngine.Tick(cfg, chip, usage, obs, MondayAfternoon, TimeSpan.FromSeconds(2), false);
        Assert.True(r.CountingGames);
    }

    [Fact]
    public void Locked_screen_and_long_sleep_are_not_counted_fully()
    {
        var (cfg, chip, usage) = Setup();
        RuleEngine.Tick(cfg, chip, usage, Playing with { SessionLocked = true }, MondayAfternoon, TimeSpan.FromSeconds(2), false);
        Assert.Equal(0, usage.GamesSeconds);
        RuleEngine.Tick(cfg, chip, usage, Playing, MondayAfternoon, TimeSpan.FromHours(3), false);
        Assert.Equal(RuleEngine.MaxTickCredit.TotalSeconds, usage.GamesSeconds);
    }

    [Fact]
    public void Bonus_time_reopens_games()
    {
        var (cfg, chip, usage) = Setup();
        usage.GamesSeconds = 45 * 60;
        Assert.True(RuleEngine.Tick(cfg, chip, usage, Playing, MondayAfternoon, TimeSpan.Zero, false).KillGames);
        RuleEngine.AddBonus(usage, "games", 15);
        var r = RuleEngine.Tick(cfg, chip, usage, Playing, MondayAfternoon, TimeSpan.Zero, false);
        Assert.False(r.KillGames);
        Assert.Equal(15 * 60, r.GamesLeftSeconds);
    }

    [Fact]
    public void Pause_stops_everything()
    {
        var (cfg, chip, usage) = Setup();
        usage.GamesSeconds = 999 * 60;
        var r = RuleEngine.Tick(cfg, chip, usage, Playing, MondayAfternoon, TimeSpan.FromSeconds(2), paused: true);
        Assert.False(r.KillGames);
        Assert.False(r.BlockYouTube);
    }

    [Fact]
    public void Bedtime_warnings_are_given_once()
    {
        var (cfg, chip, usage) = Setup();
        var r = RuleEngine.Tick(cfg, chip, usage, Homework, new DateTime(2026, 11, 2, 20, 46, 0), TimeSpan.Zero, false);
        Assert.Contains(r.Notices, n => n.Title == "Bedtime soon");
        r = RuleEngine.Tick(cfg, chip, usage, Homework, new DateTime(2026, 11, 2, 20, 47, 0), TimeSpan.Zero, false);
        Assert.Empty(r.Notices);
        r = RuleEngine.Tick(cfg, chip, usage, Homework, new DateTime(2026, 11, 2, 20, 59, 10), TimeSpan.Zero, false);
        Assert.Contains(r.Notices, n => n.Kind == Notice.Big);
        r = RuleEngine.Tick(cfg, chip, usage, Homework, new DateTime(2026, 11, 2, 21, 0, 0), TimeSpan.Zero, false);
        Assert.True(r.Bedtime);
    }

    [Fact]
    public void YouTube_blocked_when_helper_is_missing()
    {
        var (cfg, chip, usage) = Setup();
        var r = RuleEngine.Tick(cfg, chip, usage, new Observation(false, null, null, false, false), MondayAfternoon, TimeSpan.Zero, false);
        Assert.True(r.BlockYouTube);
    }

    [Fact]
    public void Unlimited_budget()
    {
        var (cfg, chip, usage) = Setup();
        chip.SchoolDayBudget.GamesMinutes = -1;
        usage.GamesSeconds = 10_000;
        var r = RuleEngine.Tick(cfg, chip, usage, Playing, MondayAfternoon, TimeSpan.FromSeconds(2), false);
        Assert.False(r.KillGames);
        Assert.Equal(-1, r.GamesLeftSeconds);
    }
}

public class GameDetectionTests
{
    private readonly GameRules _rules = GameRules.CreateDefault();

    [Theory]
    [InlineData("RobloxPlayerBeta.exe", null, true)]
    [InlineData("RobloxStudioBeta.exe", null, false)]
    [InlineData("SomeGame.exe", @"C:\Program Files (x86)\Steam\steamapps\common\SomeGame\SomeGame.exe", true)]
    [InlineData("steam.exe", @"C:\Program Files (x86)\Steam\steam.exe", false)]
    [InlineData("wallpaper64.exe", @"C:\Program Files (x86)\Steam\steamapps\common\wallpaper_engine\wallpaper64.exe", false)]
    [InlineData("EpicGamesLauncher.exe", @"C:\Program Files (x86)\Epic Games\Launcher\Portal\Binaries\Win64\EpicGamesLauncher.exe", false)]
    [InlineData("WINWORD.EXE", @"C:\Program Files\Microsoft Office\root\Office16\WINWORD.EXE", false)]
    [InlineData("chrome.exe", @"C:\Program Files\Google\Chrome\Application\chrome.exe", false)]
    public void Detects_games(string name, string? path, bool expected) =>
        Assert.Equal(expected, _rules.IsGameProcess(name, path));
}

public class ConfigTests
{
    [Fact]
    public void Default_config_round_trips_and_is_valid()
    {
        var json = GuardConfig.CreateDefault().ToJson();
        var cfg = GuardConfig.Parse(json);
        Assert.Equal(2, cfg.Children.Count);
        Assert.Empty(cfg.Validate());
    }

    [Fact]
    public void Comments_and_trailing_commas_are_allowed()
    {
        var json = GuardConfig.CreateDefault().ToJson().Replace("\"timeZone\"", "// my note\n  \"timeZone\"");
        Assert.NotNull(GuardConfig.Parse(json));
    }

    [Fact]
    public void Bad_times_are_reported()
    {
        var cfg = GuardConfig.CreateDefault();
        cfg.Children[0].SchoolNights.LockAt = "9pm";
        Assert.Throws<ConfigException>(() => GuardConfig.Parse(cfg.ToJson()));
    }

    [Fact]
    public void Account_lookup_ignores_case()
    {
        var cfg = GuardConfig.CreateDefault();
        cfg.Children[1].WindowsAccounts.Add("chip");
        Assert.Equal("Chip", cfg.FindChildByAccount("CHIP")?.Name);
        Assert.Null(cfg.FindChildByAccount("dad"));
    }

    [Fact]
    public void Pin_hashing()
    {
        var hash = PinHasher.Hash("2468");
        Assert.True(PinHasher.Verify("2468", hash));
        Assert.False(PinHasher.Verify("1357", hash));
        Assert.False(PinHasher.IsValidPin("12a4"));
        Assert.True(PinHasher.IsValidPin("1234"));
    }

    [Fact]
    public void Time_zone_resolves() =>
        Assert.Equal(TimeSpan.FromHours(7), TimeZones.Find("Asia/Ho_Chi_Minh").BaseUtcOffset);
}
