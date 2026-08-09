using System.Windows;
using System.Windows.Controls;
using Soundboard.ViewModels.Auth;

namespace Soundboard.Views.Auth;

public partial class RegisterView : UserControl
{
    public RegisterView() => InitializeComponent();

    private void RegisterPasswordBox_PasswordChanged(object sender, RoutedEventArgs e)
    {
        if (DataContext is AuthViewModel vm) vm.RegisterPassword = RegisterPasswordBox.Password;
    }

    private void RegisterConfirmPasswordBox_PasswordChanged(object sender, RoutedEventArgs e)
    {
        if (DataContext is AuthViewModel vm) vm.RegisterConfirmPassword = RegisterConfirmPasswordBox.Password;
    }
}
