namespace FamilyTimeGuard.Core;

/// <summary>One child's daily limits and bedtimes, as edited on the dashboard.</summary>
public sealed class RulesDto
{
    public int SchoolScreen { get; set; } = -1;
    public int SchoolGames { get; set; }
    public int SchoolYouTube { get; set; }
    public int WeekendScreen { get; set; } = -1;
    public int WeekendGames { get; set; }
    public int WeekendYouTube { get; set; }
    public string SchoolLockAt { get; set; } = "21:00";
    public string SchoolUnlockAt { get; set; } = "06:30";
    public string WeekendLockAt { get; set; } = "22:00";
    public string WeekendUnlockAt { get; set; } = "07:00";

    public static RulesDto From(ChildProfile c) => new()
    {
        SchoolScreen = c.SchoolDayBudget.ScreenMinutes,
        SchoolGames = c.SchoolDayBudget.GamesMinutes,
        SchoolYouTube = c.SchoolDayBudget.YouTubeMinutes,
        WeekendScreen = c.WeekendBudget.ScreenMinutes,
        WeekendGames = c.WeekendBudget.GamesMinutes,
        WeekendYouTube = c.WeekendBudget.YouTubeMinutes,
        SchoolLockAt = c.SchoolNights.LockAt,
        SchoolUnlockAt = c.SchoolNights.UnlockAt,
        WeekendLockAt = c.WeekendNights.LockAt,
        WeekendUnlockAt = c.WeekendNights.UnlockAt,
    };

    /// <summary>Checks the values and applies them to the child. Returns a list of what changed (empty = nothing changed).</summary>
    public List<string> ApplyTo(ChildProfile c)
    {
        var errors = new List<string>();
        foreach (var (name, v) in new[]
                 {
                     ("school-day screen time", SchoolScreen), ("school-day games", SchoolGames), ("school-day YouTube", SchoolYouTube),
                     ("weekend screen time", WeekendScreen), ("weekend games", WeekendGames), ("weekend YouTube", WeekendYouTube),
                 })
            if (v is < -1 or > 24 * 60) errors.Add($"The {name} limit must be between 0 and 1440 minutes (or no limit).");

        var school = new NightRule { LockAt = Norm(SchoolLockAt), UnlockAt = Norm(SchoolUnlockAt) };
        var weekend = new NightRule { LockAt = Norm(WeekendLockAt), UnlockAt = Norm(WeekendUnlockAt) };
        school.Validate("School nights", errors);
        weekend.Validate("Weekend nights", errors);
        if (errors.Count > 0) throw new ConfigException(string.Join(" ", errors));

        var changes = new List<string>();
        void Minutes(string what, int oldValue, int newValue, Action<int> set)
        {
            if (oldValue == newValue) return;
            set(newValue);
            changes.Add($"{what}: {RemoteCommandParser.FormatMinutes(oldValue)} → {RemoteCommandParser.FormatMinutes(newValue)}");
        }
        void Time(string what, string oldValue, string newValue, Action<string> set)
        {
            if (oldValue == newValue) return;
            set(newValue);
            changes.Add($"{what}: {oldValue} → {newValue}");
        }

        Minutes("Screen time on school days", c.SchoolDayBudget.ScreenMinutes, SchoolScreen, v => c.SchoolDayBudget.ScreenMinutes = v);
        Minutes("Games on school days", c.SchoolDayBudget.GamesMinutes, SchoolGames, v => c.SchoolDayBudget.GamesMinutes = v);
        Minutes("YouTube on school days", c.SchoolDayBudget.YouTubeMinutes, SchoolYouTube, v => c.SchoolDayBudget.YouTubeMinutes = v);
        Minutes("Screen time on weekends", c.WeekendBudget.ScreenMinutes, WeekendScreen, v => c.WeekendBudget.ScreenMinutes = v);
        Minutes("Games on weekends", c.WeekendBudget.GamesMinutes, WeekendGames, v => c.WeekendBudget.GamesMinutes = v);
        Minutes("YouTube on weekends", c.WeekendBudget.YouTubeMinutes, WeekendYouTube, v => c.WeekendBudget.YouTubeMinutes = v);
        Time("Bedtime on school nights", c.SchoolNights.LockAt, school.LockAt, v => c.SchoolNights.LockAt = v);
        Time("Unlock after school nights", c.SchoolNights.UnlockAt, school.UnlockAt, v => c.SchoolNights.UnlockAt = v);
        Time("Bedtime on weekend nights", c.WeekendNights.LockAt, weekend.LockAt, v => c.WeekendNights.LockAt = v);
        Time("Unlock after weekend nights", c.WeekendNights.UnlockAt, weekend.UnlockAt, v => c.WeekendNights.UnlockAt = v);
        return changes;
    }

    private static string Norm(string? t) =>
        TimeParse.TryParse(t, out var ts) ? ts.ToString(@"hh\:mm") : t ?? "";
}
