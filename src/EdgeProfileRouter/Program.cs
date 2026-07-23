using System;
using System.Globalization;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using EdgeProfileRouter.Diagnostics;
using EdgeProfileRouter.Edge;
using EdgeProfileRouter.Registration;
using EdgeProfileRouter.Routing;

namespace EdgeProfileRouter;

/// <summary>
/// Entry point. This app has two faces:
/// <list type="bullet">
///   <item><b>URL launcher</b> — when Windows (or any app) opens a link, the exe is invoked with
///     the URL as its first argument. It matches the URL against the rules and launches Edge in
///     the right profile, then exits. No window is shown, so routing stays fast.</item>
///   <item><b>Settings app</b> — when run with no arguments, it opens the WPF settings window to
///     manage rules, profiles and the default-browser registration.</item>
/// </list>
/// CLI verbs (<c>--register</c>, <c>--unregister</c>, <c>--list-profiles</c>, etc.) cover
/// scripted setup and diagnostics.
/// </summary>
internal static class Program
{
    private const string MsgBoxTitle = "Edge Profile Router";

    [STAThread]
    private static int Main(string[] args)
    {
        args ??= Array.Empty<string>();
        Log.LoadSetting();

        try
        {
            if (args.Length == 0)
                return OpenSettings();

            string first = args[0].Trim();

            // --- Untrusted entry point (a clicked link / opened file) ---------------------
            // Windows substitutes the URL into "%1" and passes it as the first argument. A
            // crafted link could try quote/argument injection to append extra tokens, so when
            // the first argument is NOT one of our flags we treat ONLY it as data (a URL or file
            // path) and ignore everything else: no link can smuggle in --register or --browser.
            if (!first.StartsWith('-'))
            {
                UrlRouter.LaunchForUrl(RoutingConfig.Load(), first);
                return 0;
            }

            bool quiet = HasFlag(args, "--quiet") || HasFlag(args, "--silent");

            switch (first.ToLowerInvariant())
            {
                case "--register":
                    BrowserRegistration.Register();
                    Report(quiet,
                        "Registered as a candidate browser.\r\n\r\n"
                        + "To make it your default, open Settings > Default apps, find "
                        + "\"Edge Profile Router\", and set it for HTTP and HTTPS.");
                    return 0;

                case "--unregister":
                    BrowserRegistration.Unregister();
                    Report(quiet, "Unregistered. If it was your default browser, pick another in "
                        + "Settings > Default apps.");
                    return 0;

                case "--list-profiles":
                    Report(quiet, DescribeEnvironment());
                    return 0;

                case "--enable-logging":
                    Log.SetEnabled(true);
                    Report(quiet, "Diagnostic logging ENABLED.\r\nLog file: " + Log.FilePath);
                    return 0;

                case "--disable-logging":
                    Log.SetEnabled(false);
                    Report(quiet, "Diagnostic logging DISABLED.");
                    return 0;

                case "--browser":
                    // The "launch the browser itself" verb (from StartMenuInternet). Open Edge
                    // plainly; if a URL happens to follow, hand that to Edge too (no forced profile).
                    UrlRouter.LaunchPlain(RoutingConfig.Load(), args.Length > 1 ? args[1] : null);
                    return 0;

                case "--route":
                    if (args.Length < 2)
                    {
                        Report(quiet, "Usage: EdgeProfileRouter.exe --route <url>");
                        return 1;
                    }
                    UrlRouter.LaunchForUrl(RoutingConfig.Load(), args[1]);
                    return 0;

                case "--dry-run":
                    if (args.Length < 2)
                    {
                        Report(quiet, "Usage: EdgeProfileRouter.exe --dry-run <url>");
                        return 1;
                    }
                    RouteDecision d = UrlRouter.Decide(RoutingConfig.Load(), args[1]);
                    Report(quiet,
                        "Dry run: " + d.Target + "\r\n" + d.Explanation
                        + "\r\nProfile: " + (d.ForcesProfile ? d.ProfileDirectory : "(hand off to Edge)"));
                    return 0;

                case "--settings":
                    return OpenSettings();

                case "--version":
                    Report(quiet, "Edge Profile Router " + AppVersion());
                    return 0;

                case "--help":
                case "-h":
                case "-?":
                    Report(quiet, UsageText());
                    return 0;

                default:
                    Report(quiet, "Unknown option: " + first + "\r\n\r\n" + UsageText());
                    return 1;
            }
        }
        catch (Exception ex)
        {
            Log.Write("Fatal: " + ex);
            Report(false, "Error: " + ex.Message);
            return 1;
        }
    }

    private static int OpenSettings()
    {
        var app = new System.Windows.Application { ShutdownMode = System.Windows.ShutdownMode.OnMainWindowClose };
        var window = new SettingsWindow();
        return app.Run(window);
    }

