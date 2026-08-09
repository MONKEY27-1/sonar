using System.Windows;
using Soundboard.ViewModels;

namespace Soundboard.Views;

public partial class FirstRunWizardWindow : Window
{
    private readonly FirstRunWizardViewModel _viewModel;

    public FirstRunWizardWindow(FirstRunWizardViewModel viewModel)
    {
        InitializeComponent();
        _viewModel = viewModel;
        DataContext = viewModel;
        _viewModel.CloseRequested += () => Close();
    }

    private async void Window_Loaded(object sender, RoutedEventArgs e)
    {
        await _viewModel.RunSetupAsync().ConfigureAwait(true);
    }
}
