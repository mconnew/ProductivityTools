using System;
using System.Diagnostics;
using EdgeProfileRouter.Diagnostics;
using Microsoft.Win32;

namespace EdgeProfileRouter.Registration;

/// <summary>Snapshot of the app's default-browser registration state (for the settings UI).</summary>
/// <param name="IsRegistered">True when the app is registered as a candidate browser.</param>
/// <param name="IsDefaultForHttps">True when Windows currently routes https to this app.</param>
/// <param name="CurrentHttpsProgId">The ProgId Windows uses for https today (may be empty).</param>
/// <param name="CurrentHttpsFriendly">A friendly name for that ProgId.</param>
/// <param name="ExePath">The executable path the registration points at.</param>
internal sealed record RegistrationInfo(
    bool IsRegistered,
    bool IsDefaultForHttps,
    string CurrentHttpsProgId,
    string CurrentHttpsFriendly,
    string ExePath);

/// <summary>
/// Registers (and unregisters) this executable as a selectable Windows default browser, entirely
/// under <c>HKEY_CURRENT_USER</c> — no elevation required.
///
/// <para>The registration follows the standard Windows "default programs" contract:</para>
/// <list type="bullet">
///   <item>a <b>ProgId</b> (<c>EdgeProfileRouterHTM</c>) whose <c>shell\open\command</c> invokes
///     this exe with the clicked URL;</item>
///   <item>a <b>StartMenuInternet</b> client entry with a <c>Capabilities</c> block that maps the
///     <c>http</c>/<c>https</c> URL associations (and <c>.htm</c>/<c>.html</c> files) to that ProgId;</item>
///   <item>a <b>RegisteredApplications</b> pointer to the Capabilities block.</item>
/// </list>
///
/// <para>Windows 10/11 deliberately forbids an app from programmatically making itself the default
/// browser (the per-user choice is protected by a signed hash). Registration only makes the app
/// <i>eligible</i>; the user then selects it in Settings → Default apps, which
/// <see cref="OpenDefaultAppsSettings"/> opens.</para>
/// </summary>
internal static class BrowserRegistration
{
    internal const string AppRegistrationName = "EdgeProfileRouter";
    internal const string ProgId = "EdgeProfileRouterHTM";
    private const string FriendlyName = "Edge Profile Router";
    private const string Description =
        "Routes links to the right Microsoft Edge profile based on host/path rules.";

    private const string StartMenuInternetPath = @"Software\Clients\StartMenuInternet\" + AppRegistrationName;
    private const string CapabilitiesPath = StartMenuInternetPath + @"\Capabilities";
    private const string ClassesProgIdPath = @"Software\Classes\" + ProgId;
    private const string RegisteredAppsPath = @"Software\RegisteredApplications";

    internal static string ExePath => Environment.ProcessPath
        ?? Process.GetCurrentProcess().MainModule?.FileName
        ?? "EdgeProfileRouter.exe";

    /// <summary>Writes the full HKCU registration. Idempotent — safe to run repeatedly.</summary>
    internal static void Register()
    {
        string exe = ExePath;
        string iconRef = exe + ",0";
        string urlCommand = "\"" + exe + "\" \"%1\"";
        string browserCommand = "\"" + exe + "\" --browser";

        // 1) ProgId: how a clicked URL / opened .htm file reaches this exe.
        using (RegistryKey progId = Registry.CurrentUser.CreateSubKey(ClassesProgIdPath))
        {
            progId.SetValue(string.Empty, FriendlyName, RegistryValueKind.String);
            progId.SetValue("FriendlyTypeName", FriendlyName, RegistryValueKind.String);

            using (RegistryKey app = progId.CreateSubKey("Application"))
            {
                app.SetValue("ApplicationName", FriendlyName, RegistryValueKind.String);
                app.SetValue("ApplicationIcon", iconRef, RegistryValueKind.String);
                app.SetValue("ApplicationDescription", Description, RegistryValueKind.String);
            }

            using (RegistryKey icon = progId.CreateSubKey("DefaultIcon"))
                icon.SetValue(string.Empty, iconRef, RegistryValueKind.String);

            using (RegistryKey cmd = progId.CreateSubKey(@"shell\open\command"))
                cmd.SetValue(string.Empty, urlCommand, RegistryValueKind.String);
        }

        // 2) StartMenuInternet client + Capabilities (what makes it a listed "web browser").
        using (RegistryKey client = Registry.CurrentUser.CreateSubKey(StartMenuInternetPath))
        {
            client.SetValue(string.Empty, FriendlyName, RegistryValueKind.String);

            using (RegistryKey icon = client.CreateSubKey("DefaultIcon"))
                icon.SetValue(string.Empty, iconRef, RegistryValueKind.String);

            // Launching the "browser" itself (e.g. from the Start menu) opens Edge plainly.
            using (RegistryKey cmd = client.CreateSubKey(@"shell\open\command"))
                cmd.SetValue(string.Empty, browserCommand, RegistryValueKind.String);

            using (RegistryKey caps = client.CreateSubKey("Capabilities"))
            {
                caps.SetValue("ApplicationName", FriendlyName, RegistryValueKind.String);
                caps.SetValue("ApplicationIcon", iconRef, RegistryValueKind.String);
                caps.SetValue("ApplicationDescription", Description, RegistryValueKind.String);

                using (RegistryKey startMenu = caps.CreateSubKey("StartMenu"))
                    startMenu.SetValue("StartMenuInternet", AppRegistrationName, RegistryValueKind.String);

                using (RegistryKey url = caps.CreateSubKey("URLAssociations"))
                {
                    url.SetValue("http", ProgId, RegistryValueKind.String);
                    url.SetValue("https", ProgId, RegistryValueKind.String);
                }

                using (RegistryKey file = caps.CreateSubKey("FileAssociations"))
                {
                    file.SetValue(".htm", ProgId, RegistryValueKind.String);
                    file.SetValue(".html", ProgId, RegistryValueKind.String);
                }
            }
        }

        // 3) Advertise the Capabilities block to Windows.
        using (RegistryKey reg = Registry.CurrentUser.CreateSubKey(RegisteredAppsPath))
            reg.SetValue(AppRegistrationName, CapabilitiesPath, RegistryValueKind.String);

        Log.Write("Registered as candidate browser. Exe = " + exe);
    }

