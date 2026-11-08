using System.Runtime.InteropServices;
using FamilyTimeGuard.Core;
using Microsoft.Win32;

namespace FamilyTimeGuard.Service;

internal static class Paths
{
    public static string DataDir { get; } =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "FamilyTimeGuard");

    public static string ConfigFile => Path.Combine(DataDir, "config.json");
    public static string StateFile => Path.Combine(DataDir, "state.json");
    public static string UsageDir => Path.Combine(DataDir, "usage");
    public static string LogDir => Path.Combine(DataDir, "logs");
}

/// <summary>
/// Two logs: an activity log for parents (logs\activity-yyyy-MM-dd.csv) and a technical log (logs\service.log).
/// </summary>
internal static class Log
{
    private static readonly object Sync = new();

    public static void Activity(DateTime local, string child, string evt, string detail = "")
    {
        lock (Sync)
        {
            try
            {
                Directory.CreateDirectory(Paths.LogDir);
                var file = Path.Combine(Paths.LogDir, $"activity-{local:yyyy-MM-dd}.csv");
                var isNew = !File.Exists(file);
                using var w = new StreamWriter(file, append: true);
                if (isNew) w.WriteLine("time,child,event,detail");
                w.WriteLine($"{local:HH:mm:ss},{Csv(child)},{Csv(evt)},{Csv(detail)}");
            }
            catch (Exception) { /* logging must never crash the guard */ }
        }
    }

    public static void Info(string message) => Write("INFO", message);
    public static void Error(string message, Exception? ex = null) => Write("ERROR", ex is null ? message : $"{message}: {ex}");

    private static void Write(string level, string message)
    {
        lock (Sync)
        {
            try
            {
                Directory.CreateDirectory(Paths.LogDir);
                var file = Path.Combine(Paths.LogDir, "service.log");
                if (File.Exists(file) && new FileInfo(file).Length > 2_000_000)
                    File.Move(file, file + ".old", overwrite: true);
                File.AppendAllText(file, $"{DateTime.Now:yyyy-MM-dd HH:mm:ss} {level} {message}{Environment.NewLine}");
            }
            catch (Exception) { }
        }
    }

    private static string Csv(string s)
    {
        s = s.Replace("\r", " ").Replace("\n", " ");
        return s.IndexOfAny([',', '"']) >= 0 ? "\"" + s.Replace("\"", "\"\"") + "\"" : s;
    }
}

/// <summary>Reads config.json and notices when a parent edits it.</summary>
internal sealed class ConfigWatcher
{
    private DateTime _lastWrite = DateTime.MinValue;
    private DateTime _lastCheck = DateTime.MinValue;

    public GuardConfig Current { get; private set; } = new();
    public string? LastError { get; private set; }

    public void Refresh(bool force = false)
    {
        if (!force && DateTime.UtcNow - _lastCheck < TimeSpan.FromSeconds(10)) return;
        _lastCheck = DateTime.UtcNow;
        try
        {
            var info = new FileInfo(Paths.ConfigFile);
            if (!info.Exists)
            {
                if (LastError is null) Log.Error($"Settings file not found: {Paths.ConfigFile}");
                LastError = "Settings file not found.";
                return;
            }
            if (!force && info.LastWriteTimeUtc == _lastWrite) return;
            _lastWrite = info.LastWriteTimeUtc;
            Current = GuardConfig.Parse(File.ReadAllText(Paths.ConfigFile));
            LastError = null;
            Log.Info("Settings loaded.");
        }
        catch (Exception ex)
        {
            // Keep using the last good settings.
            LastError = ex.Message;
            Log.Error("Could not read settings (still using the previous ones)", ex);
        }
    }
}

internal sealed class StateStore
{
    public RuntimeState State { get; private set; } = new();

    public void Load()
    {
        try
        {
            if (File.Exists(Paths.StateFile))
                State = System.Text.Json.JsonSerializer.Deserialize<RuntimeState>(File.ReadAllText(Paths.StateFile), GuardConfig.JsonOptions) ?? new();
        }
        catch (Exception ex) { Log.Error("Could not read state.json", ex); State = new(); }
    }

    public void Save()
    {
        try
        {
            Directory.CreateDirectory(Paths.DataDir);
            File.WriteAllText(Paths.StateFile, System.Text.Json.JsonSerializer.Serialize(State, GuardConfig.JsonOptions));
        }
        catch (Exception ex) { Log.Error("Could not save state.json", ex); }
    }
}

/// <summary>A logged-in Windows session.</summary>
/// <summary>A logged-in Windows session. OnScreen = the session currently using the screen (not switched away).</summary>
internal sealed record UserSession(int SessionId, string UserName, bool Locked, bool OnScreen);

internal static class Sessions
{
    public static List<UserSession> GetActiveUserSessions()
    {
        var result = new List<UserSession>();
        if (!Native.WTSEnumerateSessionsW(Native.WTS_CURRENT_SERVER_HANDLE, 0, 1, out var buffer, out var count))
            return result;
        try
        {
            var size = Marshal.SizeOf<Native.WTS_SESSION_INFO>();
            for (var i = 0; i < count; i++)
            {
                var info = Marshal.PtrToStructure<Native.WTS_SESSION_INFO>(buffer + i * size);
                if (info.SessionId == 0) continue;
                // Active = in use at the screen. Disconnected = switched away (fast user switching) but still logged in.
                if (info.State is not (Native.WTS_CONNECTSTATE_CLASS.Active or Native.WTS_CONNECTSTATE_CLASS.Disconnected)) continue;
                var user = QueryString(info.SessionId, Native.WTS_INFO_CLASS.WTSUserName);
                if (string.IsNullOrEmpty(user)) continue;
                var locked = info.State == Native.WTS_CONNECTSTATE_CLASS.Disconnected || IsLocked(info.SessionId);
                result.Add(new UserSession(info.SessionId, user, locked, info.State == Native.WTS_CONNECTSTATE_CLASS.Active));
            }
        }
        finally { Native.WTSFreeMemory(buffer); }
        return result;
    }

