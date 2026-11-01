namespace FamilyTimeGuard.Core;

/// <summary>What the Guard sees in a child's session at one moment.</summary>
public sealed record Observation(
    bool GameProcessRunning,
    string? ForegroundTitle,
    string? ForegroundProcess,
    bool AgentConnected,
    bool SessionLocked);

public sealed record Notice(string Kind, string Title, string Message)
{
    public const string Toast = "toast";
    public const string Big = "big";
}

/// <summary>What the Guard should do after one check.</summary>
public sealed class TickResult
{
    public string Child { get; set; } = "";
    public bool Paused { get; set; }
    public bool Bedtime { get; set; }
    public DateTime? NextBedtimeLocal { get; set; }
    public DateTime? UnlockAtLocal { get; set; }

    public int GamesBudgetSeconds { get; set; }    // -1 = unlimited
    public int GamesLeftSeconds { get; set; }      // -1 = unlimited
    public int YouTubeBudgetSeconds { get; set; }
    public int YouTubeLeftSeconds { get; set; }

    public bool CountingGames { get; set; }
    public bool CountingYouTube { get; set; }

    public bool KillGames { get; set; }
    public bool CloseForegroundTab { get; set; }
    public bool BlockYouTube { get; set; }

    public List<Notice> Notices { get; } = [];
}

/// <summary>
/// The heart of the app: given what is happening and the rules, decide what to do.
/// Pure logic with no Windows calls, so it can be tested anywhere.
/// </summary>
public static class RuleEngine
{
    public static readonly TimeSpan MaxTickCredit = TimeSpan.FromSeconds(10);
    private static readonly Random Rng = new();

