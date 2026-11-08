using System.Text;
using FamilyTimeGuard.Core;
using FamilyTimeGuard.Remote;
using Microsoft.Extensions.Hosting;

namespace FamilyTimeGuard.Service;

/// <summary>What the Guard remembers about one logged-in child session.</summary>
internal sealed class SessionState
{
    public int SessionId;
    public string UserName = "";
    public string Child = "";

    public DateTimeOffset LastHeartbeat = DateTimeOffset.MinValue;
    public string? ForegroundTitle;
    public string? ForegroundProcess;
    public int IdleSeconds;
    public int AgentPid = -1;

    public DateTimeOffset LastLaunch = DateTimeOffset.MinValue;
    public int LaunchesWithoutHeartbeat;
    public DateTimeOffset LastTick = DateTimeOffset.MinValue;

    public DateTimeOffset? BedtimeSince;
    public int? LogoffInSeconds;

    public TickResult? LastResult;
    public readonly List<Notice> PendingNotices = [];
    public bool PendingCloseTab;

    public string? LastYouTubeTitle;
    public bool WasPlayingGames;

    public bool JustSignedIn = true;
    public bool SignedInDuringBedtime;

    public DateTimeOffset? HelperMissingSince;
    public string? YouTubeBlockReason;
}

/// <summary>The background service: checks every 2 seconds and enforces the rules.</summary>
internal sealed partial class GuardWorker : BackgroundService
{
    private static readonly TimeSpan TickInterval = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan HeartbeatTimeout = TimeSpan.FromSeconds(12);
    private static readonly TimeSpan RelaunchDelay = TimeSpan.FromSeconds(20);
    private static readonly TimeSpan BedtimeGrace = TimeSpan.FromSeconds(60);
    /// <summary>How long the tray helper may be missing before YouTube is blocked as a precaution.</summary>
    private static readonly TimeSpan HelperMissingGrace = TimeSpan.FromMinutes(2);

    private readonly object _sync = new();
    private readonly ConfigWatcher _config = new();
    private readonly StateStore _state = new();
    private readonly UsageStore _usage = new(Paths.UsageDir);
    private readonly ProcessScanner _scanner = new();
    private readonly PinThrottle _pinThrottle = new();
    private readonly Dictionary<int, SessionState> _sessions = [];
    private DateTimeOffset _lastFlush = DateTimeOffset.UtcNow;

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        Log.Info($"Family Time Guard service starting (version {typeof(GuardWorker).Assembly.GetName().Version}).");
        Directory.CreateDirectory(Paths.DataDir);
        _config.Refresh(force: true);
        _state.Load();
        BrowserPolicies.Apply();

        var pipeTask = new PipeServer(HandleRequest).RunAsync(ct);
        _remote = new RemoteControl(this);
        var remoteTask = _remote.RunAsync(ct);

        using var timer = new PeriodicTimer(TickInterval);
        try
        {
            do
            {
                try { lock (_sync) Tick(); }
                catch (Exception ex) { Log.Error("Check failed", ex); }
            }
            while (await timer.WaitForNextTickAsync(ct));
        }
        catch (OperationCanceledException) { }

