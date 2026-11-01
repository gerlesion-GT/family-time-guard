using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using FamilyTimeGuard.Core;

namespace FamilyTimeGuard.Service;

internal sealed record RunningGame(int Pid, string Name, string? Path);

/// <summary>Finds game programs running in a session.</summary>
internal sealed class ProcessScanner
{
    // Program paths do not change while the program runs, so remember them.
    private readonly Dictionary<int, (string Name, string? Path)> _pathCache = [];

    public Dictionary<int, List<RunningGame>> FindGamesBySession(GameRules rules, ISet<int> sessionIds)
    {
        var result = sessionIds.ToDictionary(s => s, _ => new List<RunningGame>());
        var seen = new HashSet<int>();
        foreach (var p in Process.GetProcesses())
        {
            using (p)
            {
                try
                {
                    if (!result.TryGetValue(p.SessionId, out var list)) continue;
                    var name = p.ProcessName + ".exe";
                    seen.Add(p.Id);
                    string? path = null;
                    if (rules.NeedsPathCheck(name))
                    {
                        if (_pathCache.TryGetValue(p.Id, out var cached) && cached.Name == name) path = cached.Path;
                        else
                        {
                            path = Native.GetProcessPath(p.Id);
                            _pathCache[p.Id] = (name, path);
                        }
                    }
                    if (rules.IsGameProcess(name, path)) list.Add(new RunningGame(p.Id, name, path));
                }
                catch (Exception) { /* process exited while we looked at it */ }
            }
        }
        foreach (var pid in _pathCache.Keys.Where(k => !seen.Contains(k)).ToList()) _pathCache.Remove(pid);
        return result;
    }

    public static void Kill(RunningGame game)
    {
        try
        {
            using var p = Process.GetProcessById(game.Pid);
            if (!string.Equals(p.ProcessName + ".exe", game.Name, StringComparison.OrdinalIgnoreCase)) return;
            p.Kill(entireProcessTree: true);
        }
        catch (Exception ex) { Log.Error($"Could not close {game.Name}", ex); }
    }

    public static bool IsAlive(int pid)
    {
        try { using var p = Process.GetProcessById(pid); return !p.HasExited; }
        catch (Exception) { return false; }
    }
}

/// <summary>Starts the tray helper inside the child's session.</summary>
internal static class AgentLauncher
{
    /// <summary>
    /// The helper runs as the child (so it can see their screen), but its process is protected:
    /// only SYSTEM and administrators may end it, so the child cannot close it from Task Manager.
    /// "OW" (Owner Rights) stops the child from simply changing the protection back.
    /// 0x101000 = SYNCHRONIZE | PROCESS_QUERY_LIMITED_INFORMATION.
    /// </summary>
    private const string ProtectedSddl = "D:P(A;;GA;;;SY)(A;;GA;;;BA)(A;;0x101000;;;OW)(A;;0x101000;;;IU)(A;;0x101000;;;AU)";

    public static int Launch(int sessionId, bool protect = true)
    {
        var exe = Environment.ProcessPath ?? throw new InvalidOperationException("Cannot find my own program file.");
        IntPtr userToken = IntPtr.Zero, primary = IntPtr.Zero, env = IntPtr.Zero, sd = IntPtr.Zero;
        try
        {
            if (!Native.WTSQueryUserToken(sessionId, out userToken)) throw Native.LastError("WTSQueryUserToken");
            if (!Native.DuplicateTokenEx(userToken, Native.MAXIMUM_ALLOWED, IntPtr.Zero, Native.SecurityImpersonation, Native.TokenPrimary, out primary))
                throw Native.LastError("DuplicateTokenEx");
            if (!Native.CreateEnvironmentBlock(out env, primary, false)) env = IntPtr.Zero;
            if (protect && !Native.ConvertStringSecurityDescriptorToSecurityDescriptorW(ProtectedSddl, 1, out sd, out _))
                throw Native.LastError("ConvertStringSecurityDescriptor");

            var procAttr = new Native.SECURITY_ATTRIBUTES { nLength = Marshal.SizeOf<Native.SECURITY_ATTRIBUTES>(), lpSecurityDescriptor = sd };
            var threadAttr = procAttr;
            var si = new Native.STARTUPINFO { cb = Marshal.SizeOf<Native.STARTUPINFO>(), lpDesktop = @"winsta0\default" };
            var cmd = new StringBuilder($"\"{exe}\" --agent");
            var flags = Native.CREATE_NO_WINDOW | (env != IntPtr.Zero ? Native.CREATE_UNICODE_ENVIRONMENT : 0);

            if (!Native.CreateProcessAsUserW(primary, exe, cmd, ref procAttr, ref threadAttr, false, flags, env,
                    Path.GetDirectoryName(exe), ref si, out var pi))
                throw Native.LastError("CreateProcessAsUser");

            Native.CloseHandle(pi.hThread);
            Native.CloseHandle(pi.hProcess);
            return pi.dwProcessId;
        }
        finally
        {
            if (sd != IntPtr.Zero) Native.LocalFree(sd);
            if (env != IntPtr.Zero) Native.DestroyEnvironmentBlock(env);
            if (primary != IntPtr.Zero) Native.CloseHandle(primary);
            if (userToken != IntPtr.Zero) Native.CloseHandle(userToken);
        }
    }
}