    private static string? QueryString(int sessionId, Native.WTS_INFO_CLASS cls)
    {
        if (!Native.WTSQuerySessionInformationW(Native.WTS_CURRENT_SERVER_HANDLE, sessionId, cls, out var buf, out _)) return null;
        try { return Marshal.PtrToStringUni(buf); }
        finally { Native.WTSFreeMemory(buf); }
    }

    private static bool IsLocked(int sessionId)
    {
        if (!Native.WTSQuerySessionInformationW(Native.WTS_CURRENT_SERVER_HANDLE, sessionId, Native.WTS_INFO_CLASS.WTSSessionInfoEx, out var buf, out var bytes))
            return false;
        try
        {
            // WTSINFOEXW { DWORD Level; (8-byte aligned union) WTSINFOEX_LEVEL1_W { SessionId; SessionState; SessionFlags; ... } }
            if (bytes < 20 || Marshal.ReadInt32(buf, 0) != 1) return false;
            const int WTS_SESSIONSTATE_LOCK = 0;
            return Marshal.ReadInt32(buf, 16) == WTS_SESSIONSTATE_LOCK;
        }
        finally { Native.WTSFreeMemory(buf); }
    }

    public static bool LogOff(int sessionId) => Native.WTSLogoffSession(Native.WTS_CURRENT_SERVER_HANDLE, sessionId, false);
}

/// <summary>Turns off browsers' private DNS so the YouTube block cannot be skipped.</summary>
internal static class BrowserPolicies
{
    public static void Apply()
    {
        try
        {
            SetString(@"SOFTWARE\Policies\Google\Chrome", "DnsOverHttpsMode", "off");
            SetString(@"SOFTWARE\Policies\Microsoft\Edge", "DnsOverHttpsMode", "off");
            SetString(@"SOFTWARE\Policies\BraveSoftware\Brave", "DnsOverHttpsMode", "off");
            using (var ff = Registry.LocalMachine.CreateSubKey(@"SOFTWARE\Policies\Mozilla\Firefox\DNSOverHTTPS"))
            {
                ff.SetValue("Enabled", 0, RegistryValueKind.DWord);
                ff.SetValue("Locked", 1, RegistryValueKind.DWord);
            }
        }
        catch (Exception ex) { Log.Error("Could not apply browser policies", ex); }
    }

    public static void Remove()
    {
        try
        {
            DeleteValue(@"SOFTWARE\Policies\Google\Chrome", "DnsOverHttpsMode");
            DeleteValue(@"SOFTWARE\Policies\Microsoft\Edge", "DnsOverHttpsMode");
            DeleteValue(@"SOFTWARE\Policies\BraveSoftware\Brave", "DnsOverHttpsMode");
            Registry.LocalMachine.DeleteSubKeyTree(@"SOFTWARE\Policies\Mozilla\Firefox\DNSOverHTTPS", throwOnMissingSubKey: false);
        }
        catch (Exception ex) { Log.Error("Could not remove browser policies", ex); }
    }

    private static void SetString(string key, string name, string value)
    {
        using var k = Registry.LocalMachine.CreateSubKey(key);
        k.SetValue(name, value, RegistryValueKind.String);
    }

    private static void DeleteValue(string key, string name)
    {
        using var k = Registry.LocalMachine.OpenSubKey(key, writable: true);
        k?.DeleteValue(name, throwOnMissingValue: false);
    }
}

/// <summary>Blocks sites by adding lines to the Windows hosts file between our own markers.</summary>
internal static class HostsFile
{
    private const string Begin = "# >>> FamilyTimeGuard (managed automatically, do not edit)";
    private const string End = "# <<< FamilyTimeGuard";

    private static string FilePath => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), @"drivers\etc\hosts");

    private static bool? _lastBlocked;

    public static bool IsBlocked => _lastBlocked == true;
    private static string _lastDomains = "";

    public static void SetBlocked(bool blocked, IReadOnlyList<string> domains)
    {
        var key = string.Join(",", domains);
        if (_lastBlocked == blocked && _lastDomains == key) return;
        try
        {
            var original = File.Exists(FilePath) ? File.ReadAllText(FilePath) : "";
            var updated = Strip(original).TrimEnd('\r', '\n');
            if (blocked && domains.Count > 0)
            {
                var lines = new List<string> { "", Begin };
                foreach (var d in domains.Select(d => d.Trim()).Where(d => d.Length > 0))
                {
                    lines.Add($"0.0.0.0 {d}");
                    lines.Add($":: {d}");
                }
                lines.Add(End);
                updated += string.Join("\r\n", lines);
            }
            updated += "\r\n";
            if (updated != original)
            {
                File.WriteAllText(FilePath, updated);
                try { Native.DnsFlushResolverCache(); } catch (Exception) { }
                Log.Info(blocked ? "YouTube blocked." : "YouTube unblocked.");
            }
            _lastBlocked = blocked;
            _lastDomains = key;
        }
        catch (Exception ex) { Log.Error("Could not update the hosts file", ex); }
    }

    internal static string Strip(string text)
    {
        var lines = text.Replace("\r\n", "\n").Split('\n');
        var output = new List<string>();
        var inside = false;
        foreach (var line in lines)
        {
            if (line.StartsWith(Begin, StringComparison.Ordinal)) { inside = true; continue; }
            if (inside && line.StartsWith(End, StringComparison.Ordinal)) { inside = false; continue; }
            if (!inside) output.Add(line);
        }
        return string.Join("\r\n", output);
    }
}