    /// <summary>Removes the entire HKCU registration written by <see cref="Register"/>.</summary>
    internal static void Unregister()
    {
        TryDeleteTree(ClassesProgIdPath);
        TryDeleteTree(StartMenuInternetPath);

        try
        {
            using RegistryKey? reg = Registry.CurrentUser.OpenSubKey(RegisteredAppsPath, writable: true);
            if (reg?.GetValue(AppRegistrationName) is not null)
                reg.DeleteValue(AppRegistrationName, throwOnMissingValue: false);
        }
        catch (Exception ex)
        {
            Log.Write("Unregister (RegisteredApplications) note: " + ex.Message);
        }

        Log.Write("Unregistered candidate browser.");
    }

    /// <summary>Reads the current registration + default-browser status.</summary>
    internal static RegistrationInfo GetInfo()
    {
        bool registered;
        try
        {
            using RegistryKey? reg = Registry.CurrentUser.OpenSubKey(RegisteredAppsPath);
            using RegistryKey? progId = Registry.CurrentUser.OpenSubKey(ClassesProgIdPath);
            registered = reg?.GetValue(AppRegistrationName) is not null && progId is not null;
        }
        catch
        {
            registered = false;
        }

        string httpsProgId = GetDefaultHttpsProgId();
        bool isDefault = string.Equals(httpsProgId, ProgId, StringComparison.OrdinalIgnoreCase);
        return new RegistrationInfo(registered, isDefault, httpsProgId, FriendlyProgId(httpsProgId), ExePath);
    }

    /// <summary>Opens the Windows Settings "Default apps" page so the user can pick this app.</summary>
    internal static void OpenDefaultAppsSettings()
    {
        try
        {
            Process.Start(new ProcessStartInfo("ms-settings:defaultapps") { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            Log.Write("OpenDefaultAppsSettings failed — " + ex.Message);
        }
    }

    /// <summary>The ProgId Windows currently uses to open https links for this user.</summary>
    private static string GetDefaultHttpsProgId()
    {
        try
        {
            using RegistryKey? key = Registry.CurrentUser.OpenSubKey(
                @"Software\Microsoft\Windows\Shell\Associations\UrlAssociations\https\UserChoice");
            return key?.GetValue("ProgId") as string ?? string.Empty;
        }
        catch
        {
            return string.Empty;
        }
    }

    private static string FriendlyProgId(string progId) => progId switch
    {
        ProgId => FriendlyName,
        "" => "(not set)",
        "MSEdgeHTM" or "MSEdgeMHT" => "Microsoft Edge",
        "ChromeHTML" => "Google Chrome",
        "FirefoxURL" or "FirefoxHTML" => "Mozilla Firefox",
        "IE.HTTP" => "Internet Explorer",
        "BraveHTML" => "Brave",
        var s when s.StartsWith("MSEdge", StringComparison.OrdinalIgnoreCase) => "Microsoft Edge",
        var s when s.StartsWith("Chrome", StringComparison.OrdinalIgnoreCase) => "Google Chrome",
        var s when s.StartsWith("Firefox", StringComparison.OrdinalIgnoreCase) => "Mozilla Firefox",
        _ => progId,
    };

    private static void TryDeleteTree(string path)
    {
        try
        {
            Registry.CurrentUser.DeleteSubKeyTree(path, throwOnMissingSubKey: false);
        }
        catch (Exception ex)
        {
            Log.Write("Delete '" + path + "' note: " + ex.Message);
        }
    }
}
