using System.Threading.Channels;
using FamilyTimeGuard.Core;
using FamilyTimeGuard.Service;

namespace FamilyTimeGuard.Remote;

/// <summary>
/// Talks to the parent's private Telegram bot: receives commands (long polling, outgoing connections only)
/// and sends replies, alerts and requests. Only the paired parent account is obeyed; everyone else is ignored.
/// </summary>
internal sealed class RemoteControl(GuardWorker worker)
{
    private sealed record Outgoing(string Text, IReadOnlyList<IReadOnlyList<TgButton>>? Buttons);

    private readonly Channel<Outgoing> _outbox = Channel.CreateBounded<Outgoing>(
        new BoundedChannelOptions(100) { FullMode = BoundedChannelFullMode.DropOldest });

    private TelegramApi? _api;
    private string? _apiKey;
    private DateTimeOffset _lastErrorLog = DateTimeOffset.MinValue;
    private bool _warnedStranger;

    public void Enqueue(string text, IReadOnlyList<IReadOnlyList<TgButton>>? buttons = null) =>
        _outbox.Writer.TryWrite(new Outgoing(text, buttons));

    public Task RunAsync(CancellationToken ct) => Task.WhenAll(PollLoop(ct), SendLoop(ct));

    private TelegramApi? Api(out TelegramSettings settings)
    {
        var (token, s) = worker.GetTelegram();
        settings = s;
        if (token is null) return null;
        var key = token + "|" + s.Proxy;
        if (_api is null || key != _apiKey)
        {
            _api?.Dispose();
            _api = new TelegramApi(token, s.Proxy);
            _apiKey = key;
        }
        return _api;
    }

    private async Task PollLoop(CancellationToken ct)
    {
        var delay = TimeSpan.FromSeconds(5);
        while (!ct.IsCancellationRequested)
        {
            try
            {
                var api = Api(out var s);
                if (api is null) { await Task.Delay(TimeSpan.FromSeconds(30), ct); continue; }

                var updates = await api.GetUpdates(worker.TelegramOffset, 50, ct);
                delay = TimeSpan.FromSeconds(5);
                foreach (var u in updates)
                {
                    worker.SetTelegramOffset(u.UpdateId + 1);
                    try { await Handle(api, s, u, ct); }
                    catch (TelegramException ex) { LogError("Could not answer a phone message: " + ex.Message); }
                }
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { break; }
            catch (Exception ex)
            {
                LogError("Phone remote: " + (ex is TelegramException ? ex.Message : ex.ToString()));
                try { await Task.Delay(delay, ct); } catch (OperationCanceledException) { break; }
                delay = TimeSpan.FromSeconds(Math.Min(delay.TotalSeconds * 2, 300));
            }
        }
    }

    private async Task Handle(TelegramApi api, TelegramSettings s, TgUpdate u, CancellationToken ct)
    {
        if (u.Message is { } m)
        {
            if (m.From?.Id != s.OwnerUserId || m.Chat?.Id != s.OwnerChatId)
            {
                // Not the parent: ignore. Tell the parent once, in case a child found the bot.
                if (!_warnedStranger)
                {
                    _warnedStranger = true;
                    Log.Info($"Ignored a phone message from an unknown Telegram user ({m.From?.Id}).");
                    Enqueue($"ℹ️ Someone else (Telegram user {m.From?.FirstName ?? "?"}) sent a message to this bot. It was ignored; only you can control {s.PcName}.");
                }
                return;
            }
            if (string.IsNullOrWhiteSpace(m.Text)) return;
            var reply = worker.ExecuteRemote(m.Text);
            await api.SendMessage(s.OwnerChatId!.Value, reply, null, ct);
        }
        else if (u.CallbackQuery is { } q)
        {
            if (q.From?.Id != s.OwnerUserId) { await api.AnswerCallback(q.Id, "Not allowed.", ct); return; }
            var (answer, newText) = worker.HandleCallback(q.Data, q.Message?.Text);
            await api.AnswerCallback(q.Id, answer, ct);
            if (newText is not null && q.Message?.Chat is { } chat)
                await api.EditMessage(chat.Id, q.Message.MessageId, newText, ct);
        }
    }

    private async Task SendLoop(CancellationToken ct)
    {
        try
        {
            await foreach (var msg in _outbox.Reader.ReadAllAsync(ct))
            {
                for (var attempt = 1; attempt <= 6; attempt++)
                {
                    try
                    {
                        var api = Api(out var s);
                        if (api is null) break; // remote not set up: drop
                        await api.SendMessage(s.OwnerChatId!.Value, msg.Text, msg.Buttons, ct);
                        break;
                    }
                    catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
                    catch (Exception ex)
                    {
                        LogError("Could not send a phone message: " + ex.Message);
                        await Task.Delay(TimeSpan.FromSeconds(Math.Min(10 * attempt * attempt, 300)), ct);
                    }
                }
            }
        }
        catch (OperationCanceledException) { }
    }

    private void LogError(string message)
    {
        // Avoid filling the log while offline: at most one line every 10 minutes.
        if (DateTimeOffset.UtcNow - _lastErrorLog < TimeSpan.FromMinutes(10)) return;
        _lastErrorLog = DateTimeOffset.UtcNow;
        Log.Error(message);
    }
}