        lock (_sync)
        {
            _usage.Flush();
            HostsFile.SetBlocked(false, []);
        }
        try { await pipeTask; } catch (OperationCanceledException) { }
        try { await remoteTask; } catch (OperationCanceledException) { }
        Log.Info("Family Time Guard service stopped.");
    }

    private TimeZoneInfo Zone => TimeZones.Find(_config.Current.TimeZone);
    private DateTime LocalNow => TimeZoneInfo.ConvertTimeFromUtc(DateTime.UtcNow, Zone);

    // ------------------------------------------------------------------ main check

    private void Tick()
    {
        _config.Refresh();
        var cfg = _config.Current;
        var now = DateTimeOffset.UtcNow;
        var local = LocalNow;
        var paused = _state.State.IsPaused(now);
        ExpireLocks(now);

        var childSessions = new List<(UserSession Session, ChildProfile Child)>();
        foreach (var s in Sessions.GetActiveUserSessions())
            if (cfg.FindChildByAccount(s.UserName) is { } child) childSessions.Add((s, child));

        // Forget sessions that ended.
        foreach (var id in _sessions.Keys.Where(id => childSessions.All(c => c.Session.SessionId != id)).ToList())
            _sessions.Remove(id);

        var games = _scanner.FindGamesBySession(cfg.Games, childSessions.Select(c => c.Session.SessionId).ToHashSet());
        var blockYouTube = false;

        foreach (var (session, child) in childSessions)
        {
            var st = GetState(session.SessionId);
            if (st.UserName != session.UserName || st.Child != child.Name)
            {
                st.UserName = session.UserName;
                st.Child = child.Name;
                Log.Activity(local, child.Name, "logged-in", session.UserName);
            }

            var elapsed = st.LastTick == DateTimeOffset.MinValue ? TimeSpan.Zero : now - st.LastTick;
            st.LastTick = now;

            var agentConnected = now - st.LastHeartbeat < HeartbeatTimeout;
            if (agentConnected) st.HelperMissingSince = null;
            else st.HelperMissingSince ??= now;
            // Give the helper time to start (e.g. right after sign-in) before treating it as missing.
            var helperOk = agentConnected || now - st.HelperMissingSince!.Value < HelperMissingGrace;
            var running = games[session.SessionId];
            var obs = new Observation(
                GameProcessRunning: running.Count > 0,
                ForegroundTitle: agentConnected ? st.ForegroundTitle : null,
                ForegroundProcess: agentConnected ? st.ForegroundProcess : null,
                AgentConnected: helperOk,
                SessionLocked: session.Locked,
                IdleSeconds: agentConnected ? st.IdleSeconds : 0);

            var usage = _usage.Get(child.Name, DateOnly.FromDateTime(local));
            var parentLocked = _state.State.IsLocked(child.Name, now);
            var result = RuleEngine.Tick(cfg, child, usage, obs, local, elapsed, paused, parentLocked);
            _usage.MarkDirty(usage);
            if (st.JustSignedIn)
            {
                st.JustSignedIn = false;
                st.SignedInDuringBedtime = result.Bedtime && !result.Paused;
            }
            foreach (var ev in result.Events)
            {
                if (ev == "games-used-up") Alert($"🎮 {child.Name}'s game time is used up for today.");
                if (ev == "youtube-used-up") Alert($"📺 {child.Name}'s YouTube time is used up for today.");
                if (ev == "screen-used-up") Alert($"⏱ {child.Name}'s screen time is used up for today. The computer locks in 1 minute.");
            }
            st.LastResult = result;
            st.PendingNotices.AddRange(result.Notices);
            st.PendingCloseTab |= result.CloseForegroundTab;
            st.YouTubeBlockReason = !result.BlockYouTube ? null
                : !helperOk ? "the tray helper is not running"
                : "YouTube time is used up for today";
            // The block is computer-wide (hosts file), so only apply it while the child is the one using the screen.
            // Otherwise a parent who uses "Switch user" would also lose YouTube.
            if (session.OnScreen) blockYouTube |= result.BlockYouTube;

            LogActivityChanges(st, result, running, local);

            if (result.KillGames)
                foreach (var g in running)
                {
                    ProcessScanner.Kill(g);
                    Log.Activity(local, child.Name, "game-closed", g.Name);
                }

            EnforceBedtime(st, session, result, now, local);
            EnsureAgent(st, session, now, local);
        }

        HostsFile.SetBlocked(blockYouTube, cfg.YouTube.BlockDomains);
        CheckDailySummary(local);

        if (now - _lastFlush > TimeSpan.FromSeconds(30))
        {
            _usage.Flush();
            _lastFlush = now;
        }
    }

    private SessionState GetState(int sessionId)
    {
        if (!_sessions.TryGetValue(sessionId, out var st))
            _sessions[sessionId] = st = new SessionState { SessionId = sessionId };
        return st;
    }

    private void EnforceBedtime(SessionState st, UserSession session, TickResult result, DateTimeOffset now, DateTime local)
    {
        if (!result.Bedtime || result.Paused)
        {
            st.BedtimeSince = null;
            st.LogoffInSeconds = null;
            return;
        }

        st.BedtimeSince ??= now;
        var left = BedtimeGrace - (now - st.BedtimeSince.Value);
        if (left > TimeSpan.Zero)
        {
            st.LogoffInSeconds = (int)Math.Ceiling(left.TotalSeconds);
            return;
        }

        var screenLock = !result.ParentLocked && result.ScreenTimeUp && result.UnlockAtLocal is null;
        Log.Activity(local, st.Child, result.ParentLocked ? "locked-logoff" : screenLock ? "screen-logoff" : "bedtime-logoff", session.Locked ? "session was locked" : "");
        if (result.ParentLocked) Alert($"🔒 {st.Child} was logged off (locked by you).");
        else if (screenLock) Alert($"⏱ {st.Child} was logged off at {local:HH:mm}: screen time is used up for today.");
        else if (st.SignedInDuringBedtime) Alert($"🌙 {st.Child} signed in during bedtime ({local:HH:mm}) and was logged off.");
        else Alert($"🌙 Bedtime: {st.Child} was logged off at {local:HH:mm}.");
        Log.Info($"Bedtime: logging off {session.UserName} (session {session.SessionId}).");
        if (!Sessions.LogOff(session.SessionId))
            Log.Error($"Log off failed: {Native.LastError("WTSLogoffSession").Message}");
        st.BedtimeSince = now; // retry after another grace period if it did not work
    }

    private void EnsureAgent(SessionState st, UserSession session, DateTimeOffset now, DateTime local)
    {
        if (now - st.LastHeartbeat < HeartbeatTimeout) { st.LaunchesWithoutHeartbeat = 0; return; }
        if (now - st.LastLaunch < RelaunchDelay) return;
        if (st.AgentPid > 0 && ProcessScanner.IsAlive(st.AgentPid) && now - st.LastLaunch < TimeSpan.FromMinutes(1)) return;

        st.LastLaunch = now;
        try
        {
            // If the protected helper keeps failing to start, fall back to a normal one so the child still sees warnings.
            var protect = st.LaunchesWithoutHeartbeat < 3;
            st.AgentPid = AgentLauncher.Launch(session.SessionId, protect);
            st.LaunchesWithoutHeartbeat++;
            if (st.LastHeartbeat != DateTimeOffset.MinValue)
            {
                Log.Activity(local, st.Child, "helper-restarted", "the tray helper was not running");
                Alert($"⚠️ The clock helper on {_config.Current.Telegram.PcName} had stopped and was restarted ({st.Child}).");
            }
            Log.Info($"Started tray helper in session {session.SessionId} (pid {st.AgentPid}, protected={protect}).");
        }
        catch (Exception ex)
        {
            Log.Error($"Could not start tray helper in session {session.SessionId}", ex);
        }
    }

    private static void LogActivityChanges(SessionState st, TickResult r, List<RunningGame> running, DateTime local)
    {
        if (r.CountingGames != st.WasPlayingGames)
        {
            st.WasPlayingGames = r.CountingGames;
            var what = running.Count > 0 ? string.Join(" ", running.Select(g => g.Name).Distinct()) : st.ForegroundTitle ?? "";
            Log.Activity(local, st.Child, r.CountingGames ? "games-start" : "games-stop", r.CountingGames ? what : "");
        }

        var ytTitle = r.CountingYouTube ? st.ForegroundTitle : null;
        if (ytTitle != st.LastYouTubeTitle)
        {
            st.LastYouTubeTitle = ytTitle;
            if (ytTitle is not null) Log.Activity(local, st.Child, "youtube", ytTitle);
        }
    }

    // ------------------------------------------------------------------ requests from the tray helper

    private AgentResponse HandleRequest(AgentRequest req, PipeClient client)
    {
        lock (_sync)
        {
            return req.Type switch
            {
                "report" => HandleReport(req, client),
                "parent" => HandleParent(req, client),
                "request-time" => HandleTimeRequest(req, client),
                "shutdown" => HandleShutdown(client),
                "status" => HandleStatus(),
                _ => new AgentResponse { Ok = false, Error = "Unknown request" },
            };
        }
    }

    private AgentResponse HandleReport(AgentRequest req, PipeClient client)
    {
        if (client.SessionId < 0 || !_sessions.TryGetValue(client.SessionId, out var st))
            return new AgentResponse { Enforced = false };

        st.LastHeartbeat = DateTimeOffset.UtcNow;
        st.AgentPid = client.ProcessId;
        st.LaunchesWithoutHeartbeat = 0;
        st.ForegroundTitle = Truncate(req.ForegroundTitle, 300);
        st.ForegroundProcess = Truncate(req.ForegroundProcess, 100);
        st.IdleSeconds = Math.Max(0, req.IdleSeconds);

        var response = new AgentResponse
        {
            Enforced = true,
            ChildName = st.Child,
            Status = ToStatus(st.LastResult, _usage.Get(st.Child, DateOnly.FromDateTime(LocalNow))),
            Notices = [.. st.PendingNotices],
            CloseForegroundTab = st.PendingCloseTab,
            LogoffInSeconds = st.LogoffInSeconds,
            LockTitle = st.LastResult switch
            {
                { ParentLocked: true } => "Computer locked",
                { ScreenTimeUp: true, UnlockAtLocal: null } => "Screen time is over",
                _ => null,
            },
            LockMessage = st.LastResult switch
            {
                { ParentLocked: true } =>
                    $"A parent locked this computer{(_state.State.LockedUntilUtc.TryGetValue(st.Child, out var until) ? " until " + TimeZoneInfo.ConvertTimeFromUtc(until.UtcDateTime, Zone).ToString("ddd HH:mm") : "")}.",
                { ScreenTimeUp: true, UnlockAtLocal: null } =>
                    $"You have used all your computer time for today, {st.Child}. Please shut down the computer.",
                _ => null,
            },
            RemoteEnabled = _config.Current.Telegram.IsReady,
        };
        st.PendingNotices.Clear();
        st.PendingCloseTab = false;
        return response;
    }

    private AgentResponse HandleParent(AgentRequest req, PipeClient client)
    {
        var cmd = req.Parent;
        if (cmd is null) return new AgentResponse { Ok = false, Error = "Missing command" };
        var now = DateTimeOffset.UtcNow;
        var local = LocalNow;

        if (_pinThrottle.IsLocked(now, out var wait))
            return new AgentResponse { Ok = false, Error = $"Too many wrong PINs. Try again in {Math.Ceiling(wait.TotalMinutes)} minutes." };

        var cfg = _config.Current;
        if (string.IsNullOrEmpty(cfg.ParentPinHash))
            return new AgentResponse { Ok = false, Error = "No parent PIN has been set. Run the installer again to set one." };

        _sessions.TryGetValue(client.SessionId, out var st);
        if (!PinHasher.Verify(cmd.Pin, cfg.ParentPinHash))
        {
            _pinThrottle.RecordFailure(now);
            Log.Activity(local, st?.Child ?? "?", "parent-pin-wrong");
            if (_pinThrottle.IsLocked(now, out _))
                Alert($"⚠️ Someone entered a wrong parent PIN 5 times on {cfg.Telegram.PcName}. Parent options are blocked for 15 minutes.");
            return new AgentResponse { Ok = false, Error = "Wrong PIN." };
        }
        _pinThrottle.RecordSuccess();

        var minutes = Math.Clamp(cmd.Minutes, 0, 24 * 60);
        string message;
        switch (cmd.Action)
        {
            case "verify":
                return new AgentResponse { Ok = true, Message = "PIN accepted." };
            case "bonus-games" or "bonus-youtube" or "bonus-screen":
                if (st is null) return new AgentResponse { Ok = false, Error = "This account has no rules, so extra time is not needed." };
                var usage = _usage.Get(st.Child, DateOnly.FromDateTime(local));
                var what = cmd.Action[6..]; // games | youtube | screen
                RuleEngine.AddBonus(usage, what, minutes);
                _usage.MarkDirty(usage);
                _usage.Flush();
                message = $"Added {minutes} minutes of {what switch { "games" => "game", "youtube" => "YouTube", _ => "screen" }} time for {st.Child} today.";
                break;
            case "pause":
                _state.State.PausedUntilUtc = now.AddMinutes(minutes);
                _state.Save();
                message = $"All rules are paused until {local.AddMinutes(minutes):HH:mm}.";
                break;
            case "resume":
                _state.State.PausedUntilUtc = null;
                _state.Save();
                message = "Rules are active again.";
                break;
            default:
                return new AgentResponse { Ok = false, Error = "Unknown action." };
        }

        Log.Activity(local, st?.Child ?? "-", "parent-action", message);
        Alert($"ℹ️ Parent options were used on {cfg.Telegram.PcName} (with the PIN): {message}");
        Log.Info("Parent action: " + message);
        return new AgentResponse { Ok = true, Message = message };
    }

    private AgentResponse HandleStatus()
    {
        var sb = new StringBuilder();
        sb.AppendLine($"Family Time Guard service {typeof(GuardWorker).Assembly.GetName().Version} is running.");
        sb.AppendLine($"Local time ({_config.Current.TimeZone}): {LocalNow:yyyy-MM-dd HH:mm}");
        sb.AppendLine(_config.LastError is null ? "Settings: OK" : $"Settings problem: {_config.LastError}");
        sb.AppendLine($"Parent PIN set: {(!string.IsNullOrEmpty(_config.Current.ParentPinHash) ? "yes" : "NO")}");
        if (_state.State.IsPaused(DateTimeOffset.UtcNow)) sb.AppendLine($"Rules paused until {_state.State.PausedUntilUtc:u}");
        foreach (var c in _config.Current.Children)
            sb.AppendLine($"Child {c.Name}: accounts [{string.Join(", ", c.WindowsAccounts)}]");
        foreach (var st in _sessions.Values)
        {
            var r = st.LastResult;
            sb.AppendLine($"Session {st.SessionId}: {st.Child} ({st.UserName}), helper {(DateTimeOffset.UtcNow - st.LastHeartbeat < HeartbeatTimeout ? "connected" : "NOT connected")}" +
                          (r is null ? "" : $", games left {Fmt(r.GamesLeftSeconds)}, YouTube left {Fmt(r.YouTubeLeftSeconds)}, screen left {Fmt(r.ScreenLeftSeconds)}, bedtime {(r.Bedtime ? "NOW" : r.NextBedtimeLocal?.ToString("HH:mm"))}"));
            if (st.YouTubeBlockReason is not null) sb.AppendLine($"  YouTube is blocked for {st.Child} because {st.YouTubeBlockReason}.");
        }
        if (_sessions.Count == 0) sb.AppendLine("No child is logged in right now.");
        sb.AppendLine($"YouTube block on this computer: {(HostsFile.IsBlocked ? "ON" : "off")}");
        return new AgentResponse { Ok = true, Diagnostics = sb.ToString() };

        static string Fmt(int s) => s < 0 ? "unlimited" : $"{s / 60} min";
    }

    private StatusInfo? ToStatus(TickResult? r, UsageDay usage)
    {
        if (r is null) return null;
        return new StatusInfo
        {
            GamesLeftSeconds = r.GamesLeftSeconds,
            GamesBudgetSeconds = r.GamesBudgetSeconds,
            YouTubeLeftSeconds = r.YouTubeLeftSeconds,
            YouTubeBudgetSeconds = r.YouTubeBudgetSeconds,
            Bedtime = r.Bedtime,
            NextBedtime = r.NextBedtimeLocal?.ToString("HH:mm"),
            UnlockAt = r.UnlockAtLocal?.ToString("HH:mm"),
            Paused = r.Paused,
            PausedUntil = r.Paused && _state.State.PausedUntilUtc is { } p
                ? TimeZoneInfo.ConvertTimeFromUtc(p.UtcDateTime, Zone).ToString("HH:mm") : null,
            CountingGames = r.CountingGames,
            CountingYouTube = r.CountingYouTube,
            ScreenLeftSeconds = r.ScreenLeftSeconds,
            ScreenBudgetSeconds = r.ScreenBudgetSeconds,
            ScreenTimeUp = r.ScreenTimeUp,
            CountingScreen = r.CountingScreen,
            GamesUsedSeconds = (int)usage.GamesSeconds,
            YouTubeUsedSeconds = (int)usage.YouTubeSeconds,
            ScreenUsedSeconds = (int)usage.ScreenSeconds,
            BonusGamesMinutes = usage.BonusGamesMinutes,
            BonusYouTubeMinutes = usage.BonusYouTubeMinutes,
            BonusScreenMinutes = usage.BonusScreenMinutes,
            ParentLocked = r.ParentLocked,
        };
    }

    private static string? Truncate(string? s, int max) => s is null || s.Length <= max ? s : s[..max];
}
