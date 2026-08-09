using System.Windows;
using System.Windows.Controls;
using Soundboard.ViewModels.Auth;

namespace Soundboard.Views.Auth;

public partial class LoginView : UserControl
{
    public LoginView() => InitializeComponent();

    // PasswordBox.Password is deliberately not a dependency property (WPF avoids keeping
    // plaintext passwords in the binding/undo infrastructure), so it can't be bound from XAML —
    // this is the standard, minimal way to get it into the view model: relay the raw event,
    // no business logic here.
    private void LoginPasswordBox_PasswordChanged(object sender, RoutedEventArgs e)
    {
        if (DataContext is AuthViewModel vm) vm.LoginPassword = LoginPasswordBox.Password;
    }
}
