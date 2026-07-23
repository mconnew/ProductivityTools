using System;
using System.Globalization;
using System.IO;
using Microsoft.Win32;

namespace EdgeProfileRouter.Diagnostics;

/// <summary>
/// Lightweight per-user diagnostic logging, mirroring the OutlookLinkHandler pattern.
///
/// Logging is OFF unless the user opts in (HKCU DWORD <c>LoggingEnabled = 1</c>). The
/// preference lives under <c>HKCU\Software\EdgeProfileRouter</c>, deliberately separate from
/// the browser registration so it survives <c>--unregister</c>. When enabled, lines are
/// appended to <c>%TEMP%\EdgeProfileRouter.log</c>. Logging must never throw — a broken log
/// file can never be allowed to stop a URL from opening.
/// </summary>
internal static class Log
{
    internal const string SettingsKeyPath = @"Software\EdgeProfileRouter";
    private const string LoggingValueName = "LoggingEnabled";

    private static readonly string LogPath =
        Path.Combine(Path.GetTempPath(), "EdgeProfileRouter.log");

    private static bool _enabled;

    internal static string FilePath => LogPath;

    internal static bool Enabled => _enabled;

    /// <summary>Reads the persisted logging preference once at process start.</summary>
    internal static void LoadSetting()
    {
        try
        {
            using RegistryKey? key = Registry.CurrentUser.OpenSubKey(SettingsKeyPath);
            _enabled = key?.GetValue(LoggingValueName) is int v && v != 0;
        }
        catch
        {
            _enabled = false;
        }
    }

    internal static void SetEnabled(bool enabled)
    {
        using RegistryKey key = Registry.CurrentUser.CreateSubKey(SettingsKeyPath);
        key.SetValue(LoggingValueName, enabled ? 1 : 0, RegistryValueKind.DWord);
        _enabled = enabled;
    }

    internal static void Write(string message)
    {
        if (!_enabled)
            return;

        try
        {
            File.AppendAllText(
                LogPath,
                DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff", CultureInfo.InvariantCulture)
                    + "  " + message + Environment.NewLine);
        }
        catch
        {
            // Never let logging break routing.
        }
    }
}
