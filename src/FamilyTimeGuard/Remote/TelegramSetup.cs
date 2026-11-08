using System.Security.Cryptography;
using System.Text;
using FamilyTimeGuard.Core;
using FamilyTimeGuard.Service;

namespace FamilyTimeGuard.Remote;

/// <summary>
/// Connects this computer to the parent's private Telegram bot.
/// The bot key is typed on this computer (never sent anywhere except Telegram) and stored encrypted.
/// Pairing: the computer shows a 6-digit code, the parent sends "/pair CODE" to the bot from their phone,
/// and from then on only that Telegram account is obeyed.
/// </summary>
internal static class TelegramSetup
{
    private static readonly TimeSpan PairingWindow = TimeSpan.FromMinutes(10);

    public static async Task<int> Run(string[] args)
    {
        var cfg = Cli.Load();
        var token = Environment.GetEnvironmentVariable("FTG_TELEGRAM_TOKEN");
        if (string.IsNullOrWhiteSpace(token))
        {
            Console.Write("Paste the bot key from BotFather, then press Enter: ");
            token = ReadSecret();
        }
        token = token?.Trim();
        if (!TelegramApi.LooksLikeToken(token))
        {
            Console.WriteLine("That doesn't look like a bot key. It looks like 1234567890:AAH... (numbers, a colon, then letters).");
            return 4;
        }

        var pcName = Cli.Arg(args, "--pc-name");
        if (string.IsNullOrWhiteSpace(pcName))
            pcName = cfg.ChildrenOnThisPc().FirstOrDefault() is { } c ? $"{c.Name}'s PC" : Environment.MachineName;

        using var api = new TelegramApi(token!, cfg.Telegram.Proxy);
        TgUser bot;
        try
        {
            bot = await api.GetMe();
            await api.DeleteWebhook();
        }
        catch (TelegramException ex)
        {
            Console.WriteLine(ex.Message);
            Console.WriteLine("If Telegram only works with a VPN on this network, the computer cannot reach it either.");
            return 5;
        }
        Console.WriteLine($"Connected to your bot @{bot.Username}.");

        // Skip messages sent to the bot before now.
        long offset = 0;
        try
        {
            var old = await api.GetUpdates(0, 0);
            if (old.Count > 0) offset = old.Max(u => u.UpdateId) + 1;
        }
        catch (TelegramException ex) { Console.WriteLine(ex.Message); return 5; }

        var code = RandomNumberGenerator.GetInt32(100000, 1000000).ToString();
        Console.WriteLine();
        Console.WriteLine("  Now, on your phone:");
        Console.WriteLine($"   1. Open Telegram and search for  @{bot.Username}");
        Console.WriteLine("   2. Tap Start");
        Console.WriteLine($"   3. Send this message:   /pair {code}");
        Console.WriteLine();
        Console.WriteLine($"  Waiting for your message (up to {PairingWindow.TotalMinutes:0} minutes)...");

        var deadline = DateTime.UtcNow + PairingWindow;
        while (DateTime.UtcNow < deadline)
        {
            List<TgUpdate> updates;
            try { updates = await api.GetUpdates(offset, 20); }
            catch (TelegramException ex)
            {
                Console.WriteLine("  " + ex.Message + " Retrying...");
                await Task.Delay(5000);
                continue;
            }

            foreach (var u in updates)
            {
                offset = u.UpdateId + 1;
                var m = u.Message;
                if (m?.From is null || m.Chat is null || m.Chat.Type != "private" || m.From.IsBot) continue;
                var text = (m.Text ?? "").Trim();
                var sentCode = text.StartsWith("/pair", StringComparison.OrdinalIgnoreCase) || text.StartsWith("/start", StringComparison.OrdinalIgnoreCase)
                    ? text.Split(' ', StringSplitOptions.RemoveEmptyEntries).Skip(1).FirstOrDefault()
                    : text;
                if (sentCode != code)
                {
                    await TrySend(api, m.Chat.Id, "To connect this bot to the computer, send /pair followed by the 6-digit code shown on the computer screen.");
                    continue;
                }

                // Paired.
                cfg = Cli.Load();
                cfg.Telegram.EncryptedBotToken = TokenProtector.Protect(token!);
                cfg.Telegram.BotUsername = bot.Username;
                cfg.Telegram.OwnerUserId = m.From.Id;
                cfg.Telegram.OwnerChatId = m.Chat.Id;
                cfg.Telegram.PcName = pcName!;
                Cli.Save(cfg);

                try { await api.GetUpdates(offset, 0); } catch (TelegramException) { } // acknowledge the pairing message
                var state = new StateStore();
                state.Load();
                state.State.TelegramOffset = offset;
                state.Save();

                await TrySend(api, m.Chat.Id,
                    $"✅ Paired! This chat now controls {pcName}. Only you can use it.\n\n{RemoteCommandParser.Help}");
                Console.WriteLine($"  Paired with {m.From.FirstName}. The phone remote for {pcName} is ready.");
                return 0;
            }
        }

        Console.WriteLine("  No pairing message arrived in time. You can try again later from the Start menu:");
        Console.WriteLine("  Family Time Guard > Set up phone remote (parents)");
        return 6;
    }

    public static async Task<int> Test()
    {
        var cfg = Cli.Load();
        var token = TokenProtector.Unprotect(cfg.Telegram.EncryptedBotToken);
        if (!cfg.Telegram.IsReady || token is null) { Console.WriteLine("The phone remote is not set up on this computer."); return 4; }
        using var api = new TelegramApi(token, cfg.Telegram.Proxy);
        try
        {
            await api.SendMessage(cfg.Telegram.OwnerChatId!.Value, $"👋 Test message from {cfg.Telegram.PcName}. The phone remote works.");
            Console.WriteLine("Test message sent. Check Telegram on your phone.");
            return 0;
        }
        catch (TelegramException ex) { Console.WriteLine(ex.Message); return 5; }
    }

    public static int TurnOff()
    {
        var cfg = Cli.Load();
        cfg.Telegram.EncryptedBotToken = null;
        cfg.Telegram.OwnerUserId = null;
        cfg.Telegram.OwnerChatId = null;
        cfg.Telegram.BotUsername = null;
        Cli.Save(cfg);
        Console.WriteLine("The phone remote is now disconnected from this computer.");
        return 0;
    }

    private static async Task TrySend(TelegramApi api, long chatId, string text)
    {
        try { await api.SendMessage(chatId, text); } catch (TelegramException) { }
    }

    private static string ReadSecret()
    {
        if (Console.IsInputRedirected) return Console.ReadLine() ?? "";
        var sb = new StringBuilder();
        while (true)
        {
            var key = Console.ReadKey(intercept: true);
            if (key.Key == ConsoleKey.Enter) break;
            if (key.Key == ConsoleKey.Backspace) { if (sb.Length > 0) { sb.Length--; Console.Write("\b \b"); } continue; }
            if (!char.IsControl(key.KeyChar)) { sb.Append(key.KeyChar); Console.Write('*'); }
        }
        Console.WriteLine();
        return sb.ToString();
    }
}
