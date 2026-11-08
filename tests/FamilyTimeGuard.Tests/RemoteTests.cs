using FamilyTimeGuard.Core;

namespace FamilyTimeGuard.Tests;

public class RemoteCommandParserTests
{
    private static readonly string[] Names = ["Boong", "Chip"];
    private static RemoteCommand P(string text) => RemoteCommandParser.Parse(text, Names);

    [Fact]
    public void Simple_commands()
    {
        Assert.IsType<StatusCommand>(P("/status"));
        Assert.IsType<StatusCommand>(P("/status@chip_pc_guard_bot"));
        Assert.IsType<HelpCommand>(P("/start"));
        Assert.IsType<TodayCommand>(P("/today"));
        Assert.IsType<RulesCommand>(P("/rules"));
        Assert.IsType<ResumeCommand>(P("/resume"));
        Assert.IsType<UnlockCommand>(P("/unlock"));
    }

    [Theory]
    [InlineData("/bonus games 30", "games", 30)]
    [InlineData("/bonus 30 games", "games", 30)]
    [InlineData("/bonus youtube 15", "youtube", 15)]
    [InlineData("/BONUS YT 15", "youtube", 15)]
    [InlineData("/extra game 60", "games", 60)]
    public void Bonus(string text, string kind, int minutes)
    {
        var c = Assert.IsType<BonusCommand>(P(text));
        Assert.Equal(kind, c.Kind);
        Assert.Equal(minutes, c.Minutes);
        Assert.Null(c.Child);
    }

    [Fact]
    public void Child_name_is_optional_first_argument()
    {
        var c = Assert.IsType<BonusCommand>(P("/bonus chip games 30"));
        Assert.Equal("Chip", c.Child);
        Assert.Equal("Boong", P("/status Boong").Child);
    }

    [Theory]
    [InlineData("/bonus games")]
    [InlineData("/bonus games abc")]
    [InlineData("/bonus games 0")]
    [InlineData("/bonus games 9999")]
    [InlineData("/bonus pizza 30")]
    [InlineData("/set school games")]
    [InlineData("/set monday games 60")]
    [InlineData("/bedtime school 9pm")]
    [InlineData("/pause forever")]
    [InlineData("/pause 48h")]
    [InlineData("/dance")]
    [InlineData("hello")]
    public void Invalid_input_gives_a_helpful_message(string text) =>
        Assert.False(string.IsNullOrWhiteSpace(Assert.IsType<InvalidCommand>(P(text)).Problem));

    [Fact]
    public void Set_budget()
    {
        var c = Assert.IsType<SetBudgetCommand>(P("/set weekend youtube 90"));
        Assert.Equal(("weekend", "youtube", 90), (c.DayType, c.Kind, c.Minutes));
        Assert.Equal(-1, Assert.IsType<SetBudgetCommand>(P("/set school games -1")).Minutes);
    }

    [Fact]
    public void Bedtime_and_wakeup()
    {
        var b = Assert.IsType<SetBedtimeCommand>(P("/bedtime school 21:30"));
        Assert.Equal(("school", "lock", "21:30"), (b.NightType, b.Which, b.Time));
        var w = Assert.IsType<SetBedtimeCommand>(P("/wakeup weekend 7:00"));
        Assert.Equal(("weekend", "unlock", "07:00"), (w.NightType, w.Which, w.Time));
    }

    [Fact]
    public void Pause_and_lock_durations()
    {
        Assert.Equal(TimeSpan.FromHours(2), Assert.IsType<PauseCommand>(P("/pause 2h")).Duration);
        Assert.Equal(TimeSpan.FromMinutes(90), Assert.IsType<PauseCommand>(P("/pause 1h30m")).Duration);
        Assert.Equal(TimeSpan.FromMinutes(45), Assert.IsType<PauseCommand>(P("/pause 45")).Duration);
        Assert.Null(Assert.IsType<LockCommand>(P("/lock")).Duration);
        Assert.Equal(TimeSpan.FromMinutes(30), Assert.IsType<LockCommand>(P("/lock Chip 30m")).Duration);
    }
}

public class RemoteConfigEditorTests
{
    [Fact]
    public void Changes_budget_and_bedtime()
    {
        var cfg = GuardConfig.CreateDefault();
        var chip = cfg.FindChildByName("Chip")!;
        var msg = RemoteConfigEditor.Apply(cfg, chip, new SetBudgetCommand("school", "games", 60));
        Assert.Equal(60, chip.SchoolDayBudget.GamesMinutes);
        Assert.Contains("is now 1h per day", msg);

        RemoteConfigEditor.Apply(cfg, chip, new SetBedtimeCommand("weekend", "lock", "22:30"));
        Assert.Equal("22:30", chip.WeekendNights.LockAt);
        Assert.Empty(cfg.Validate());
    }

    [Fact]
    public void Rejects_bad_unlock_time_and_keeps_old_value()
    {
        var cfg = GuardConfig.CreateDefault();
        var chip = cfg.FindChildByName("Chip")!;
        Assert.Throws<ConfigException>(() => RemoteConfigEditor.Apply(cfg, chip, new SetBedtimeCommand("school", "unlock", "13:00")));
        Assert.Equal("06:30", chip.SchoolNights.UnlockAt);
    }
}

public class ReportTests
{
    [Fact]
    public void Csv_parsing_handles_quotes_and_commas()
    {
        var entries = Reports.ParseActivityCsv([
            "time,child,event,detail",
            "16:00:01,Chip,games-start,RobloxPlayerBeta.exe",
            "16:30:00,Chip,youtube,\"Cats, dogs and \"\"more\"\" - YouTube - Google Chrome\"",
        ]);
        Assert.Equal(2, entries.Count);
        Assert.Equal("Cats, dogs and \"more\" - YouTube - Google Chrome", entries[1].Detail);
    }

