using System.Globalization;
using System.Text.RegularExpressions;

namespace FamilyTimeGuard.Core;

/// <summary>What the parent asked for in a phone message.</summary>
public abstract record RemoteCommand
{
    /// <summary>The child the command is about (null = the only child on this computer).</summary>
    public string? Child { get; init; }
}

public sealed record StatusCommand : RemoteCommand;
public sealed record TodayCommand : RemoteCommand;
public sealed record HelpCommand : RemoteCommand;
public sealed record RulesCommand : RemoteCommand;
public sealed record BonusCommand(string Kind, int Minutes) : RemoteCommand;               // Kind: games | youtube | screen
public sealed record SetBudgetCommand(string DayType, string Kind, int Minutes) : RemoteCommand; // DayType: school | weekend
public sealed record SetBedtimeCommand(string NightType, string Which, string Time) : RemoteCommand; // Which: lock | unlock
public sealed record PauseCommand(TimeSpan Duration) : RemoteCommand;
public sealed record ResumeCommand : RemoteCommand;
public sealed record LockCommand(TimeSpan? Duration) : RemoteCommand;                       // null = until tomorrow morning
public sealed record UnlockCommand : RemoteCommand;
public sealed record PairCommand(string Code) : RemoteCommand;
public sealed record InvalidCommand(string Problem) : RemoteCommand;

/// <summary>Turns messages like "/bonus games 30" into commands. Pure logic, unit-tested.</summary>
public static class RemoteCommandParser
{
    public const string Help = """
        Commands:
        /status – time left and bedtime
        /today – what was played and watched today
        /rules – current daily limits and bedtimes
        /bonus games 30 – extra game minutes today
        /bonus youtube 15 – extra YouTube minutes today
        /bonus screen 30 – extra total computer time today
        /set school games 60 – change the daily limit
           (school or weekend; games, youtube or screen; minutes, -1 = no limit)
        /set school screen 180 – total computer time per day
           (when used up, the computer locks like at bedtime)
        /bedtime school 21:30 – change bedtime (school or weekend nights)
        /wakeup school 06:30 – change the morning unlock time
        /pause 2h – pause all rules (e.g. 30m, 2h, 1h30m)
        /resume – rules active again
        /lock – lock the computer now until tomorrow morning
        /lock 1h – lock for a while
        /unlock – unlock

        If this computer has more than one child, add the name first, e.g. /bonus Chip games 30
        """;

    private static readonly string[] GamesWords = ["games", "game", "g"];
    private static readonly string[] YouTubeWords = ["youtube", "yt", "y", "video", "videos"];
    private static readonly string[] ScreenWords = ["screen", "pc", "computer", "total", "all"];
    private static readonly string[] SchoolWords = ["school", "schoolday", "schooldays", "weekday", "weekdays", "s"];
    private static readonly string[] WeekendWords = ["weekend", "weekends", "holiday", "w"];

    public static RemoteCommand Parse(string? text, IReadOnlyCollection<string> childNames)
    {
        if (string.IsNullOrWhiteSpace(text)) return new InvalidCommand("Empty message.");
        var parts = text.Trim().Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries).ToList();
        var cmd = parts[0].ToLowerInvariant();
        if (!cmd.StartsWith('/')) return new InvalidCommand("Commands start with /. Send /help to see them.");
        cmd = cmd[1..];
        var at = cmd.IndexOf('@'); // "/status@my_bot" in group-style syntax
        if (at >= 0) cmd = cmd[..at];
        parts.RemoveAt(0);

        // Optional child name as first argument.
        string? child = null;
        if (parts.Count > 0 && childNames.FirstOrDefault(n => n.Equals(parts[0], StringComparison.OrdinalIgnoreCase)) is { } name)
        {
            child = name;
            parts.RemoveAt(0);
        }

