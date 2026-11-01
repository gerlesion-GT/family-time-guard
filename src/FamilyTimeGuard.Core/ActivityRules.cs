namespace FamilyTimeGuard.Core;

/// <summary>Which programs and websites count as "games".</summary>
public sealed class GameRules
{
    /// <summary>Program file names that are games, e.g. "RobloxPlayerBeta.exe".</summary>
    public List<string> ProcessNames { get; set; } = [];

    /// <summary>Any program whose file path contains one of these is a game (e.g. everything installed by Steam).</summary>
    public List<string> PathContains { get; set; } = [];

    /// <summary>Programs that are never counted (launchers, helpers, wallpaper apps...).</summary>
    public List<string> NeverCountProcessNames { get; set; } = [];

    /// <summary>Paths that are never counted even if they match <see cref="PathContains"/>.</summary>
    public List<string> NeverCountPathContains { get; set; } = [];

    /// <summary>Browser tab titles containing one of these count as game time (web games).</summary>
    public List<string> WebsiteTitleKeywords { get; set; } = [];

    public bool IsGameProcess(string processName, string? fullPath)
    {
        if (string.IsNullOrWhiteSpace(processName)) return false;
        var name = NormalizeExe(processName);
        if (NeverCountProcessNames.Any(n => NormalizeExe(n).Equals(name, StringComparison.OrdinalIgnoreCase))) return false;
        if (ProcessNames.Any(n => NormalizeExe(n).Equals(name, StringComparison.OrdinalIgnoreCase))) return true;
        if (string.IsNullOrEmpty(fullPath)) return false;
        if (NeverCountPathContains.Any(p => fullPath.Contains(p, StringComparison.OrdinalIgnoreCase))) return false;
        return PathContains.Any(p => fullPath.Contains(p, StringComparison.OrdinalIgnoreCase));
    }

    public bool IsGameWebsiteTitle(string? title) =>
        !string.IsNullOrEmpty(title) && WebsiteTitleKeywords.Any(k => title.Contains(k, StringComparison.OrdinalIgnoreCase));

    /// <summary>True if the program could be a game, so we need its full path to decide.</summary>
    public bool NeedsPathCheck(string processName) =>
        PathContains.Count > 0 && !NeverCountProcessNames.Any(n => NormalizeExe(n).Equals(NormalizeExe(processName), StringComparison.OrdinalIgnoreCase));

    internal static string NormalizeExe(string name)
    {
        name = name.Trim();
        return name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) ? name : name + ".exe";
    }

    public static GameRules CreateDefault() => new()
    {
        ProcessNames =
        [
            "RobloxPlayerBeta.exe",          // Roblox (playing)
            "Minecraft.Windows.exe",         // Minecraft Bedrock
            "MinecraftLauncher.exe",         // Minecraft Java launcher
            "Minecraft.exe",
            "javaw.exe",                     // Minecraft Java edition runs inside Java
            "FortniteClient-Win64-Shipping.exe",
            "GTA5.exe",
        ],
        PathContains =
        [
            @"\steamapps\common\",           // every game installed by Steam
            @"\Epic Games\",                 // every game installed by Epic
            @"\XboxGames\",                  // Xbox / Microsoft Store games
            @"\Riot Games\",
            @"\EA Games\",
            @"\Ubisoft Game Launcher\games\",
        ],
        NeverCountProcessNames =
        [
            "steam.exe", "steamwebhelper.exe", "steamservice.exe",
            "EpicGamesLauncher.exe", "EpicWebHelper.exe", "EpicOnlineServicesHost.exe",
            "wallpaper32.exe", "wallpaper64.exe", "webwallpaper32.exe", // Wallpaper Engine (runs all the time)
            "RobloxStudioBeta.exe",          // Roblox Studio = building/coding, encouraged
            "CrashReportClient.exe", "UnityCrashHandler64.exe", "UnityCrashHandler32.exe",
        ],
        NeverCountPathContains =
        [
            @"\Epic Games\Launcher\",
            @"\steamapps\common\Steamworks Shared\",
            @"\steamapps\common\wallpaper_engine\",
        ],
        WebsiteTitleKeywords =
        [
            "Poki", "CrazyGames", "Friv", "Y8 Games", "Miniclip", "Kizi", "Coolmath Games", "Agame", "Roblox",
        ],
    };
}

/// <summary>How YouTube is detected and blocked.</summary>
public sealed class YouTubeRules
{
    /// <summary>A browser window whose title contains one of these is counted as YouTube.</summary>
    public List<string> WindowTitleKeywords { get; set; } = [];

    /// <summary>Programs treated as web browsers.</summary>
    public List<string> BrowserProcessNames { get; set; } = [];

    /// <summary>Web addresses blocked once YouTube time is used up.</summary>
    public List<string> BlockDomains { get; set; } = [];

    public bool IsBrowser(string? processName) =>
        !string.IsNullOrEmpty(processName) &&
        BrowserProcessNames.Any(b => GameRules.NormalizeExe(b).Equals(GameRules.NormalizeExe(processName), StringComparison.OrdinalIgnoreCase));

    public bool IsYouTubeTitle(string? title) =>
        !string.IsNullOrEmpty(title) && WindowTitleKeywords.Any(k => title.Contains(k, StringComparison.OrdinalIgnoreCase));

    public static YouTubeRules CreateDefault() => new()
    {
        WindowTitleKeywords = ["YouTube"],
        BrowserProcessNames = ["chrome.exe", "msedge.exe", "firefox.exe", "brave.exe", "opera.exe", "vivaldi.exe", "iexplore.exe"],
        BlockDomains =
        [
            "youtube.com", "www.youtube.com", "m.youtube.com", "music.youtube.com", "youtu.be",
            "youtube-nocookie.com", "www.youtube-nocookie.com", "youtubei.googleapis.com",
            "youtube.googleapis.com", "yt3.ggpht.com", "i.ytimg.com", "s.ytimg.com",
        ],
    };
}
