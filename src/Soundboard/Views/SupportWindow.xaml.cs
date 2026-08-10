using System.Windows;
using Soundboard.ViewModels;

namespace Soundboard.Views;

public partial class SupportWindow : Window
{
    public SupportWindow(SupportViewModel viewModel)
    {
        InitializeComponent();
        DataContext = viewModel;
    }

    private async void Window_Loaded(object sender, RoutedEventArgs e)
    {
        if (DataContext is SupportViewModel vm)
        {
            await vm.LoadCommand.ExecuteAsync(null).ConfigureAwait(true);
        }
    }
}