        RemoteCommand result = cmd switch
        {
            "start" or "help" => new HelpCommand(),
            "status" => new StatusCommand(),
            "today" or "report" => new TodayCommand(),
            "rules" or "limits" => new RulesCommand(),
            "bonus" or "extra" or "add" => ParseBonus(parts),
            "set" or "limit" => ParseSet(parts),
            "bedtime" or "lockat" => ParseBedtime(parts, "lock"),
            "wakeup" or "unlockat" or "wake" => ParseBedtime(parts, "unlock"),
            "pause" => parts.Count == 1 && TryParseDuration(parts[0], out var d) && d > TimeSpan.Zero && d <= TimeSpan.FromHours(24)
                ? new PauseCommand(d)
                : new InvalidCommand("Example: /pause 2h  (between 1m and 24h)"),
            "resume" => new ResumeCommand(),
            "lock" => parts.Count == 0 ? new LockCommand(null)
                : parts.Count == 1 && TryParseDuration(parts[0], out var l) && l > TimeSpan.Zero && l <= TimeSpan.FromDays(2)
                    ? new LockCommand(l)
                    : new InvalidCommand("Example: /lock  or  /lock 1h"),
            "unlock" => new UnlockCommand(),
            "pair" => parts.Count == 1 ? new PairCommand(parts[0]) : new InvalidCommand("Example: /pair 123456"),
            _ => new InvalidCommand($"I don't know /{cmd}. Send /help to see the commands."),
        };
        return result with { Child = child };
    }

    private static RemoteCommand ParseBonus(List<string> p)
    {
        // /bonus games 30   or   /bonus 30 games
        if (p.Count == 2)
        {
            var (kindWord, numWord) = int.TryParse(p[0], out _) ? (p[1], p[0]) : (p[0], p[1]);
            if (Kind(kindWord) is { } kind && int.TryParse(numWord, out var m) && m is > 0 and <= 600)
                return new BonusCommand(kind, m);
        }
        return new InvalidCommand("Example: /bonus games 30  or  /bonus youtube 15  or  /bonus screen 30");
    }

    private static RemoteCommand ParseSet(List<string> p)
    {
        // /set school games 60
        if (p.Count == 3 && DayType(p[0]) is { } day && Kind(p[1]) is { } kind && int.TryParse(p[2], out var m) && m is >= -1 and <= 24 * 60)
            return new SetBudgetCommand(day, kind, m);
        return new InvalidCommand("Example: /set school games 60  or  /set weekend youtube 90  or  /set school screen 180  (-1 = no limit)");
    }

    private static RemoteCommand ParseBedtime(List<string> p, string which)
    {
        if (p.Count == 2 && DayType(p[0]) is { } night && TimeParse.TryParse(p[1], out var t))
            return new SetBedtimeCommand(night, which, t.ToString(@"hh\:mm", CultureInfo.InvariantCulture));
        return new InvalidCommand(which == "lock"
            ? "Example: /bedtime school 21:30  or  /bedtime weekend 22:30"
            : "Example: /wakeup school 06:30  or  /wakeup weekend 07:30");
    }

    private static string? Kind(string w)
    {
        w = w.ToLowerInvariant();
        return GamesWords.Contains(w) ? "games" : YouTubeWords.Contains(w) ? "youtube" : ScreenWords.Contains(w) ? "screen" : null;
    }

    public static string KindName(string kind) => kind switch { "games" => "games", "youtube" => "YouTube", _ => "screen time" };

    private static string? DayType(string w) =>
        SchoolWords.Contains(w.ToLowerInvariant()) ? "school" : WeekendWords.Contains(w.ToLowerInvariant()) ? "weekend" : null;

    /// <summary>Accepts "30m", "2h", "1h30m", "90" (minutes).</summary>
    public static bool TryParseDuration(string s, out TimeSpan duration)
    {
        duration = TimeSpan.Zero;
        s = s.Trim().ToLowerInvariant();
        if (int.TryParse(s, out var mins)) { duration = TimeSpan.FromMinutes(mins); return mins > 0; }
        var m = Regex.Match(s, @"^(?:(\d+)\s*h)?\s*(?:(\d+)\s*m(?:in)?)?$");
        if (!m.Success || (m.Groups[1].Length == 0 && m.Groups[2].Length == 0)) return false;
        var h = m.Groups[1].Length > 0 ? int.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture) : 0;
        var mm = m.Groups[2].Length > 0 ? int.Parse(m.Groups[2].Value, CultureInfo.InvariantCulture) : 0;
        duration = new TimeSpan(h, mm, 0);
        return duration > TimeSpan.Zero;
    }

    public static string FormatDuration(TimeSpan d) =>
        d.TotalMinutes < 60 ? $"{(int)Math.Round(d.TotalMinutes)} min"
        : d.Minutes == 0 ? $"{(int)d.TotalHours}h" : $"{(int)d.TotalHours}h {d.Minutes:00}m";

    public static string FormatMinutes(int minutes) =>
        minutes < 0 ? "no limit" : FormatDuration(TimeSpan.FromMinutes(minutes));
}

