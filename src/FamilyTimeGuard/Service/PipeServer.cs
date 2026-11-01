using System.IO.Pipes;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text;
using FamilyTimeGuard.Core;

namespace FamilyTimeGuard.Service;

/// <summary>Who sent a request: the process id and Windows session id, as reported by Windows (cannot be faked by the sender).</summary>
internal sealed record PipeClient(int ProcessId, int SessionId);

/// <summary>Named pipe the tray helper uses to talk to the Guard. One JSON line in, one JSON line out.</summary>
internal sealed class PipeServer(Func<AgentRequest, PipeClient, AgentResponse> handler)
{
    private const int Listeners = 4;

    public Task RunAsync(CancellationToken ct) =>
        Task.WhenAll(Enumerable.Range(0, Listeners).Select(_ => Task.Run(() => ListenLoop(ct), ct)));

    private async Task ListenLoop(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                await using var pipe = Create();
                await pipe.WaitForConnectionAsync(ct);
                await HandleAsync(pipe, ct);
            }
            catch (OperationCanceledException) { break; }
            catch (Exception ex)
            {
                Log.Error("Pipe error", ex);
                await Task.Delay(500, ct).ContinueWith(_ => { });
            }
        }
    }

    private static NamedPipeServerStream Create()
    {
        var security = new PipeSecurity();
        security.AddAccessRule(new PipeAccessRule(new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null), PipeAccessRights.FullControl, AccessControlType.Allow));
        security.AddAccessRule(new PipeAccessRule(new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null), PipeAccessRights.FullControl, AccessControlType.Allow));
        security.AddAccessRule(new PipeAccessRule(new SecurityIdentifier(WellKnownSidType.AuthenticatedUserSid, null), PipeAccessRights.ReadWrite, AccessControlType.Allow));
        return NamedPipeServerStreamAcl.Create(Protocol.PipeName, PipeDirection.InOut, Listeners,
            PipeTransmissionMode.Byte, PipeOptions.Asynchronous, 4096, 4096, security);
    }

    private async Task HandleAsync(NamedPipeServerStream pipe, CancellationToken ct)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(5));

        var client = new PipeClient(-1, -1);
        if (Native.GetNamedPipeClientProcessId(pipe.SafePipeHandle.DangerousGetHandle(), out var pid) &&
            Native.ProcessIdToSessionId(pid, out var sid))
            client = new PipeClient(pid, sid);

        var reader = new StreamReader(pipe, Encoding.UTF8, false, 4096, leaveOpen: true);
        var line = await reader.ReadLineAsync(timeout.Token);
        if (line is null || line.Length > 64_000) return;

        AgentResponse response;
        try
        {
            var request = Protocol.Deserialize<AgentRequest>(line) ?? new AgentRequest();
            response = handler(request, client);
        }
        catch (Exception ex)
        {
            Log.Error("Bad request from helper", ex);
            response = new AgentResponse { Ok = false, Error = "Bad request" };
        }

        var bytes = Encoding.UTF8.GetBytes(Protocol.Serialize(response) + "\n");
        await pipe.WriteAsync(bytes, timeout.Token);
        await pipe.FlushAsync(timeout.Token);
        try { pipe.WaitForPipeDrain(); } catch (IOException) { }
    }
}

/// <summary>Client side, used by the tray helper and the "status" command.</summary>
internal static class PipeClientCall
{
    public static AgentResponse Send(AgentRequest request, int timeoutMs = 2000)
    {
        using var pipe = new NamedPipeClientStream(".", Protocol.PipeName, PipeDirection.InOut, PipeOptions.None);
        pipe.Connect(timeoutMs);
        var bytes = Encoding.UTF8.GetBytes(Protocol.Serialize(request) + "\n");
        pipe.Write(bytes);
        pipe.Flush();
        using var reader = new StreamReader(pipe, Encoding.UTF8);
        var line = reader.ReadLine() ?? throw new IOException("No answer from Family Time Guard service.");
        return Protocol.Deserialize<AgentResponse>(line) ?? throw new IOException("Unreadable answer.");
    }
}
