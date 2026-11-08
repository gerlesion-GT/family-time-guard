using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace FamilyTimeGuard.Core;

/// <summary>Messages between the tray helper (runs as the child) and the Guard service (runs as SYSTEM).</summary>
public static class Protocol
{
    public const string PipeName = "FamilyTimeGuard";

    public static string Serialize<T>(T value) => JsonSerializer.Serialize(value, CompactOptions);
    public static T? Deserialize<T>(string json) => JsonSerializer.Deserialize<T>(json, CompactOptions);

    private static readonly JsonSerializerOptions CompactOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        WriteIndented = false,
    };
}

public sealed class AgentRequest
{
    /// <summary>"report" (every 2 seconds), "parent" (parent action), "request-time" (ask parent) or "status" (diagnostics).</summary>
    public string Type { get; set; } = "report";
    public string? ForegroundTitle { get; set; }
    public string? ForegroundProcess { get; set; }
    public ParentCommand? Parent { get; set; }
    public TimeRequest? Request { get; set; }
}

/// <summary>The child asks the parent for more time (sent to the parent's phone).</summary>
public sealed class TimeRequest
{
    public string Kind { get; set; } = "games";   // games | youtube
    public int Minutes { get; set; } = 15;
    public string? Note { get; set; }
}

public sealed class ParentCommand
{
    public string Pin { get; set; } = "";
    /// <summary>"verify", "bonus-games", "bonus-youtube", "pause", "resume".</summary>
    public string Action { get; set; } = "verify";
    public int Minutes { get; set; }
}

public sealed class AgentResponse
{
    public bool Ok { get; set; } = true;
    public string? Error { get; set; }
    public bool Enforced { get; set; }
    public string? ChildName { get; set; }
    public StatusInfo? Status { get; set; }
    public List<Notice> Notices { get; set; } = [];
    public bool CloseForegroundTab { get; set; }
    /// <summary>When set, it is bedtime and the session will be logged off in this many seconds.</summary>
    public int? LogoffInSeconds { get; set; }
    public string? Message { get; set; }
    /// <summary>Text for the full-screen lock message (bedtime or locked by a parent).</summary>
    public string? LockMessage { get; set; }
    /// <summary>True when the phone remote is set up, so the child can ask for more time.</summary>
    public bool RemoteEnabled { get; set; }
    /// <summary>Diagnostics for the "status" command.</summary>
    public string? Diagnostics { get; set; }
}

public sealed class StatusInfo
{
    public int GamesLeftSeconds { get; set; }
    public int GamesBudgetSeconds { get; set; }
    public int YouTubeLeftSeconds { get; set; }
    public int YouTubeBudgetSeconds { get; set; }
    public bool Bedtime { get; set; }
    public string? NextBedtime { get; set; }   // "HH:mm"
    public string? UnlockAt { get; set; }      // "HH:mm"
    public bool Paused { get; set; }
    public string? PausedUntil { get; set; }   // "HH:mm"
    public bool CountingGames { get; set; }
    public bool CountingYouTube { get; set; }
}

/// <summary>Stores the parent PIN as a salted PBKDF2 hash.</summary>
public static class PinHasher
{
    private const int Iterations = 120_000;

    public static string Hash(string pin)
    {
        var salt = RandomNumberGenerator.GetBytes(16);
        var hash = Rfc2898DeriveBytes.Pbkdf2(Encoding.UTF8.GetBytes(pin), salt, Iterations, HashAlgorithmName.SHA256, 32);
        return $"pbkdf2-sha256${Iterations}${Convert.ToBase64String(salt)}${Convert.ToBase64String(hash)}";
    }

    public static bool Verify(string pin, string? stored)
    {
        if (string.IsNullOrEmpty(stored) || string.IsNullOrEmpty(pin)) return false;
        var parts = stored.Split('$');
        if (parts.Length != 4 || parts[0] != "pbkdf2-sha256" || !int.TryParse(parts[1], out var iterations)) return false;
        try
        {
            var salt = Convert.FromBase64String(parts[2]);
            var expected = Convert.FromBase64String(parts[3]);
            var actual = Rfc2898DeriveBytes.Pbkdf2(Encoding.UTF8.GetBytes(pin), salt, iterations, HashAlgorithmName.SHA256, expected.Length);
            return CryptographicOperations.FixedTimeEquals(actual, expected);
        }
        catch (FormatException) { return false; }
    }

    public static bool IsValidPin(string? pin) => pin is { Length: >= 4 and <= 12 } && pin.All(char.IsDigit);
}

/// <summary>Limits wrong PIN guesses: 5 wrong tries locks parent actions for 15 minutes.</summary>
public sealed class PinThrottle
{
    private int _failures;
    private DateTimeOffset _lockedUntil = DateTimeOffset.MinValue;

    public bool IsLocked(DateTimeOffset now, out TimeSpan remaining)
    {
        remaining = _lockedUntil - now;
        return remaining > TimeSpan.Zero;
    }

    public void RecordFailure(DateTimeOffset now)
    {
        if (++_failures >= 5) { _lockedUntil = now.AddMinutes(15); _failures = 0; }
    }

    public void RecordSuccess() => _failures = 0;
}
