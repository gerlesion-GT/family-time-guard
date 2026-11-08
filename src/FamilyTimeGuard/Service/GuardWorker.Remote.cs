using System.Security.Cryptography;
using FamilyTimeGuard.Core;
using FamilyTimeGuard.Remote;

namespace FamilyTimeGuard.Service;

/// <summary>Phone remote: commands from the parent, alerts, "ask for more time" requests, evening summary.</summary>
internal sealed partial class GuardWorker
{
    private static readonly TimeSpan RequestExpiry = TimeSpan.FromMinutes(30);
    private const int MaxRequestsPerDay = 5;

    private RemoteControl? _remote;
    private readonly Dictionary<string, PendingRequest> _requests = [];

    private sealed record PendingRequest(string Id, string Child, string Kind, int Minutes, DateTimeOffset CreatedUtc);

    // ------------------------------------------------------------------ used by RemoteControl (thread-safe)

    internal (string? Token, TelegramSettings Settings) GetTelegram()
    {
        lock (_sync)
        {
            var t = _config.Current.Telegram;
            var copy = new TelegramSettings
            {
                EncryptedBotToken = t.EncryptedBotToken, BotUsername = t.BotUsername, OwnerUserId = t.OwnerUserId,
                OwnerChatId = t.OwnerChatId, PcName = t.PcName, DailySummaryAt = t.DailySummaryAt, Proxy = t.Proxy, Alerts = t.Alerts,
            };
            return (copy.IsReady ? TokenProtector.Unprotect(copy.EncryptedBotToken) : null, copy);
        }
    }

    internal long TelegramOffset { get { lock (_sync) return _state.State.TelegramOffset; } }

    internal void SetTelegramOffset(long offset)
    {
        lock (_sync)
        {
            if (offset <= _state.State.TelegramOffset) return;
            _state.State.TelegramOffset = offset;
            _state.Save();
        }
    }

    /// <summary>Runs a command sent by the parent and returns the reply text.</summary>
    internal string ExecuteRemote(string text)
    {
        lock (_sync)
        {
            try { return ExecuteRemoteLocked(text); }
            catch (ConfigException ex) { return "Not changed: " + ex.Message; }
            catch (Exception ex)
            {
                Log.Error("Remote command failed", ex);
                return "Sorry, something went wrong on the computer: " + ex.Message;
            }
        }
    }

    /// <summary>Parent pressed Approve/Deny on a request. Returns (popup text, new message text or null).</summary>
    internal (string Answer, string? NewText) HandleCallback(string? data, string? originalText)
    {
        lock (_sync)
        {
            var parts = (data ?? "").Split('|');
            if (parts.Length < 3 || parts[0] != "rq") return ("Unknown button.", null);
            ExpireRequests();
            if (!_requests.Remove(parts[1], out var req))
                return ("This request has already been answered or has expired.", (originalText ?? "") + "\n\n(expired)");

            var local = LocalNow;
            var what = req.Kind switch { "games" => "game", "youtube" => "YouTube", _ => "computer" };
            if (parts[2] == "ok" && parts.Length == 4 && int.TryParse(parts[3], out var minutes) && minutes is > 0 and <= 240)
            {
                var usage = _usage.Get(req.Child, DateOnly.FromDateTime(local));
                RuleEngine.AddBonus(usage, req.Kind, minutes);
                _usage.MarkDirty(usage);
                _usage.Flush();
                NotifyChild(req.Child, "Good news!", $"Your parent added {minutes} minutes of {what} time. Enjoy!");
                Log.Activity(local, req.Child, "request-approved", $"+{minutes} min {req.Kind}");
                return ($"Added {minutes} min", (originalText ?? "") + $"\n\n✅ You approved +{minutes} min of {what} time.");
            }

            NotifyChild(req.Child, "Not this time", $"Your parent said no to more {what} time today. Idea: {SuggestFor(req.Child)}.");
            Log.Activity(local, req.Child, "request-denied", req.Kind);
            return ("Answered: no", (originalText ?? "") + "\n\n❌ You said no.");
        }
    }

    // ------------------------------------------------------------------ inside the lock

