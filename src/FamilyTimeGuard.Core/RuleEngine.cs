namespace FamilyTimeGuard.Core;

/// <summary>What the Guard sees in a child's session at one moment.</summary>
public sealed record Observation(
    bool GameProcessRunning,
    string? ForegroundTitle,
    string? ForegroundProcess,
    bool AgentConnected,
    bool SessionLocked,
    int IdleSeconds = 0);

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
    /// <summary>A parent locked the computer from the phone (enforced like bedtime).</summary>
    public bool ParentLocked { get; set; }
    /// <summary>Total screen time is used up for today (enforced like bedtime).</summary>
    public bool ScreenTimeUp { get; set; }
    public int ScreenBudgetSeconds { get; set; } = -1;
    public int ScreenLeftSeconds { get; set; } = -1;
    public bool CountingScreen { get; set; }
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

    /// <summary>Things worth telling the parent about, e.g. "games-used-up".</summary>
    public List<string> Events { get; } = [];
}

/// <summary>
/// The heart of the app: given what is happening and the rules, decide what to do.
/// Pure logic with no Windows calls, so it can be tested anywhere.
/// </summary>
public static class RuleEngine
{
    public static readonly TimeSpan MaxTickCredit = TimeSpan.FromSeconds(10);
    /// <summary>No keyboard/mouse for this long (and no game or video) = away from the computer.</summary>
    public const int IdleAfterSeconds = 300;
    private static readonly Random Rng = new();

    public static TickResult Tick(
        GuardConfig cfg,
        ChildProfile child,
        UsageDay usage,
        Observation obs,
        DateTime localNow,
        TimeSpan elapsed,
        bool paused,
        bool parentLocked = false)
    {
        if (parentLocked) paused = false; // an explicit lock wins over a pause
        var r = new TickResult { Child = child.Name, Paused = paused, ParentLocked = parentLocked };
        var today = DateOnly.FromDateTime(localNow);
        var budget = Schedule.BudgetFor(cfg, child, today);

        r.Bedtime = parentLocked || Schedule.IsBedtime(cfg, child, localNow);
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
        var screenBudgetMinutes = budget.ScreenMinutes < 0 ? -1 : budget.ScreenMinutes + usage.BonusScreenMinutes;
        var screenAlreadyUp = screenBudgetMinutes >= 0 && usage.ScreenSeconds >= screenBudgetMinutes * 60;
        var counting = !paused && !r.Bedtime && !screenAlreadyUp && !obs.SessionLocked;
        if (counting && playingGames) { usage.GamesSeconds += elapsed.TotalSeconds; r.CountingGames = true; }
        if (counting && fgYouTube) { usage.YouTubeSeconds += elapsed.TotalSeconds; r.CountingYouTube = true; }
        if (counting && (obs.IdleSeconds < IdleAfterSeconds || playingGames || fgYouTube))
        {
            usage.ScreenSeconds += elapsed.TotalSeconds;
            r.CountingScreen = true;
        }
        r.ScreenBudgetSeconds = budget.ScreenMinutes < 0 ? -1 : (budget.ScreenMinutes + usage.BonusScreenMinutes) * 60;
        r.ScreenLeftSeconds = r.ScreenBudgetSeconds < 0 ? -1 : Math.Max(0, (int)(r.ScreenBudgetSeconds - usage.ScreenSeconds));

        r.GamesBudgetSeconds = budget.GamesMinutes < 0 ? -1 : (budget.GamesMinutes + usage.BonusGamesMinutes) * 60;
        r.YouTubeBudgetSeconds = budget.YouTubeMinutes < 0 ? -1 : (budget.YouTubeMinutes + usage.BonusYouTubeMinutes) * 60;
        r.GamesLeftSeconds = r.GamesBudgetSeconds < 0 ? -1 : Math.Max(0, (int)(r.GamesBudgetSeconds - usage.GamesSeconds));
        r.YouTubeLeftSeconds = r.YouTubeBudgetSeconds < 0 ? -1 : Math.Max(0, (int)(r.YouTubeBudgetSeconds - usage.YouTubeSeconds));

        if (paused) return r; // a parent paused the rules: count nothing, block nothing

        // ---- Total screen time: when used up, the computer locks like at bedtime ----
        if (r.ScreenLeftSeconds == 0)
        {
            r.ScreenTimeUp = true;
            if (!r.Bedtime && WarnOnce(r, usage, true, "scr0", Notice.Toast, "Screen time is over for today",
                    "The computer will sign you out in 1 minute. You can shut it down now."))
                r.Events.Add("screen-used-up");
            r.Bedtime = true;
        }
        else if (r.ScreenLeftSeconds > 0 && r.CountingScreen)
        {
            var left = r.ScreenLeftSeconds;
            WarnOnce(r, usage, left <= 900 && left > 300, "scr15", Notice.Toast, "15 minutes of screen time left",
                $"Start finishing up, {child.Name}. The computer locks when screen time is used up.");
            WarnOnce(r, usage, left <= 300 && left > 60, "scr5", Notice.Toast, "5 minutes of screen time left",
                "Save your game and your work now.");
            WarnOnce(r, usage, left <= 60, "scr1", Notice.Big, "1 minute of screen time left",
                "Please save everything now. The computer will lock in a minute.");
        }

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
                if (WarnOnce(r, usage, playingGames, "games0", Notice.Big, "Game time is over for today",
                    $"Great playing, {child.Name}! Games are closed until tomorrow. Idea: {Suggest(child)}."))
                    r.Events.Add("games-used-up");
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
                if (WarnOnce(r, usage, fgYouTube, "yt0", Notice.Big, "YouTube time is over for today",
                    $"YouTube is closed until tomorrow. Idea: {Suggest(child)}."))
                    r.Events.Add("youtube-used-up");
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

    private static bool WarnOnce(TickResult r, UsageDay usage, bool condition, string flag, string kind, string title, string message)
    {
        if (!condition || usage.Flags.Contains(flag)) return false;
        usage.Flags.Add(flag);
        r.Notices.Add(new Notice(kind, title, message));
        return true;
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
        else if (what == "screen")
        {
            usage.BonusScreenMinutes += minutes;
            usage.Flags.RemoveWhere(f => f.StartsWith("scr", StringComparison.Ordinal));
        }
        else throw new ArgumentException($"Unknown bonus type '{what}'.");
    }

    private static string Suggest(ChildProfile child)
    {
        if (child.Suggestions.Count == 0) return "take a break and stretch";
        lock (Rng) return child.Suggestions[Rng.Next(child.Suggestions.Count)];
    }
}