    [Theory]
    [InlineData("Funny cats - YouTube - Google Chrome", "Funny cats")]
    [InlineData("(3) Minecraft tips - YouTube - Microsoft​ Edge", "Minecraft tips")]
    [InlineData("YouTube", "YouTube")]
    public void Cleans_video_titles(string raw, string expected) => Assert.Equal(expected, Reports.CleanTitle(raw));

    [Fact]
    public void Today_report_lists_games_videos_and_warnings()
    {
        var cfg = GuardConfig.CreateDefault();
        var chip = cfg.FindChildByName("Chip")!;
        var usage = new UsageDay { Child = "Chip", Date = "2026-11-09", GamesSeconds = 50 * 60, YouTubeSeconds = 20 * 60, BonusGamesMinutes = 15 };
        var entries = new List<ActivityEntry>
        {
            new("16:00:00", "Chip", "games-start", "RobloxPlayerBeta.exe"),
            new("17:00:00", "Chip", "youtube", "Funny cats - YouTube - Google Chrome"),
            new("17:05:00", "Chip", "youtube", "Funny cats - YouTube - Google Chrome"),
            new("23:40:00", "Chip", "bedtime-logoff", ""),
            new("18:00:00", "Boong", "youtube", "Not Chip's video - YouTube - Google Chrome"),
        };
        var text = Reports.Today(chip, usage, chip.SchoolDayBudget, entries);
        Assert.Contains("Games: 50 min of 1h", text);       // 45 + 15 bonus
        Assert.Contains("RobloxPlayerBeta", text);
        Assert.Contains("YouTube videos (1)", text);
        Assert.Contains("• Funny cats", text);
        Assert.DoesNotContain("Not Chip's video", text);
        Assert.Contains("Logged off at bedtime: 1", text);
    }

    [Fact]
    public void Status_text()
    {
        var cfg = GuardConfig.CreateDefault();
        var chip = cfg.FindChildByName("Chip")!;
        var usage = new UsageDay { GamesSeconds = 45 * 60, YouTubeSeconds = 10 * 60 };
        var text = Reports.Status(chip, null, usage, chip.SchoolDayBudget, true, false, false);
        Assert.Contains("not signed in", text);
        Assert.Contains("Games: 45 min used, none left", text);
        Assert.Contains("YouTube: 10 min used, 20 min left", text);
    }
}

public class ParentLockTests
{
    [Fact]
    public void Lock_acts_like_bedtime_and_beats_pause()
    {
        var cfg = GuardConfig.CreateDefault();
        var chip = cfg.FindChildByName("Chip")!;
        var usage = new UsageDay();
        var obs = new Observation(true, null, null, true, false);
        var r = RuleEngine.Tick(cfg, chip, usage, obs, new DateTime(2026, 11, 9, 15, 0, 0), TimeSpan.FromSeconds(2), paused: true, parentLocked: true);
        Assert.True(r.Bedtime);
        Assert.True(r.ParentLocked);
        Assert.False(r.Paused);
        Assert.Equal(0, usage.GamesSeconds);
    }

    [Fact]
    public void Used_up_events_are_reported_once()
    {
        var cfg = GuardConfig.CreateDefault();
        var chip = cfg.FindChildByName("Chip")!;
        var usage = new UsageDay { GamesSeconds = 45 * 60 };
        var obs = new Observation(true, null, null, true, false);
        var t = new DateTime(2026, 11, 9, 15, 0, 0);
        Assert.Contains("games-used-up", RuleEngine.Tick(cfg, chip, usage, obs, t, TimeSpan.Zero, false).Events);
        Assert.Empty(RuleEngine.Tick(cfg, chip, usage, obs, t, TimeSpan.Zero, false).Events);
    }

    [Fact]
    public void Next_morning_unlock()
    {
        var cfg = GuardConfig.CreateDefault();
        var chip = cfg.FindChildByName("Chip")!;
        // Monday 15:00 -> Tuesday 06:30 (school day)
        Assert.Equal(new DateTime(2026, 11, 10, 6, 30, 0), Schedule.NextMorningUnlock(cfg, chip, new DateTime(2026, 11, 9, 15, 0, 0)));
        // Friday 20:00 -> Saturday 07:00 (weekend)
        Assert.Equal(new DateTime(2026, 11, 14, 7, 0, 0), Schedule.NextMorningUnlock(cfg, chip, new DateTime(2026, 11, 13, 20, 0, 0)));
        // Tuesday 05:00 -> Tuesday 06:30
        Assert.Equal(new DateTime(2026, 11, 10, 6, 30, 0), Schedule.NextMorningUnlock(cfg, chip, new DateTime(2026, 11, 10, 5, 0, 0)));
    }

    [Fact]
    public void Telegram_settings_round_trip_and_children_on_pc()
    {
        var cfg = GuardConfig.CreateDefault();
        cfg.Telegram.OwnerUserId = 42;
        cfg.Telegram.OwnerChatId = 42;
        cfg.Telegram.EncryptedBotToken = "abc";
        cfg.Children[1].WindowsAccounts.Add("chip");
        var back = GuardConfig.Parse(cfg.ToJson());
        Assert.True(back.Telegram.IsReady);
        Assert.Equal("Chip", Assert.Single(back.ChildrenOnThisPc()).Name);
    }
}
