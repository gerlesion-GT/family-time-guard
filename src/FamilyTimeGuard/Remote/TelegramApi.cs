using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace FamilyTimeGuard.Remote;

// ---------------- Telegram Bot API data (only the fields we use) ----------------

internal sealed class TgUser
{
    public long Id { get; set; }
    public bool IsBot { get; set; }
    public string? FirstName { get; set; }
    public string? Username { get; set; }
}

internal sealed class TgChat
{
    public long Id { get; set; }
    public string? Type { get; set; }
}

internal sealed class TgMessage
{
    public long MessageId { get; set; }
    public TgUser? From { get; set; }
    public TgChat? Chat { get; set; }
    public string? Text { get; set; }
}

internal sealed class TgCallbackQuery
{
    public string Id { get; set; } = "";
    public TgUser? From { get; set; }
    public TgMessage? Message { get; set; }
    public string? Data { get; set; }
}

internal sealed class TgUpdate
{
    public long UpdateId { get; set; }
    public TgMessage? Message { get; set; }
    public TgCallbackQuery? CallbackQuery { get; set; }
}

internal sealed record TgButton(string Text, string Data);

internal sealed class TelegramException(string message) : Exception(message);

/// <summary>
/// Minimal Telegram Bot API client. Only makes outgoing HTTPS requests (no open ports on the PC).
/// The bot token is never written to logs or error messages.
/// </summary>
internal sealed class TelegramApi : IDisposable
{
    private static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        PropertyNameCaseInsensitive = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    private readonly string _token;
    private readonly HttpClient _http;

    public TelegramApi(string token, string? proxy)
    {
        _token = token.Trim();
        var handler = new SocketsHttpHandler { PooledConnectionLifetime = TimeSpan.FromMinutes(10) };
        if (!string.IsNullOrWhiteSpace(proxy))
        {
            handler.Proxy = new WebProxy(proxy);
            handler.UseProxy = true;
        }
        _http = new HttpClient(handler) { BaseAddress = new Uri("https://api.telegram.org/"), Timeout = TimeSpan.FromSeconds(75) };
    }

    public static bool LooksLikeToken(string? t) =>
        t is not null && System.Text.RegularExpressions.Regex.IsMatch(t.Trim(), @"^\d{5,15}:[A-Za-z0-9_-]{30,60}$");

    public Task<TgUser> GetMe(CancellationToken ct = default) => Call<TgUser>("getMe", new { }, ct);

    public Task<bool> DeleteWebhook(CancellationToken ct = default) => Call<bool>("deleteWebhook", new { drop_pending_updates = false }, ct);

    public Task<List<TgUpdate>> GetUpdates(long offset, int timeoutSeconds, CancellationToken ct = default) =>
        Call<List<TgUpdate>>("getUpdates", new { offset, timeout = timeoutSeconds, allowed_updates = new[] { "message", "callback_query" } }, ct);

    public async Task<long> SendMessage(long chatId, string text, IReadOnlyList<IReadOnlyList<TgButton>>? buttons = null, CancellationToken ct = default)
    {
        var msg = await Call<TgMessage>("sendMessage", new
        {
            chat_id = chatId,
            text = Limit(text),
            reply_markup = Keyboard(buttons),
        }, ct);
        return msg.MessageId;
    }

    public Task EditMessage(long chatId, long messageId, string text, CancellationToken ct = default) =>
        Call<JsonElement>("editMessageText", new { chat_id = chatId, message_id = messageId, text = Limit(text) }, ct);

    public Task AnswerCallback(string callbackId, string? text, CancellationToken ct = default) =>
        Call<bool>("answerCallbackQuery", new { callback_query_id = callbackId, text }, ct);

    private static object? Keyboard(IReadOnlyList<IReadOnlyList<TgButton>>? rows) =>
        rows is null ? null : new
        {
            inline_keyboard = rows.Select(r => r.Select(b => new { text = b.Text, callback_data = b.Data }).ToArray()).ToArray(),
        };

    private static string Limit(string text) => text.Length <= 4000 ? text : text[..3990] + "…";

    private async Task<T> Call<T>(string method, object payload, CancellationToken ct)
    {
        HttpResponseMessage response;
        try
        {
            response = await _http.PostAsJsonAsync($"bot{_token}/{method}", payload, Json, ct);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException && !ct.IsCancellationRequested)
        {
            throw new TelegramException("Cannot reach Telegram: " + Scrub(ex.Message));
        }

        using (response)
        {
            var body = await response.Content.ReadAsStringAsync(ct);
            JsonElement root;
            try { root = JsonDocument.Parse(body).RootElement; }
            catch (JsonException) { throw new TelegramException($"Unexpected answer from Telegram (HTTP {(int)response.StatusCode})."); }

            if (!root.TryGetProperty("ok", out var ok) || !ok.GetBoolean())
            {
                var desc = root.TryGetProperty("description", out var d) ? d.GetString() : null;
                throw new TelegramException(response.StatusCode switch
                {
                    HttpStatusCode.Unauthorized or HttpStatusCode.NotFound => "Telegram did not accept the bot key. Check it was copied completely from BotFather.",
                    HttpStatusCode.Conflict => "Another program is already reading this bot's messages. Each computer needs its own bot.",
                    _ => "Telegram error: " + Scrub(desc ?? response.StatusCode.ToString()),
                });
            }
            return root.GetProperty("result").Deserialize<T>(Json)!;
        }
    }

    private string Scrub(string s) => s.Replace(_token, "***", StringComparison.Ordinal);

    public void Dispose() => _http.Dispose();
}

/// <summary>Encrypts the bot key so it can only be decrypted on this computer.</summary>
internal static class TokenProtector
{
    private static readonly byte[] Entropy = "FamilyTimeGuard.Telegram.v1"u8.ToArray();

    public static string Protect(string token) =>
        Convert.ToBase64String(ProtectedData.Protect(Encoding.UTF8.GetBytes(token.Trim()), Entropy, DataProtectionScope.LocalMachine));

    public static string? Unprotect(string? encrypted)
    {
        if (string.IsNullOrEmpty(encrypted)) return null;
        try { return Encoding.UTF8.GetString(ProtectedData.Unprotect(Convert.FromBase64String(encrypted), Entropy, DataProtectionScope.LocalMachine)); }
        catch (Exception) { return null; }
    }
}