    private void Alert(string text)
    {
        var t = _config.Current.Telegram;
        if (t.IsReady && t.Alerts) _remote?.Enqueue(text);
    }

    private void ExpireLocks(DateTimeOffset now)
    {
        var expired = _state.State.LockedUntilUtc.Where(kv => kv.Value <= now).Select(kv => kv.Key).ToList();
        if (expired.Count == 0) return;
        foreach (var k in expired) _state.State.LockedUntilUtc.Remove(k);
        _state.Save();
    }

    private void ExpireRequests()
    {
        var now = DateTimeOffset.UtcNow;
        foreach (var id in _requests.Values.Where(r => now - r.CreatedUtc > RequestExpiry).Select(r => r.Id).ToList())
            _requests.Remove(id);
    }

    private void CheckDailySummary(DateTime local)
    {
        var cfg = _config.Current;
        if (!cfg.Telegram.IsReady || !TimeParse.TryParse(cfg.Telegram.DailySummaryAt, out var at)) return;
        var today = local.ToString("yyyy-MM-dd");
        if (local.TimeOfDay < at || _state.State.LastSummaryDate == today) return;

        _state.State.LastSummaryDate = today;
        _state.Save();
        var children = cfg.ChildrenOnThisPc();
        if (children.Count == 0) return;
        var entries = ReadTodayActivity(local);
        var day = DateOnly.FromDateTime(local);
        var text = "📋 Evening summary – " + cfg.Telegram.PcName + "\n\n" + string.Join("\n\n",
            children.Select(c => Reports.Today(c, _usage.Get(c.Name, day), Schedule.BudgetFor(cfg, c, day), entries)));
        _remote?.Enqueue(text);
    }

    private AgentResponse HandleTimeRequest(AgentRequest req, PipeClient client)
    {
        var cfg = _config.Current;
        if (!cfg.Telegram.IsReady) return new AgentResponse { Ok = false, Error = "The phone remote is not set up. Please ask a parent in person." };
        if (req.Request is not { } r || client.SessionId < 0 || !_sessions.TryGetValue(client.SessionId, out var st))
            return new AgentResponse { Ok = false, Error = "Bad request." };

        ExpireRequests();
        if (_requests.Values.Any(p => p.Child == st.Child))
            return new AgentResponse { Ok = false, Error = "You already sent a request. Please wait for the answer." };

        var local = LocalNow;
        var day = DateOnly.FromDateTime(local);
        var usage = _usage.Get(st.Child, day);
        if (usage.RequestsToday >= MaxRequestsPerDay)
            return new AgentResponse { Ok = false, Error = $"You can ask {MaxRequestsPerDay} times a day. Try again tomorrow." };

        var kind = r.Kind is "youtube" or "screen" ? r.Kind : "games";
        var minutes = r.Minutes is 15 or 30 or 60 ? r.Minutes : 15;
        var note = string.IsNullOrWhiteSpace(r.Note) ? null : r.Note.Trim()[..Math.Min(r.Note.Trim().Length, 150)];
        usage.RequestsToday++;
        _usage.MarkDirty(usage);

        var id = Convert.ToHexString(RandomNumberGenerator.GetBytes(4)).ToLowerInvariant();
        _requests[id] = new PendingRequest(id, st.Child, kind, minutes, DateTimeOffset.UtcNow);

        var child = cfg.FindChildByName(st.Child)!;
        var budget = Schedule.BudgetFor(cfg, child, day);
        var what = kind switch { "games" => "games", "youtube" => "YouTube", _ => "computer time" };
        var text = $"🙋 {st.Child} asks for {minutes} more minutes of {what}." +
                   (note is null ? "" : $"\n“{note}”") +
                   $"\n\n{Reports.Status(child, st.LastResult, usage, budget, true, false, false)}";
        _remote?.Enqueue(text,
        [
            [new TgButton($"✅ +{minutes} min", $"rq|{id}|ok|{minutes}"), new TgButton($"✅ +{minutes * 2} min", $"rq|{id}|ok|{minutes * 2}")],
            [new TgButton("❌ No", $"rq|{id}|no")],
        ]);
        Log.Activity(local, st.Child, "request-sent", $"{minutes} min {kind}" + (note is null ? "" : $": {note}"));
        return new AgentResponse { Ok = true, Message = "Request sent to your parent. The answer will pop up here." };
    }

