using System.Text.Json;

namespace FamilyTimeGuard.Core;

/// <summary>What one child used on one day.</summary>
public sealed class UsageDay
{
    public string Child { get; set; } = "";
    public string Date { get; set; } = "";              // yyyy-MM-dd (local)
    public double GamesSeconds { get; set; }
    public double YouTubeSeconds { get; set; }
    public int BonusGamesMinutes { get; set; }
    public int BonusYouTubeMinutes { get; set; }
    public double ScreenSeconds { get; set; }
    public int BonusScreenMinutes { get; set; }

    /// <summary>"Ask for more time" requests sent today.</summary>
    public int RequestsToday { get; set; }

    /// <summary>Warnings already shown today, so they are not repeated.</summary>
    public HashSet<string> Flags { get; set; } = [];
}

/// <summary>Settings changed at run time by a parent (e.g. "pause rules for 1 hour").</summary>
public sealed class RuntimeState
{
    public DateTimeOffset? PausedUntilUtc { get; set; }

    /// <summary>Children locked by a parent from the phone, until the given time (UTC).</summary>
    public Dictionary<string, DateTimeOffset> LockedUntilUtc { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Telegram: the last update already handled.</summary>
    public long TelegramOffset { get; set; }

    /// <summary>Telegram: last date (yyyy-MM-dd) the evening summary was sent.</summary>
    public string? LastSummaryDate { get; set; }

    public bool IsLocked(string child, DateTimeOffset nowUtc) =>
        LockedUntilUtc.TryGetValue(child, out var until) && until > nowUtc;

    public bool IsPaused(DateTimeOffset nowUtc) => PausedUntilUtc is { } p && p > nowUtc;
}

/// <summary>Saves usage as small JSON files: usage\{child}\{date}.json</summary>
public sealed class UsageStore(string directory)
{
    private readonly Dictionary<string, UsageDay> _cache = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _dirty = new(StringComparer.OrdinalIgnoreCase);

    public UsageDay Get(string child, DateOnly date)
    {
        var key = $"{child}|{date:yyyy-MM-dd}";
        if (_cache.TryGetValue(key, out var day)) return day;

        day = new UsageDay { Child = child, Date = date.ToString("yyyy-MM-dd") };
        var path = PathFor(child, date);
        try
        {
            if (File.Exists(path))
                day = JsonSerializer.Deserialize<UsageDay>(File.ReadAllText(path), GuardConfig.JsonOptions) ?? day;
        }
        catch (Exception) { /* corrupt file: start fresh for the day */ }

        // Keep only today-ish entries in memory.
        foreach (var old in _cache.Keys.Where(k => k.StartsWith(child + "|", StringComparison.OrdinalIgnoreCase)).ToList())
        {
            if (_dirty.Contains(old)) Save(_cache[old]);
            _cache.Remove(old);
            _dirty.Remove(old);
        }
        _cache[key] = day;
        return day;
    }

    public void MarkDirty(UsageDay day) => _dirty.Add($"{day.Child}|{day.Date}");

    public void Flush()
    {
        foreach (var key in _dirty.ToList())
            if (_cache.TryGetValue(key, out var day)) Save(day);
        _dirty.Clear();
    }

    public IEnumerable<UsageDay> Recent(string child, int days, DateOnly today)
    {
        for (var i = 0; i < days; i++) yield return Get(child, today.AddDays(-i));
    }

    private string PathFor(string child, DateOnly date) =>
        Path.Combine(directory, SafeName(child), date.ToString("yyyy-MM-dd") + ".json");

    private void Save(UsageDay day)
    {
        var path = PathFor(day.Child, DateOnly.ParseExact(day.Date, "yyyy-MM-dd"));
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var tmp = path + ".tmp";
        File.WriteAllText(tmp, JsonSerializer.Serialize(day, GuardConfig.JsonOptions));
        File.Move(tmp, path, overwrite: true);
    }

    private static string SafeName(string s) =>
        string.Concat(s.Select(ch => Path.GetInvalidFileNameChars().Contains(ch) ? '_' : ch));
}
