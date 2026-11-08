using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace FamilyTimeGuard.Core;

/// <summary>
/// All settings for Family Time Guard. Stored as JSON in
/// C:\ProgramData\FamilyTimeGuard\config.json (only administrators can edit it).
/// </summary>
public sealed class GuardConfig
{
    /// <summary>Time zone used for all rules. Kids can change the Windows time zone, so we never rely on it.</summary>
    public string TimeZone { get; set; } = "Asia/Ho_Chi_Minh";

    /// <summary>Days that use the "weekend" budgets. Nights before these days use the "weekend night" bedtime.</summary>
    public List<string> WeekendDays { get; set; } = ["Saturday", "Sunday"];

    /// <summary>Extra dates (yyyy-MM-dd) treated like a weekend day, e.g. school holidays.</summary>
    public List<string> HolidayDates { get; set; } = [];

    /// <summary>Hashed parent PIN (never stored in plain text). Set with: FamilyTimeGuard.exe set-pin</summary>
    public string? ParentPinHash { get; set; }

    public List<ChildProfile> Children { get; set; } = [];

    public GameRules Games { get; set; } = GameRules.CreateDefault();

    public YouTubeRules YouTube { get; set; } = YouTubeRules.CreateDefault();

    /// <summary>Phone remote through a private Telegram bot (one bot per computer).</summary>
    public TelegramSettings Telegram { get; set; } = new();

    /// <summary>Children whose Windows accounts are on this computer.</summary>
    public List<ChildProfile> ChildrenOnThisPc() => Children.Where(c => c.WindowsAccounts.Count > 0).ToList();

    public ChildProfile? FindChildByAccount(string? windowsAccount)
    {
        if (string.IsNullOrWhiteSpace(windowsAccount)) return null;
        return Children.FirstOrDefault(c =>
            c.WindowsAccounts.Any(a => string.Equals(a.Trim(), windowsAccount.Trim(), StringComparison.OrdinalIgnoreCase)));
    }

    public ChildProfile? FindChildByName(string? name) =>
        Children.FirstOrDefault(c => string.Equals(c.Name, name, StringComparison.OrdinalIgnoreCase));

    public bool IsWeekendLike(DateOnly date)
    {
        if (HolidayDates.Any(d => DateOnly.TryParseExact(d.Trim(), "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var h) && h == date))
            return true;
        return WeekendDays.Any(d => Enum.TryParse<DayOfWeek>(d.Trim(), true, out var dow) && dow == date.DayOfWeek);
    }

    // ---------- Serialization ----------

    public static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
        PropertyNameCaseInsensitive = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.Never,
    };

    public static GuardConfig Parse(string json)
    {
        var cfg = JsonSerializer.Deserialize<GuardConfig>(json, JsonOptions)
                  ?? throw new ConfigException("The settings file is empty.");
        var errors = cfg.Validate();
        if (errors.Count > 0) throw new ConfigException(string.Join(Environment.NewLine, errors));
        return cfg;
    }

    public string ToJson() => JsonSerializer.Serialize(this, JsonOptions);

    /// <summary>Returns a list of human-readable problems (empty list = settings are fine).</summary>
    public List<string> Validate()
    {
        var errors = new List<string>();
        if (!string.IsNullOrWhiteSpace(Telegram.DailySummaryAt) && !TimeParse.TryParse(Telegram.DailySummaryAt, out _))
            errors.Add($"telegram.dailySummaryAt '{Telegram.DailySummaryAt}' must look like 21:30.");
        try { TimeZones.Find(TimeZone); }
        catch (Exception) { errors.Add($"Unknown timeZone '{TimeZone}'."); }

        foreach (var d in WeekendDays)
            if (!Enum.TryParse<DayOfWeek>(d, true, out _)) errors.Add($"weekendDays: '{d}' is not a day name (use e.g. \"Saturday\").");
        foreach (var d in HolidayDates)
            if (!DateOnly.TryParseExact(d, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out _))
                errors.Add($"holidayDates: '{d}' must look like 2026-12-31.");

        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var c in Children)
        {
            if (string.IsNullOrWhiteSpace(c.Name)) errors.Add("Every child needs a name.");
            else if (!names.Add(c.Name)) errors.Add($"Child name '{c.Name}' is used twice.");
            c.Validate(errors);
        }
        return errors;
    }

    // ---------- Defaults for this family ----------

    public static GuardConfig CreateDefault() => new()
    {
        Children =
        [
            new ChildProfile
            {
                Name = "Boong",
                SchoolNights = new NightRule { LockAt = "22:00", UnlockAt = "06:30" },
                WeekendNights = new NightRule { LockAt = "23:00", UnlockAt = "07:00" },
                SchoolDayBudget = new DailyBudget { GamesMinutes = 60, YouTubeMinutes = 45 },
                WeekendBudget = new DailyBudget { GamesMinutes = 150, YouTubeMinutes = 90 },
            },
            new ChildProfile
            {
                Name = "Chip",
                SchoolNights = new NightRule { LockAt = "21:00", UnlockAt = "06:30" },
                WeekendNights = new NightRule { LockAt = "22:00", UnlockAt = "07:00" },
                SchoolDayBudget = new DailyBudget { GamesMinutes = 45, YouTubeMinutes = 30 },
                WeekendBudget = new DailyBudget { GamesMinutes = 120, YouTubeMinutes = 60 },
            },
        ],
    };
}

public sealed class ConfigException(string message) : Exception(message);

public sealed class TelegramSettings
{
    /// <summary>The bot's secret key, encrypted for this computer. Set by the installer, never typed here.</summary>
    public string? EncryptedBotToken { get; set; }