    private AgentResponse HandleShutdown(PipeClient client)
    {
        if (client.SessionId < 0 || !_sessions.TryGetValue(client.SessionId, out var st))
            return new AgentResponse { Ok = false, Error = "Not allowed." };
        Log.Activity(LocalNow, st.Child, "shutdown", "from the lock screen");
        Log.Info($"Shutting down at the request of {st.Child}.");
        try
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("shutdown.exe", "/s /t 0") { CreateNoWindow = true, UseShellExecute = false });
            return new AgentResponse { Ok = true, Message = "Shutting down..." };
        }
        catch (Exception ex)
        {
            Log.Error("Shutdown failed", ex);
            return new AgentResponse { Ok = false, Error = "Could not shut down: " + ex.Message };
        }
    }

    private string ExecuteRemoteLocked(string text)
    {
        var cfg = _config.Current;
        var onPc = cfg.ChildrenOnThisPc();
        var cmd = RemoteCommandParser.Parse(text, cfg.Children.Select(c => c.Name).ToList());
        var now = DateTimeOffset.UtcNow;
        var local = LocalNow;
        var day = DateOnly.FromDateTime(local);

        if (cmd is InvalidCommand bad) return bad.Problem;
        if (cmd is HelpCommand) return $"{cfg.Telegram.PcName} – Family Time Guard\n\n{RemoteCommandParser.Help}";
        if (cmd is PairCommand) return "This computer is already paired with you.";
        if (onPc.Count == 0) return "No child account is set up on this computer yet.";

        // Commands about all children on this PC when no name is given.
        var targets = cmd.Child is { } name
            ? cfg.Children.Where(c => c.Name.Equals(name, StringComparison.OrdinalIgnoreCase)).ToList()
            : onPc;

        switch (cmd)
        {
            case StatusCommand:
                return string.Join("\n\n", targets.Select(c => Reports.Status(c, LiveResult(c.Name), _usage.Get(c.Name, day),
                    Schedule.BudgetFor(cfg, c, day), true, _state.State.IsLocked(c.Name, now), _state.State.IsPaused(now))))
                    + (_state.State.IsPaused(now) ? $"\n\nRules are paused until {ToLocal(_state.State.PausedUntilUtc!.Value):HH:mm}." : "");

            case TodayCommand:
                var entries = ReadTodayActivity(local);
                return string.Join("\n\n", targets.Select(c => Reports.Today(c, _usage.Get(c.Name, day), Schedule.BudgetFor(cfg, c, day), entries)));

            case RulesCommand:
                return string.Join("\n\n", targets.Select(c => RemoteConfigEditor.DescribeRules(cfg, c)));

            case PauseCommand p:
                _state.State.PausedUntilUtc = now + p.Duration;
                _state.State.LockedUntilUtc.Clear();
                _state.Save();
                Log.Activity(local, "-", "parent-action", $"phone: paused for {RemoteCommandParser.FormatDuration(p.Duration)}");
                return $"⏸ All rules paused until {local + p.Duration:HH:mm} (including bedtime). Send /resume to end early.";

            case ResumeCommand:
                _state.State.PausedUntilUtc = null;
                _state.Save();
                Log.Activity(local, "-", "parent-action", "phone: resumed");
                return "▶️ Rules are active again.";
        }

        if (targets.Count != 1)
            return $"This computer has more than one child. Add the name, e.g. {text.Split(' ')[0]} {onPc[0].Name} ...";
        var child = targets[0];

        switch (cmd)
        {
            case BonusCommand b:
            {
                var usage = _usage.Get(child.Name, day);
                RuleEngine.AddBonus(usage, b.Kind, b.Minutes);
                _usage.MarkDirty(usage);
                _usage.Flush();
                var what = b.Kind switch { "games" => "game", "youtube" => "YouTube", _ => "computer" };
                NotifyChild(child.Name, "Extra time!", $"Your parent gave you {b.Minutes} extra minutes of {what} time today.");
                Log.Activity(local, child.Name, "parent-action", $"phone: +{b.Minutes} min {b.Kind}");
                return $"✅ Added {b.Minutes} min of {what} time for {child.Name} today.\n\n" +
                       Reports.Status(child, LiveResult(child.Name), usage, Schedule.BudgetFor(cfg, child, day), true, false, false);
            }

            case SetBudgetCommand sb:
                return EditConfig(c => RemoteConfigEditor.Apply(c, c.FindChildByName(child.Name)!, sb), local, child.Name);

            case SetBedtimeCommand bt:
                return EditConfig(c => RemoteConfigEditor.Apply(c, c.FindChildByName(child.Name)!, bt), local, child.Name);

            case LockCommand l:
            {
                var untilLocal = l.Duration is { } d ? local + d : Schedule.NextMorningUnlock(cfg, child, local);
                _state.State.LockedUntilUtc[child.Name] = TimeZoneInfo.ConvertTimeToUtc(untilLocal, Zone);
                _state.State.PausedUntilUtc = null;
                _state.Save();
                Log.Activity(local, child.Name, "parent-action", $"phone: locked until {untilLocal:yyyy-MM-dd HH:mm}");
                var signedIn = _sessions.Values.Any(s => s.Child == child.Name);
                return $"🔒 {child.Name}'s computer is locked until {untilLocal:ddd HH:mm}." +
                       (signedIn ? " They will see a message and be logged off in about 1 minute." : "") + "\nSend /unlock to end early.";
            }

            case UnlockCommand:
                var wasLocked = _state.State.LockedUntilUtc.Remove(child.Name);
                _state.Save();
                Log.Activity(local, child.Name, "parent-action", "phone: unlocked");
                return wasLocked ? $"🔓 {child.Name}'s computer is unlocked." : $"{child.Name}'s computer was not locked by you." +
                    (Schedule.IsBedtime(cfg, child, local) ? " (It is bedtime though. Use /pause 1h to allow use now.)" : "");
        }

        return "Sorry, I didn't understand. Send /help.";
    }

    private string EditConfig(Func<GuardConfig, string> change, DateTime local, string child, bool logIt = true)
    {
        var cfg = GuardConfig.Parse(File.ReadAllText(Paths.ConfigFile));
        var message = change(cfg);
        var errors = cfg.Validate();
        if (errors.Count > 0) throw new ConfigException(string.Join(" ", errors));
        var tmp = Paths.ConfigFile + ".tmp";
        File.WriteAllText(tmp, cfg.ToJson());
        File.Move(tmp, Paths.ConfigFile, overwrite: true);
        _config.Refresh(force: true);
        if (logIt) Log.Activity(local, child, "parent-action", "phone: " + message);
        return "✅ " + message;
    }

    private TickResult? LiveResult(string child) =>
        _sessions.Values.Where(s => s.Child == child).Select(s => s.LastResult).FirstOrDefault(r => r is not null);

    private void NotifyChild(string child, string title, string message)
    {
        foreach (var st in _sessions.Values.Where(s => s.Child == child))
            st.PendingNotices.Add(new Notice(Notice.Toast, title, message));
    }

    private string SuggestFor(string child)
    {
        var list = _config.Current.FindChildByName(child)?.Suggestions ?? [];
        return list.Count == 0 ? "take a break" : list[Random.Shared.Next(list.Count)];
    }

    private DateTime ToLocal(DateTimeOffset utc) => TimeZoneInfo.ConvertTimeFromUtc(utc.UtcDateTime, Zone);

    private static List<ActivityEntry> ReadTodayActivity(DateTime local)
    {
        var file = Path.Combine(Paths.LogDir, $"activity-{local:yyyy-MM-dd}.csv");
        try
        {
            if (!File.Exists(file)) return [];
            using var fs = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            using var reader = new StreamReader(fs);
            var lines = new List<string>();
            while (reader.ReadLine() is { } line) lines.Add(line);
            return Reports.ParseActivityCsv(lines);
        }
        catch (Exception ex)
        {
            Log.Error("Could not read the activity log", ex);
            return [];
        }
    }
}
