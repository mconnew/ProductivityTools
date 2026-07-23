using System;
using System.Collections.ObjectModel;
using System.Linq;
using System.Windows;
using EdgeProfileRouter.Edge;
using EdgeProfileRouter.Registration;
using EdgeProfileRouter.Routing;

namespace EdgeProfileRouter;

/// <summary>
/// The settings window: manage routing rules, view detected Edge profiles, register/unregister
/// as a default browser, and dry-run URLs to see which profile they would open in.
/// </summary>
public partial class SettingsWindow : Window
{
    /// <summary>Editable routing rules bound to the grid (in priority order).</summary>
    public ObservableCollection<RoutingRule> Rules { get; } = new();

    /// <summary>Detected Edge profiles, offered in the per-rule profile picker.</summary>
    public ObservableCollection<EdgeProfile> Profiles { get; } = new();

    public SettingsWindow()
    {
        InitializeComponent();
        DataContext = this;

        foreach (EdgeProfile p in EdgeProfiles.Enumerate())
            Profiles.Add(p);
        ProfilesList.ItemsSource = Profiles;

        // First run (no config yet): seed the two example rules so the feature is discoverable.
        bool seeded = !RoutingConfig.Exists();
        RoutingConfig config = seeded
            ? RoutingConfig.CreateSeededExample(Profiles)
            : RoutingConfig.Load();

        foreach (RoutingRule rule in config.Rules)
            Rules.Add(rule);

        EdgePathBox.Text = config.EdgePathOverride ?? string.Empty;

        RefreshStatus();

        if (seeded)
            SaveHintText.Text = "Example rules added — review and click Save.";
    }

    private void RefreshStatus()
    {
        RegistrationInfo info = BrowserRegistration.GetInfo();
        string edge = EdgeLocator.Resolve(NullIfBlank(EdgePathBox.Text)) ?? "Edge not found";

        string reg = info.IsRegistered ? "Registered as a candidate browser." : "Not registered.";
        string def = info.IsDefaultForHttps
            ? "This app is your default for HTTPS."
            : $"Current HTTPS default: {info.CurrentHttpsFriendly}.";

        StatusText.Text = $"{reg}  {def}\r\nEdge: {edge}";

        RegisterButton.IsEnabled = !info.IsRegistered;
        UnregisterButton.IsEnabled = info.IsRegistered;
    }

    private void OnRegister(object sender, RoutedEventArgs e)
    {
        try
        {
            BrowserRegistration.Register();
            RefreshStatus();
            MessageBox.Show(this,
                "Registered.\r\n\r\nNow click \"Set as default…\" and choose \"Edge Profile Router\" "
                + "for HTTP and HTTPS in Windows Settings.",
                "Edge Profile Router", MessageBoxButton.OK, MessageBoxImage.Information);
        }
        catch (Exception ex)
        {
            ShowError("Registration failed", ex);
        }
    }

    private void OnUnregister(object sender, RoutedEventArgs e)
    {
        try
        {
            BrowserRegistration.Unregister();
            RefreshStatus();
        }
        catch (Exception ex)
        {
            ShowError("Unregister failed", ex);
        }
    }

    private void OnOpenDefaultApps(object sender, RoutedEventArgs e)
        => BrowserRegistration.OpenDefaultAppsSettings();

    private void OnAddRule(object sender, RoutedEventArgs e)
    {
        var rule = new RoutingRule
        {
            Name = "New rule",
            HostPattern = "example.com",
            ProfileDirectory = Profiles.FirstOrDefault()?.Directory ?? string.Empty,
        };
        Rules.Add(rule);
        RulesGrid.SelectedItem = rule;
        RulesGrid.ScrollIntoView(rule);
    }

    private void OnRemoveRule(object sender, RoutedEventArgs e)
    {
        if (RulesGrid.SelectedItem is RoutingRule rule)
            Rules.Remove(rule);
    }

    private void OnMoveUp(object sender, RoutedEventArgs e) => Move(-1);

    private void OnMoveDown(object sender, RoutedEventArgs e) => Move(+1);

    private void Move(int delta)
    {
        int i = RulesGrid.SelectedIndex;
        int j = i + delta;
        if (i < 0 || j < 0 || j >= Rules.Count)
            return;
        Rules.Move(i, j);
        RulesGrid.SelectedIndex = j;
    }

    private void OnTestUrl(object sender, RoutedEventArgs e)
    {
        RoutingConfig config = BuildConfigFromUi();
        RouteDecision decision = UrlRouter.Decide(config, TestUrlBox.Text);
        TestResultText.Text = decision.Explanation;
    }

    private void OnSave(object sender, RoutedEventArgs e)
    {
        try
        {
            RoutingConfig config = BuildConfigFromUi();
            config.Save();
            SaveHintText.Text = "Saved.";
            RefreshStatus();
        }
        catch (Exception ex)
        {
            ShowError("Could not save configuration", ex);
        }
    }

    private void OnClose(object sender, RoutedEventArgs e) => Close();

    /// <summary>Snapshots the current UI into a <see cref="RoutingConfig"/> (used by Save and Test).</summary>
    private RoutingConfig BuildConfigFromUi()
    {
        // Keep each rule's remembered profile label in sync for readability in the JSON.
        foreach (RoutingRule rule in Rules)
        {
            EdgeProfile? match = Profiles.FirstOrDefault(
                p => string.Equals(p.Directory, rule.ProfileDirectory, StringComparison.OrdinalIgnoreCase));
            rule.ProfileLabel = match?.DisplayLabel;
        }

        return new RoutingConfig
        {
            EdgePathOverride = NullIfBlank(EdgePathBox.Text),
            Rules = Rules.ToList(),
        };
    }

    private static string? NullIfBlank(string? value)
        => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private void ShowError(string title, Exception ex)
        => MessageBox.Show(this, ex.Message, title, MessageBoxButton.OK, MessageBoxImage.Error);
}
