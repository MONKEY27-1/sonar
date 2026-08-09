using System.Windows;
using Soundboard.ViewModels.Auth;

namespace Soundboard.Views.Auth;

public partial class AuthWindow : Window
{
    public AuthWindow(AuthViewModel viewModel)
    {
        InitializeComponent();
        DataContext = viewModel;
        viewModel.Completed += (_, _) => Close();
    }
}
