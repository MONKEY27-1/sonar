using System.Windows;
using Soundboard.ViewModels;

namespace Soundboard.Views;

public partial class AdminWindow : Window
{
    public AdminWindow(AdminViewModel viewModel)
    {
        InitializeComponent();
        DataContext = viewModel;
    }

    private async void Window_Loaded(object sender, RoutedEventArgs e)
    {
        if (DataContext is AdminViewModel vm)
        {
            await vm.LoadUsersCommand.ExecuteAsync(null).ConfigureAwait(true);
        }
    }
}
