using System.Text;

namespace FamilyTimeGuard.Core;

public sealed record ActivityEntry(string Time, string Child, string Event, string Detail);

/// <summary>Reads the activity log (logs\activity-yyyy-MM-dd.csv) and builds parent-friendly reports.</summary>
public static class Reports
{
    public static List<ActivityEntry> ParseActivityCsv(IEnumerable<string> lines)
    {
        var result = new List<ActivityEntry>();
        foreach (var line in lines.Skip(1))
        {
            var f = SplitCsv(line);
            if (f.Count >= 3) result.Add(new ActivityEntry(f[0], f[1], f[2], f.Count > 3 ? f[3] : ""));
        }
        return result;
    }

    internal static List<string> SplitCsv(string line)
    {
        var fields = new List<string>();
        var sb = new StringBuilder();
        var quoted = false;
        for (var i = 0; i < line.Length; i++)
        {
            var ch = line[i];
            if (quoted)
            {
                if (ch == '"' && i + 1 < line.Length && line[i + 1] == '"') { sb.Append('"'); i++; }
                else if (ch == '"') quoted = false;
                else sb.Append(ch);
            }
            else if (ch == '"') quoted = true;
            else if (ch == ',') { fields.Add(sb.ToString()); sb.Clear(); }
            else sb.Append(ch);
        }
        fields.Add(sb.ToString());
        return fields;
    }

    /// <summary>Short status line used by /status.</summary>
    public static string Status(ChildProfile child, TickResult? live, UsageDay usage, DailyBudget budget, bool online, bool locked, bool paused)
    {
        var sb = new StringBuilder();
        sb.Append(child.Name).Append(": ");
        if (live is null) sb.Append(online ? "not signed in right now." : "computer is off or offline.");
        else if (locked) sb.Append("locked by you.");
        else if (live.ScreenTimeUp) sb.Append("screen time is used up for today.");
        else if (live.Bedtime) sb.Append($"bedtime, unlocks at {live.UnlockAtLocal:HH:mm}.");
        else sb.Append(live.CountingGames ? "playing a game now." : live.CountingYouTube ? "watching YouTube now." : "signed in.");
        sb.AppendLine();
        var gamesBudget = budget.GamesMinutes < 0 ? -1 : budget.GamesMinutes + usage.BonusGamesMinutes;
        var ytBudget = budget.YouTubeMinutes < 0 ? -1 : budget.YouTubeMinutes + usage.BonusYouTubeMinutes;
        sb.AppendLine($"Games: {Used(usage.GamesSeconds)} used, {Left(gamesBudget, usage.GamesSeconds)}");
        sb.AppendLine($"YouTube: {Used(usage.YouTubeSeconds)} used, {Left(ytBudget, usage.YouTubeSeconds)}");
        if (budget.ScreenMinutes >= 0)
            sb.AppendLine($"Screen time: {Used(usage.ScreenSeconds)} used, {Left(budget.ScreenMinutes + usage.BonusScreenMinutes, usage.ScreenSeconds)}");
        if (paused) sb.AppendLine("Rules are paused.");
        else if (live?.NextBedtimeLocal is { } bed) sb.AppendLine($"Bedtime: {bed:HH:mm}");
        return sb.ToString().TrimEnd();
    }

