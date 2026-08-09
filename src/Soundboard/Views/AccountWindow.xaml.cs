using System.Windows;
using Soundboard.ViewModels;

namespace Soundboard.Views;

public partial class AccountWindow : Window
{
    public AccountWindow(AccountViewModel viewModel)
    {
        InitializeComponent();
        DataContext = viewModel;
    }

    private async void Window_Loaded(object sender, RoutedEventArgs e)
    {
        if (DataContext is AccountViewModel vm)
        {
            await vm.LoadCommand.ExecuteAsync(null).ConfigureAwait(true);
        }
    }
}
