using System;
using System.IO;
using EdgeProfileRouter.Diagnostics;
using Microsoft.Win32;

namespace EdgeProfileRouter.Edge;

/// <summary>
/// Resolves the path to <c>msedge.exe</c>. Resolution order:
/// <list type="number">
///   <item>an explicit override (from config), if it exists;</item>
///   <item>the Windows <c>App Paths</c> registration (per-user then machine);</item>
///   <item>the standard install locations under Program Files.</item>
/// </list>
/// Returns <c>null</c> only if Edge genuinely cannot be found.
/// </summary>
internal static class EdgeLocator
{
    private const string AppPathsKey =
        @"SOFTWARE\Microsoft\Windows\CurrentVersion\App Paths\msedge.exe";

    internal static string? Resolve(string? overridePath = null)
    {
        if (!string.IsNullOrWhiteSpace(overridePath) && File.Exists(overridePath))
            return overridePath;

        string? fromAppPaths = FromAppPaths(Registry.CurrentUser) ?? FromAppPaths(Registry.LocalMachine);
        if (fromAppPaths is not null)
            return fromAppPaths;

        foreach (Environment.SpecialFolder pf in new[]
                 {
                     Environment.SpecialFolder.ProgramFilesX86,
                     Environment.SpecialFolder.ProgramFiles,
                 })
        {
            string root = Environment.GetFolderPath(pf);
            if (string.IsNullOrEmpty(root))
                continue;
            string candidate = Path.Combine(root, "Microsoft", "Edge", "Application", "msedge.exe");
            if (File.Exists(candidate))
                return candidate;
        }

        Log.Write("EdgeLocator: msedge.exe could not be located.");
        return null;
    }

    private static string? FromAppPaths(RegistryKey hive)
    {
        try
        {
            using RegistryKey? key = hive.OpenSubKey(AppPathsKey);
            if (key?.GetValue(null) is string path)
            {
                path = Environment.ExpandEnvironmentVariables(path.Trim('"'));
                if (File.Exists(path))
                    return path;
            }
        }
        catch
        {
            // ignore and fall through to the well-known locations
        }
        return null;
    }
}