    /// <summary>Evening summary / "/today".</summary>
    public static string Today(ChildProfile child, UsageDay usage, DailyBudget budget, IReadOnlyList<ActivityEntry> entries)
    {
        var mine = entries.Where(e => e.Child.Equals(child.Name, StringComparison.OrdinalIgnoreCase)).ToList();
        var sb = new StringBuilder();
        sb.AppendLine($"{child.Name} today ({usage.Date})");
        var gamesBudget = budget.GamesMinutes < 0 ? -1 : budget.GamesMinutes + usage.BonusGamesMinutes;
        var ytBudget = budget.YouTubeMinutes < 0 ? -1 : budget.YouTubeMinutes + usage.BonusYouTubeMinutes;
        sb.AppendLine($"Games: {Used(usage.GamesSeconds)}{Of(gamesBudget)}");
        sb.AppendLine($"YouTube: {Used(usage.YouTubeSeconds)}{Of(ytBudget)}");
        sb.AppendLine($"Screen time: {Used(usage.ScreenSeconds)}{Of(budget.ScreenMinutes < 0 ? -1 : budget.ScreenMinutes + usage.BonusScreenMinutes)}");
        if (usage.BonusGamesMinutes + usage.BonusYouTubeMinutes + usage.BonusScreenMinutes > 0)
            sb.AppendLine($"Extra time given: games +{usage.BonusGamesMinutes} min, YouTube +{usage.BonusYouTubeMinutes} min" +
                          (usage.BonusScreenMinutes > 0 ? $", screen +{usage.BonusScreenMinutes} min" : ""));

        var games = mine.Where(e => e.Event == "games-start" && e.Detail.Length > 0)
            .SelectMany(e => e.Detail.Split(' ', StringSplitOptions.RemoveEmptyEntries))
            .Select(g => g.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) ? g[..^4] : g)
            .Distinct(StringComparer.OrdinalIgnoreCase).Take(8).ToList();
        if (games.Count > 0) sb.AppendLine($"Played: {string.Join(", ", games)}");

        var videos = mine.Where(e => e.Event == "youtube").Select(e => CleanTitle(e.Detail))
            .Where(t => t.Length > 0 && !t.Equals("YouTube", StringComparison.OrdinalIgnoreCase))
            .Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        if (videos.Count > 0)
        {
            sb.AppendLine($"YouTube videos ({videos.Count}):");
            foreach (var v in videos.Take(10)) sb.AppendLine("• " + v);
            if (videos.Count > 10) sb.AppendLine($"…and {videos.Count - 10} more");
        }

        var screenLogoffs = mine.Count(e => e.Event == "screen-logoff");
        if (screenLogoffs > 0) sb.AppendLine($"Logged off because screen time was used up: {screenLogoffs} time(s)");
        var lateLogins = mine.Count(e => e.Event == "bedtime-logoff");
        if (lateLogins > 0) sb.AppendLine($"Logged off at bedtime: {lateLogins} time(s)");
        var restarts = mine.Count(e => e.Event == "helper-restarted");
        if (restarts > 0) sb.AppendLine($"Warning: the clock icon helper was stopped {restarts} time(s)");
        var wrongPins = mine.Count(e => e.Event == "parent-pin-wrong");
        if (wrongPins > 0) sb.AppendLine($"Wrong parent PIN entered {wrongPins} time(s)");
        return sb.ToString().TrimEnd();
    }

    /// <summary>"Funny cats - YouTube - Google Chrome" -> "Funny cats".</summary>
    public static string CleanTitle(string title)
    {
        var t = title.Trim();
        foreach (var suffix in new[] { " - Google Chrome", " - Microsoft\u200B Edge", " - Microsoft Edge", " — Mozilla Firefox", " - Mozilla Firefox", " - Brave", " - Opera" })
            if (t.EndsWith(suffix, StringComparison.OrdinalIgnoreCase)) t = t[..^suffix.Length];
        if (t.EndsWith(" - YouTube", StringComparison.OrdinalIgnoreCase)) t = t[..^" - YouTube".Length];
        if (t.StartsWith('(') && t.IndexOf(')') is var close and > 0 and < 6) t = t[(close + 1)..].TrimStart(); // "(3) title" notification count
        return t.Trim();
    }

    private static string Used(double seconds) => RemoteCommandParser.FormatDuration(TimeSpan.FromSeconds(Math.Round(seconds / 60) * 60));
    private static string Of(int budgetMinutes) => budgetMinutes < 0 ? " (no limit)" : $" of {RemoteCommandParser.FormatMinutes(budgetMinutes)}";

    private static string Left(int budgetMinutes, double usedSeconds)
    {
        if (budgetMinutes < 0) return "no limit";
        var left = Math.Max(0, budgetMinutes * 60 - usedSeconds);
        return left <= 0 ? "none left" : $"{RemoteCommandParser.FormatDuration(TimeSpan.FromSeconds(Math.Ceiling(left / 60) * 60))} left";
    }
}
