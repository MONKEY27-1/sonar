using System.Windows;
using System.Windows.Controls;
using Soundboard.ViewModels.Auth;

namespace Soundboard.Views.Auth;

public partial class ForgotPasswordView : UserControl
{
    public ForgotPasswordView() => InitializeComponent();

    private void ForgotNewPasswordBox_PasswordChanged(object sender, RoutedEventArgs e)
    {
        if (DataContext is AuthViewModel vm) vm.ForgotNewPassword = ForgotNewPasswordBox.Password;
    }

    private void ForgotConfirmPasswordBox_PasswordChanged(object sender, RoutedEventArgs e)
    {
        if (DataContext is AuthViewModel vm) vm.ForgotConfirmPassword = ForgotConfirmPasswordBox.Password;
    }
}
