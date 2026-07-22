using System.Windows;
using System.Windows.Controls;
using WcfPrTriage.ViewModels;

namespace WcfPrTriage;

public partial class MainWindow : Window
{
    public MainWindow()
    {
        InitializeComponent();
    }

    private async void Window_Loaded(object sender, RoutedEventArgs e)
    {
        if (DataContext is MainViewModel vm)
            await vm.InitializeAsync();
    }

    private void FailureTree_SelectedItemChanged(object sender, RoutedPropertyChangedEventArgs<object> e)
    {
        if (DataContext is MainViewModel vm)
            vm.SelectedNode = e.NewValue as TriageNodeViewModel;
    }
}
