using System.Windows;
using Soundboard.ViewModels;

namespace Soundboard.Views;

public partial class PluginMarketplaceWindow : Window
{
    public PluginMarketplaceWindow(PluginMarketplaceViewModel viewModel)
    {
        InitializeComponent();
        DataContext = viewModel;
    }

    private async void Window_Loaded(object sender, RoutedEventArgs e)
    {
        if (DataContext is PluginMarketplaceViewModel vm)
        {
            await vm.LoadTrustStatusCommand.ExecuteAsync(null).ConfigureAwait(true);
            await vm.SearchCommunityCommand.ExecuteAsync(null).ConfigureAwait(true);
        }
    }
}