    public static TickResult Tick(
        GuardConfig cfg,
        ChildProfile child,
        UsageDay usage,
        Observation obs,
        DateTime localNow,
        TimeSpan elapsed,
        bool paused)
    {
        var r = new TickResult { Child = child.Name, Paused = paused };
        var today = DateOnly.FromDateTime(localNow);
        var budget = Schedule.BudgetFor(cfg, child, today);

        r.Bedtime = Schedule.IsBedtime(cfg, child, localNow);
        r.NextBedtimeLocal = Schedule.NextBedtime(cfg, child, localNow);
        r.UnlockAtLocal = Schedule.UnlockTime(cfg, child, localNow);

        // ---- What is the child doing right now? ----
        var fgIsBrowser = cfg.YouTube.IsBrowser(obs.ForegroundProcess);
        var fgYouTube = fgIsBrowser && cfg.YouTube.IsYouTubeTitle(obs.ForegroundTitle);
        var fgGameSite = fgIsBrowser && !fgYouTube && cfg.Games.IsGameWebsiteTitle(obs.ForegroundTitle);
        var playingGames = obs.GameProcessRunning || fgGameSite;

        // ---- Count time ----
        if (elapsed < TimeSpan.Zero) elapsed = TimeSpan.Zero;
        if (elapsed > MaxTickCredit) elapsed = MaxTickCredit; // e.g. after sleep/hibernate
        var counting = !paused && !r.Bedtime && !obs.SessionLocked;
        if (counting && playingGames) { usage.GamesSeconds += elapsed.TotalSeconds; r.CountingGames = true; }
        if (counting && fgYouTube) { usage.YouTubeSeconds += elapsed.TotalSeconds; r.CountingYouTube = true; }

        r.GamesBudgetSeconds = budget.GamesMinutes < 0 ? -1 : (budget.GamesMinutes + usage.BonusGamesMinutes) * 60;
        r.YouTubeBudgetSeconds = budget.YouTubeMinutes < 0 ? -1 : (budget.YouTubeMinutes + usage.BonusYouTubeMinutes) * 60;
        r.GamesLeftSeconds = r.GamesBudgetSeconds < 0 ? -1 : Math.Max(0, (int)(r.GamesBudgetSeconds - usage.GamesSeconds));
        r.YouTubeLeftSeconds = r.YouTubeBudgetSeconds < 0 ? -1 : Math.Max(0, (int)(r.YouTubeBudgetSeconds - usage.YouTubeSeconds));

        if (paused) return r; // a parent paused the rules: count nothing, block nothing

        // ---- Bedtime warnings (the actual log-off is done by the service with a countdown) ----
        if (r.NextBedtimeLocal is { } bed)
        {
            var mins = (bed - localNow).TotalMinutes;
            var at = bed.ToString("HH:mm");
            WarnOnce(r, usage, mins <= 15 && mins > 5, "bed15", Notice.Toast, "Bedtime soon",
                $"The computer locks at {at} (in about 15 minutes). Start finishing up, {child.Name}.");
            WarnOnce(r, usage, mins <= 5 && mins > 1, "bed5", Notice.Toast, "Bedtime in 5 minutes",
                $"Save your game and your work now. The computer locks at {at}.");
            WarnOnce(r, usage, mins <= 1, "bed1", Notice.Big, "Bedtime in 1 minute",
                "Please save everything now. Good night!");
        }

        // ---- Games ----
        if (r.GamesLeftSeconds >= 0)
        {
            if (r.GamesLeftSeconds == 0)
            {
                if (playingGames)
                {
                    r.KillGames = obs.GameProcessRunning;
                    r.CloseForegroundTab |= fgGameSite;
                }
                WarnOnce(r, usage, playingGames, "games0", Notice.Big, "Game time is over for today",
                    $"Great playing, {child.Name}! Games are closed until tomorrow. Idea: {Suggest(child)}.");
            }
            else if (playingGames)
            {
                WarnOnce(r, usage, r.GamesLeftSeconds <= 120, "games2", Notice.Toast, "2 minutes of game time left",
                    "Save your game now so you don't lose progress.");
                WarnOnce(r, usage, r.GamesLeftSeconds <= 600 && r.GamesLeftSeconds > 120, "games10", Notice.Toast,
                    "10 minutes of game time left", "Find a good place to stop soon.");
            }
        }

        // ---- YouTube ----
        if (r.YouTubeLeftSeconds >= 0)
        {
            if (r.YouTubeLeftSeconds == 0)
            {
                r.BlockYouTube = true;
                r.CloseForegroundTab |= fgYouTube;
                WarnOnce(r, usage, fgYouTube, "yt0", Notice.Big, "YouTube time is over for today",
                    $"YouTube is closed until tomorrow. Idea: {Suggest(child)}.");
            }
            else if (fgYouTube)
            {
                WarnOnce(r, usage, r.YouTubeLeftSeconds <= 120, "yt2", Notice.Toast, "2 minutes of YouTube left",
                    "Time to finish this video.");
                WarnOnce(r, usage, r.YouTubeLeftSeconds <= 600 && r.YouTubeLeftSeconds > 120, "yt10", Notice.Toast,
                    "10 minutes of YouTube left", "Pick your last video or two.");
            }
        }

        // Without the helper app we cannot see YouTube, so keep it blocked until the helper is back.
        if (!obs.AgentConnected) r.BlockYouTube = true;

        return r;
    }

    private static void WarnOnce(TickResult r, UsageDay usage, bool condition, string flag, string kind, string title, string message)
    {
        if (!condition || usage.Flags.Contains(flag)) return;
        usage.Flags.Add(flag);
        r.Notices.Add(new Notice(kind, title, message));
    }

    /// <summary>A parent gave extra time: allow the warnings to show again.</summary>
    public static void AddBonus(UsageDay usage, string what, int minutes)
    {
        if (what == "games")
        {
            usage.BonusGamesMinutes += minutes;
            usage.Flags.RemoveWhere(f => f.StartsWith("games", StringComparison.Ordinal));
        }
        else if (what == "youtube")
        {
            usage.BonusYouTubeMinutes += minutes;
            usage.Flags.RemoveWhere(f => f.StartsWith("yt", StringComparison.Ordinal));
        }
        else throw new ArgumentException($"Unknown bonus type '{what}'.");
    }

    private static string Suggest(ChildProfile child)
    {
        if (child.Suggestions.Count == 0) return "take a break and stretch";
        lock (Rng) return child.Suggestions[Rng.Next(child.Suggestions.Count)];
    }
}
