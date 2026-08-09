using System.Windows;
using Soundboard.Core.Interfaces;

namespace Soundboard.Services;

public sealed class NotificationService : INotificationService
{
    public void ShowInfo(string title, string message) => ShowToast(title, message, "Info");

    public void ShowError(string title, string message) => ShowToast(title, message, "Error");

    public void ShowSuccess(string title, string message) => ShowToast(title, message, "Success");

    private static void ShowToast(string title, string message, string kind)
    {
        Application.Current?.Dispatcher.Invoke(() =>
        {
            // Lightweight in-app notification via status bar; can be extended to toast UI.
            if (Application.Current.MainWindow?.DataContext is ViewModels.MainViewModel vm)
            {
                vm.StatusMessage = $"[{kind}] {title}: {message}";
            }
        });
    }
}
