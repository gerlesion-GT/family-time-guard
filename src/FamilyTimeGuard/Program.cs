using FamilyTimeGuard.Agent;
using FamilyTimeGuard.Core;
using FamilyTimeGuard.Service;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Hosting.WindowsServices;

namespace FamilyTimeGuard;

/// <summary>
/// One program, several jobs:
///   --service   the background Guard (started by Windows)
///   --agent     the tray helper (started by the Guard inside the child's session)
///   status | init-config | set-pin | add-account | check-config | cleanup   (used by the installer)
/// </summary>
internal static class Program
{
    [STAThread]
    private static int Main(string[] args)
    {
        var command = args.Length > 0 ? args[0].ToLowerInvariant() : "";

        if (command == "--service" || (command == "" && WindowsServiceHelpers.IsWindowsService()))
            return RunService();

        if (command == "--agent")
            return RunAgent();

        try
        {
            return command switch
            {
                "status" => Cli.Status(),
                "init-config" => Cli.InitConfig(args),
                "set-pin" => Cli.SetPin(),
                "add-account" => Cli.AddAccount(args),
                "check-config" => Cli.CheckConfig(),
                "cleanup" => Cli.Cleanup(),
                _ => Cli.Help(),
            };
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine("Error: " + ex.Message);
            return 1;
        }
    }

    private static int RunService()
    {
        var builder = Host.CreateApplicationBuilder(); // args are not settings, so do not pass them
        builder.Services.AddWindowsService(o => o.ServiceName = "FamilyTimeGuard");
        builder.Services.AddHostedService<GuardWorker>();
        builder.Build().Run();
        return 0;
    }

    private static int RunAgent()
    {
        using var mutex = new Mutex(true, @"Local\FamilyTimeGuard.Agent", out var isFirst);
        if (!isFirst) return 0;

        Application.ThreadException += (_, e) => AgentLog.Write("UI error: " + e.Exception);
        AppDomain.CurrentDomain.UnhandledException += (_, e) => AgentLog.Write("Crash: " + e.ExceptionObject);
        Application.SetUnhandledExceptionMode(UnhandledExceptionMode.CatchException);
        ApplicationConfiguration.Initialize();
        Application.Run(new AgentContext());
        return 0;
    }
}

internal static class Cli
{
    public static int Help()
    {
        Console.WriteLine("""
            Family Time Guard
            -----------------
            This program runs in the background. Install it with Install.cmd.

            Commands (run as administrator):
              FamilyTimeGuard.exe status                         Show what the Guard is doing
              FamilyTimeGuard.exe check-config                   Check the settings file for mistakes
              FamilyTimeGuard.exe init-config --child NAME --account WINDOWS_USER [--force]
              FamilyTimeGuard.exe add-account --child NAME --account WINDOWS_USER
              FamilyTimeGuard.exe set-pin                        Reads a new parent PIN from the keyboard
            """);
        return 0;
    }

    public static int Status()
    {
        try
        {
            var r = PipeClientCall.Send(new AgentRequest { Type = "status" }, 5000);
            Console.WriteLine(r.Diagnostics);
            return 0;
        }
        catch (Exception ex)
        {
            Console.WriteLine("The Family Time Guard service is NOT responding: " + ex.Message);
            return 2;
        }
    }

    public static int InitConfig(string[] args)
    {
        var child = Arg(args, "--child");
        var account = Arg(args, "--account");
        var force = args.Contains("--force", StringComparer.OrdinalIgnoreCase);
        if (File.Exists(Paths.ConfigFile) && !force)
        {
            Console.WriteLine("Settings already exist; keeping them. (Use --force to start over.)");
            return account is null || child is null ? 0 : AddAccount(args);
        }

        var cfg = GuardConfig.CreateDefault();
        if (child is not null && account is not null)
        {
            var profile = cfg.FindChildByName(child) ?? throw new ArgumentException($"Unknown child '{child}'. Known: {string.Join(", ", cfg.Children.Select(c => c.Name))}");
            profile.WindowsAccounts.Add(account);
        }
        Save(cfg);
        Console.WriteLine($"Created settings at {Paths.ConfigFile}");
        return 0;
    }

    public static int AddAccount(string[] args)
    {
        var child = Arg(args, "--child") ?? throw new ArgumentException("--child is required");
        var account = Arg(args, "--account") ?? throw new ArgumentException("--account is required");
        var cfg = Load();
        var profile = cfg.FindChildByName(child) ?? throw new ArgumentException($"Unknown child '{child}'.");
        foreach (var c in cfg.Children) c.WindowsAccounts.RemoveAll(a => a.Equals(account, StringComparison.OrdinalIgnoreCase));
        profile.WindowsAccounts.Add(account);
        Save(cfg);
        Console.WriteLine($"Windows account '{account}' now belongs to {profile.Name}.");
        return 0;
    }

    public static int SetPin()
    {
        // The installer passes the PIN in FTG_NEW_PIN; otherwise read it from the keyboard.
        var pin = Environment.GetEnvironmentVariable("FTG_NEW_PIN");
        if (string.IsNullOrEmpty(pin))
        {
            Console.Write("New parent PIN (4-12 digits): ");
            pin = Console.ReadLine();
        }
        pin = pin?.Trim().Trim('\uFEFF', '\u200B').Trim();
        if (!PinHasher.IsValidPin(pin)) throw new ArgumentException("The PIN must be 4 to 12 digits.");
        var cfg = Load();
        cfg.ParentPinHash = PinHasher.Hash(pin!);
        Save(cfg);
        Console.WriteLine("Parent PIN saved.");
        return 0;
    }

    public static int CheckConfig()
    {
        var errors = Load(validate: false).Validate();
        if (errors.Count == 0) { Console.WriteLine("Settings look good."); return 0; }
        Console.WriteLine("Problems found in the settings:");
        foreach (var e in errors) Console.WriteLine(" - " + e);
        return 3;
    }

    public static int Cleanup()
    {
        HostsFile.SetBlocked(false, []);
        BrowserPolicies.Remove();
        Console.WriteLine("Removed the YouTube block and browser settings.");
        return 0;
    }

    private static GuardConfig Load(bool validate = true)
    {
        if (!File.Exists(Paths.ConfigFile)) throw new FileNotFoundException($"No settings file at {Paths.ConfigFile}. Run init-config first.");
        var json = File.ReadAllText(Paths.ConfigFile);
        return validate
            ? GuardConfig.Parse(json)
            : System.Text.Json.JsonSerializer.Deserialize<GuardConfig>(json, GuardConfig.JsonOptions) ?? new GuardConfig();
    }

    private static void Save(GuardConfig cfg)
    {
        Directory.CreateDirectory(Paths.DataDir);
        var tmp = Paths.ConfigFile + ".tmp";
        File.WriteAllText(tmp, cfg.ToJson());
        File.Move(tmp, Paths.ConfigFile, overwrite: true);
    }

    private static string? Arg(string[] args, string name)
    {
        var i = Array.FindIndex(args, a => a.Equals(name, StringComparison.OrdinalIgnoreCase));
        return i >= 0 && i + 1 < args.Length ? args[i + 1] : null;
    }
}
