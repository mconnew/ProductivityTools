using System.Windows;
using WcfPrTriage.Services;

namespace WcfPrTriage.Views;

public partial class SettingsWindow : Window
{
    public SettingsWindow(AppSettings current)
    {
        InitializeComponent();
        OwnerBox.Text = current.Owner;
        RepoBox.Text = current.Repo;
        DraftsBox.IsChecked = current.IncludeDrafts;
        AutoRefreshBox.Text = current.AutoRefreshSeconds.ToString();
        TokenBox.Password = current.GitHubToken ?? string.Empty;
        Result = current;
    }

    public AppSettings Result { get; private set; }

    private void Save_Click(object sender, RoutedEventArgs e)
    {
        string owner = OwnerBox.Text.Trim();
        string repo = RepoBox.Text.Trim();
        if (string.IsNullOrWhiteSpace(owner) || string.IsNullOrWhiteSpace(repo))
        {
            MessageBox.Show(this, "Owner and repo are required.", "Settings", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        int autoRefresh = 120;
        string autoText = AutoRefreshBox.Text.Trim();
        if (autoText.Length > 0 && (!int.TryParse(autoText, out autoRefresh) || autoRefresh < 0))
        {
            MessageBox.Show(this, "Auto-refresh must be a whole number of seconds (0 to disable).", "Settings", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        Result = new AppSettings
        {
            Owner = owner,
            Repo = repo,
            IncludeDrafts = DraftsBox.IsChecked == true,
            AutoRefreshSeconds = autoRefresh,
            GitHubToken = string.IsNullOrWhiteSpace(TokenBox.Password) ? null : TokenBox.Password,
        };
        DialogResult = true;
    }

    private void Cancel_Click(object sender, RoutedEventArgs e) => DialogResult = false;
}