/// <summary>Applies a rules change from the phone to the settings. Returns a confirmation message.</summary>
public static class RemoteConfigEditor
{
    public static string Apply(GuardConfig cfg, ChildProfile child, SetBudgetCommand c)
    {
        var budget = c.DayType == "school" ? child.SchoolDayBudget : child.WeekendBudget;
        switch (c.Kind)
        {
            case "games": budget.GamesMinutes = c.Minutes; break;
            case "youtube": budget.YouTubeMinutes = c.Minutes; break;
            default: budget.ScreenMinutes = c.Minutes; break;
        }
        var days = c.DayType == "school" ? "school days" : "weekends";
        return c.Kind == "screen"
            ? $"{child.Name}: total screen time on {days} is now {RemoteCommandParser.FormatMinutes(c.Minutes)} per day." +
              (c.Minutes >= 0 ? " When it is used up, the computer locks until tomorrow (like bedtime)." : "")
            : $"{child.Name}: {RemoteCommandParser.KindName(c.Kind)} on {days} is now {RemoteCommandParser.FormatMinutes(c.Minutes)} per day.";
    }

    public static string Apply(GuardConfig cfg, ChildProfile child, SetBedtimeCommand c)
    {
        var night = c.NightType == "school" ? child.SchoolNights : child.WeekendNights;
        var oldLock = night.LockAt;
        var oldUnlock = night.UnlockAt;
        if (c.Which == "lock") night.LockAt = c.Time; else night.UnlockAt = c.Time;
        var errors = new List<string>();
        night.Validate(child.Name, errors);
        if (errors.Count > 0)
        {
            night.LockAt = oldLock;
            night.UnlockAt = oldUnlock;
            throw new ConfigException(string.Join(" ", errors));
        }
        var which = c.NightType == "school" ? "school nights (Sun–Thu)" : "weekend nights (Fri–Sat)";
        return c.Which == "lock"
            ? $"{child.Name}: bedtime on {which} is now {c.Time}."
            : $"{child.Name}: the computer now unlocks at {c.Time} after {which}.";
    }

    public static string DescribeRules(GuardConfig cfg, ChildProfile child) =>
        $"""
        {child.Name}'s rules
        School days: games {RemoteCommandParser.FormatMinutes(child.SchoolDayBudget.GamesMinutes)}, YouTube {RemoteCommandParser.FormatMinutes(child.SchoolDayBudget.YouTubeMinutes)}, screen {RemoteCommandParser.FormatMinutes(child.SchoolDayBudget.ScreenMinutes)}
        Weekends: games {RemoteCommandParser.FormatMinutes(child.WeekendBudget.GamesMinutes)}, YouTube {RemoteCommandParser.FormatMinutes(child.WeekendBudget.YouTubeMinutes)}, screen {RemoteCommandParser.FormatMinutes(child.WeekendBudget.ScreenMinutes)}
        School nights (Sun–Thu): lock {child.SchoolNights.LockAt}, unlock {child.SchoolNights.UnlockAt}
        Weekend nights (Fri–Sat): lock {child.WeekendNights.LockAt}, unlock {child.WeekendNights.UnlockAt}
        """;
}