    private static string DescribeEnvironment()
    {
        var sb = new StringBuilder();
        RegistrationInfo info = BrowserRegistration.GetInfo();
        sb.AppendLine("Edge Profile Router " + AppVersion());
        sb.AppendLine();
        sb.AppendLine("Executable : " + info.ExePath);
        sb.AppendLine("Config     : " + RoutingConfig.FilePath
            + (RoutingConfig.Exists() ? string.Empty : "  (not created yet)"));
        sb.AppendLine("Edge       : " + (EdgeLocator.Resolve() ?? "(not found)"));
        sb.AppendLine("Registered : " + (info.IsRegistered ? "yes" : "no"));
        sb.AppendLine("Default for https : "
            + (info.IsDefaultForHttps ? "yes (this app)" : "no — currently " + info.CurrentHttpsFriendly));
        sb.AppendLine();

        var profiles = EdgeProfiles.Enumerate();
        if (profiles.Count == 0)
        {
            sb.AppendLine("Edge profiles: none found (is Edge installed and set up?).");
        }
        else
        {
            sb.AppendLine("Edge profiles (use the Directory value in rules):");
            foreach (EdgeProfile p in profiles)
            {
                string tag = string.Equals(p.Directory, EdgeProfiles.LastUsedDirectory, StringComparison.OrdinalIgnoreCase)
                    ? "  [last used]" : string.Empty;
                sb.AppendLine(string.Format(
                    CultureInfo.InvariantCulture,
                    "  Directory=\"{0,-10}\"  Name=\"{1}\"  Account=\"{2}\"{3}",
                    p.Directory, p.Name, p.UserName, tag));
            }
        }
        return sb.ToString().TrimEnd();
    }

    private static string UsageText()
    {
        var sb = new StringBuilder();
        sb.AppendLine("Edge Profile Router " + AppVersion());
        sb.AppendLine();
        sb.AppendLine("Opens links in a specific Microsoft Edge profile based on host/path rules,");
        sb.AppendLine("and hands unmatched links to Edge to decide the profile.");
        sb.AppendLine();
        sb.AppendLine("Run with no arguments to open the settings window.");
        sb.AppendLine();
        sb.AppendLine("Setup (no admin required):");
        sb.AppendLine("  EdgeProfileRouter.exe --register     Register as a candidate browser");
        sb.AppendLine("  EdgeProfileRouter.exe --unregister   Remove the registration");
        sb.AppendLine("  (then choose it in Settings > Default apps for HTTP and HTTPS)");
        sb.AppendLine();
        sb.AppendLine("Diagnostics:");
        sb.AppendLine("  EdgeProfileRouter.exe --list-profiles   Show Edge profiles + status");
        sb.AppendLine("  EdgeProfileRouter.exe --route <url>     Route a URL now (test)");
        sb.AppendLine("  EdgeProfileRouter.exe --dry-run <url>   Show what a URL would do (no launch)");
        sb.AppendLine("  EdgeProfileRouter.exe --enable-logging  Turn on diagnostic logging");
        sb.AppendLine("  EdgeProfileRouter.exe --disable-logging Turn it back off");
        sb.AppendLine();
        sb.AppendLine("Logging is currently " + (Log.Enabled ? "ENABLED" : "DISABLED")
            + " (" + Log.FilePath + ").");
        return sb.ToString().TrimEnd();
    }

    private static string AppVersion() =>
        Assembly.GetExecutingAssembly().GetName().Version?.ToString(3) ?? "1.0.0";

    private static bool HasFlag(string[] args, string flag)
    {
        foreach (string a in args)
            if (string.Equals(a.Trim(), flag, StringComparison.OrdinalIgnoreCase))
                return true;
        return false;
    }

    // ---- Output: prefer the parent console; fall back to a message box -----------------------

    private static void Report(bool quiet, string message)
    {
        Log.Write("[report] " + message.Replace("\r\n", " | ").Replace('\n', '|'));
        if (quiet)
            return;

        if (ConsoleBridge.TryWriteLine(message))
            return;

        MessageBoxW(IntPtr.Zero, message, MsgBoxTitle, MB_OK | MB_ICONINFORMATION | MB_TOPMOST);
    }

    private const uint MB_OK = 0x0;
    private const uint MB_ICONINFORMATION = 0x40;
    private const uint MB_TOPMOST = 0x40000;

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int MessageBoxW(IntPtr hWnd, string text, string caption, uint type);

    /// <summary>
    /// Bridges console output for this WinExe: when launched from a terminal, attach to the
    /// parent console so CLI verbs can print there; when launched from Explorer there is no
    /// console and the caller falls back to a dialog.
    /// </summary>
    private static class ConsoleBridge
    {
        private const int ATTACH_PARENT_PROCESS = -1;
        private static bool? _attached;

        internal static bool TryWriteLine(string message)
        {
            if (!EnsureAttached())
                return false;
            try
            {
                Console.Out.WriteLine(message);
                Console.Out.Flush();
                return true;
            }
            catch
            {
                return false;
            }
        }

        private static bool EnsureAttached()
        {
            if (_attached is bool cached)
                return cached;

            bool ok = false;
            try
            {
                if (AttachConsole(ATTACH_PARENT_PROCESS))
                {
                    var writer = new StreamWriter(Console.OpenStandardOutput()) { AutoFlush = true };
                    Console.SetOut(writer);
                    ok = true;
                }
            }
            catch
            {
                ok = false;
            }

            _attached = ok;
            return ok;
        }

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool AttachConsole(int dwProcessId);
    }
}
