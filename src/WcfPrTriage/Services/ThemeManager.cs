using System.Collections.ObjectModel;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using Microsoft.Win32;

namespace WcfPrTriage.Services;

/// <summary>
/// Applies a light or dark palette at runtime and follows the OS app-theme setting.
///
/// The whole UI references its neutral and status colours through a fixed set of brush keys
/// (BgBrush, TextBrush, FailureBrush, …) using <c>DynamicResource</c>. The concrete brushes live in
/// two swappable dictionaries — <c>Palette.Dark.xaml</c> and <c>Palette.Light.xaml</c>. Switching the
/// theme is simply a matter of replacing the merged palette dictionary; every DynamicResource
/// reference re-resolves and the whole window retints live, with no per-reference edits.
/// </summary>
public sealed class ThemeManager
{
    private const string DarkPalettePath = "Assets/Palette.Dark.xaml";
    private const string LightPalettePath = "Assets/Palette.Light.xaml";

    // A key that only ever exists in a palette dictionary — used to spot the current palette entry.
    private const string PaletteMarkerKey = "BgBrush";

    private const string PersonalizeKey =
        @"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize";

    private readonly Application _app;
    private Window? _window;
    private bool? _forcedDark; // startup override: null => follow OS.
    private bool _isDark;
    private bool _hooked;

    public ThemeManager(Application app) => _app = app;

    public bool IsDark => _isDark;

    /// <summary>
    /// Applies the initial theme (an optional <c>--theme light|dark|auto</c> arg wins over the OS
    /// setting) and subscribes to OS theme changes so the app follows them live.
    /// </summary>
    public void Initialize(string[]? args)
    {
        _forcedDark = ParseOverride(args);
        Apply(ResolveDark());

        if (!_hooked)
        {
            SystemEvents.UserPreferenceChanged += OnUserPreferenceChanged;
            _hooked = true;
        }
    }

    /// <summary>Ties the window's title bar chrome to the current theme (and future changes).</summary>
    public void AttachWindow(Window window)
    {
        _window = window;
        if (new WindowInteropHelper(window).Handle != IntPtr.Zero)
            ApplyTitleBar(window, _isDark);
        else
            window.SourceInitialized += (_, _) => ApplyTitleBar(window, _isDark);
    }

    /// <summary>Unsubscribes from the static <see cref="SystemEvents"/> handler to avoid a leak on exit.</summary>
    public void Shutdown()
    {
        if (_hooked)
        {
            SystemEvents.UserPreferenceChanged -= OnUserPreferenceChanged;
            _hooked = false;
        }
    }

    private bool ResolveDark() => _forcedDark ?? IsOsDark();

    private void OnUserPreferenceChanged(object sender, UserPreferenceChangedEventArgs e)
    {
        // Theme changes surface under the General category; ignore an explicit startup override.
        if (e.Category != UserPreferenceCategory.General || _forcedDark is not null)
            return;

        // The event can arrive on a non-UI thread — marshal the swap to the dispatcher.
        _app.Dispatcher.BeginInvoke(new Action(() =>
        {
            bool dark = IsOsDark();
            if (dark != _isDark)
                Apply(dark);
        }));
    }

    private void Apply(bool dark)
    {
        _isDark = dark;

        var palette = new ResourceDictionary
        {
            Source = new Uri(dark ? DarkPalettePath : LightPalettePath, UriKind.Relative),
        };

        Collection<ResourceDictionary> merged = _app.Resources.MergedDictionaries;

        // Add the new palette first so DynamicResource always has a source, then drop the old one(s).
        merged.Add(palette);
        for (int i = merged.Count - 2; i >= 0; i--)
        {
            if (!ReferenceEquals(merged[i], palette) && merged[i].Contains(PaletteMarkerKey))
                merged.RemoveAt(i);
        }

        if (_window is not null)
            ApplyTitleBar(_window, dark);
    }

    private static bool? ParseOverride(string[]? args)
    {
        if (args is null)
            return null;

        for (int i = 0; i < args.Length; i++)
        {
            string arg = args[i];
            string? value = null;

            if (arg.StartsWith("--theme=", StringComparison.OrdinalIgnoreCase))
                value = arg["--theme=".Length..];
            else if (arg.Equals("--theme", StringComparison.OrdinalIgnoreCase) && i + 1 < args.Length)
                value = args[i + 1];

            if (value is null)
                continue;

            if (value.Equals("dark", StringComparison.OrdinalIgnoreCase))
                return true;
            if (value.Equals("light", StringComparison.OrdinalIgnoreCase))
                return false;
            if (value.Equals("auto", StringComparison.OrdinalIgnoreCase))
                return null;
        }

        return null;
    }

    private static bool IsOsDark()
    {
        try
        {
            using RegistryKey? key = Registry.CurrentUser.OpenSubKey(PersonalizeKey);
            // AppsUseLightTheme: 1 => light, 0 => dark. Absent => assume light (the Windows default).
            if (key?.GetValue("AppsUseLightTheme") is int v)
                return v == 0;
        }
        catch { /* registry unreadable — fall through to the light default */ }

        return false;
    }

    private static void ApplyTitleBar(Window window, bool dark)
    {
        try
        {
            IntPtr hwnd = new WindowInteropHelper(window).Handle;
            if (hwnd == IntPtr.Zero)
                return;

            int useDark = dark ? 1 : 0;
            // DWMWA_USE_IMMERSIVE_DARK_MODE is 20 on Windows 10 2004+ / Windows 11; older builds used 19.
            if (DwmSetWindowAttribute(hwnd, 20, ref useDark, sizeof(int)) != 0)
                DwmSetWindowAttribute(hwnd, 19, ref useDark, sizeof(int));
        }
        catch { /* DWM attribute is best-effort cosmetic polish */ }
    }

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);
}