    /// <summary>The bot's public name, e.g. "chip_pc_guard_bot" (for display only).</summary>
    public string? BotUsername { get; set; }

    /// <summary>The only Telegram user allowed to control this computer (the parent). Set by pairing.</summary>
    public long? OwnerUserId { get; set; }

    /// <summary>The private chat between the parent and the bot.</summary>
    public long? OwnerChatId { get; set; }

    /// <summary>Name used in messages, e.g. "Chip's PC".</summary>
    public string PcName { get; set; } = "Kid's PC";

    /// <summary>Local time for the evening summary. Empty = no summary.</summary>
    public string DailySummaryAt { get; set; } = "21:30";

    /// <summary>Optional proxy for reaching Telegram, e.g. "http://192.168.0.10:8080".</summary>
    public string? Proxy { get; set; }

    public bool Alerts { get; set; } = true;

    public bool IsReady => !string.IsNullOrEmpty(EncryptedBotToken) && OwnerUserId is not null && OwnerChatId is not null;
}

public sealed class ChildProfile
{
    public string Name { get; set; } = "";

    /// <summary>Windows account names (user names) that belong to this child.</summary>
    public List<string> WindowsAccounts { get; set; } = [];

    /// <summary>Nights before a school day (normally Sunday–Thursday nights).</summary>
    public NightRule SchoolNights { get; set; } = new();

    /// <summary>Nights before a weekend day or holiday (normally Friday and Saturday nights).</summary>
    public NightRule WeekendNights { get; set; } = new() { LockAt = "23:00", UnlockAt = "07:00" };

    public DailyBudget SchoolDayBudget { get; set; } = new();
    public DailyBudget WeekendBudget { get; set; } = new() { GamesMinutes = 120, YouTubeMinutes = 60 };

    /// <summary>Friendly ideas shown when fun time runs out.</summary>
    public List<string> Suggestions { get; set; } =
    [
        "Try a Scratch coding project (scratch.mit.edu)",
        "Build something in Roblox Studio instead of playing",
        "Practice typing at typing.com",
        "Read a book or draw something",
        "Go outside or help with dinner",
        "Learn something new on Khan Academy",
    ];

    internal void Validate(List<string> errors)
    {
        SchoolNights.Validate($"{Name}.schoolNights", errors);
        WeekendNights.Validate($"{Name}.weekendNights", errors);
    }
}

public sealed class NightRule
{
    /// <summary>"HH:mm" when the computer locks (e.g. "21:30"). Times before 12:00 mean after midnight.</summary>
    public string LockAt { get; set; } = "22:00";

    /// <summary>"HH:mm" when the computer unlocks the next morning.</summary>
    public string UnlockAt { get; set; } = "06:30";

    [JsonIgnore] public TimeSpan Lock => TimeParse.Parse(LockAt);
    [JsonIgnore] public TimeSpan Unlock => TimeParse.Parse(UnlockAt);

    internal void Validate(string where, List<string> errors)
    {
        if (!TimeParse.TryParse(LockAt, out var l)) { errors.Add($"{where}.lockAt '{LockAt}' must look like 21:30."); return; }
        if (!TimeParse.TryParse(UnlockAt, out var u)) { errors.Add($"{where}.unlockAt '{UnlockAt}' must look like 06:30."); return; }
        if (u >= TimeSpan.FromHours(12)) errors.Add($"{where}.unlockAt must be in the morning (before 12:00).");
        if (l < TimeSpan.FromHours(12) && l >= u) errors.Add($"{where}: lockAt after midnight must be earlier than unlockAt.");
    }
}

public sealed class DailyBudget
{
    /// <summary>Minutes of games per day. -1 = unlimited, 0 = no games.</summary>
    public int GamesMinutes { get; set; } = 60;

    /// <summary>Minutes of YouTube per day. -1 = unlimited, 0 = no YouTube.</summary>
    public int YouTubeMinutes { get; set; } = 30;
}

public static class TimeParse
{
    public static bool TryParse(string? s, out TimeSpan t) =>
        TimeSpan.TryParseExact(s?.Trim(), [@"h\:mm", @"hh\:mm"], CultureInfo.InvariantCulture, out t)
        && t >= TimeSpan.Zero && t < TimeSpan.FromDays(1);

    public static TimeSpan Parse(string s) =>
        TryParse(s, out var t) ? t : throw new ConfigException($"'{s}' is not a valid time (use HH:mm).");
}

public static class TimeZones
{
    public static TimeZoneInfo Find(string id)
    {
        try { return TimeZoneInfo.FindSystemTimeZoneById(id); }
        catch (Exception) when (id is "Asia/Ho_Chi_Minh" or "Asia/Saigon")
        {
            try { return TimeZoneInfo.FindSystemTimeZoneById("SE Asia Standard Time"); }
            catch (Exception) { return TimeZoneInfo.CreateCustomTimeZone("UTC+7", TimeSpan.FromHours(7), "UTC+7", "UTC+7"); }
        }
    }
}
