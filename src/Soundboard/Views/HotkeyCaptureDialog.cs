using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using Soundboard.Core.Models;
using Soundboard.Helpers;

namespace Soundboard.Views;

public partial class HotkeyCaptureDialog : Window
{
    private readonly Button _captureButton;
    private bool _isCapturing;

    public HotkeyBinding? Result { get; private set; }

    public HotkeyCaptureDialog(string soundName, HotkeyBinding? currentHotkey)
    {
        Result = currentHotkey;

        Title = "Set Hotkey";
        Width = 420;
        Height = 220;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        ResizeMode = ResizeMode.NoResize;
        Background = (Brush)Application.Current.FindResource("SurfaceBrush")!;
        var textBrush = (Brush)Application.Current.FindResource("TextPrimaryBrush")!;
        var toolbarButtonStyle = (Style)Application.Current.FindResource("ToolbarButton")!;
        var accentButtonStyle = (Style)Application.Current.FindResource("AccentButton")!;

        var promptBlock = new TextBlock
        {
            Text = $"Hotkey for \"{soundName}\"",
            Foreground = textBrush,
            FontWeight = FontWeights.SemiBold,
            Margin = new Thickness(16, 16, 16, 8)
        };

        _captureButton = new Button
        {
            Content = currentHotkey?.DisplayName ?? "Click to set",
            Style = toolbarButtonStyle,
            Margin = new Thickness(16, 0, 16, 8),
            Padding = new Thickness(8),
            HorizontalContentAlignment = HorizontalAlignment.Center
        };
        _captureButton.Click += (_, _) =>
        {
            _isCapturing = true;
            _captureButton.Content = "Press a key (Esc to cancel)...";
        };
        _captureButton.PreviewKeyDown += CaptureButton_PreviewKeyDown;

        var hintBlock = new TextBlock
        {
            Text = "Click the box above, then press the key combo you want.",
            Foreground = textBrush,
            Opacity = 0.6,
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(16, 0, 16, 16)
        };

        var clearButton = new Button { Content = "Clear", Style = toolbarButtonStyle, Width = 80, Margin = new Thickness(0, 0, 8, 16) };
        clearButton.Click += (_, _) =>
        {
            Result = null;
            _captureButton.Content = "Click to set";
        };

        var okButton = new Button { Content = "OK", Style = accentButtonStyle, Width = 80, Margin = new Thickness(0, 0, 8, 16), IsDefault = true };
        okButton.Click += (_, _) => DialogResult = true;

        var cancelButton = new Button { Content = "Cancel", Style = toolbarButtonStyle, Width = 80, Margin = new Thickness(0, 0, 16, 16), IsCancel = true };

        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
        buttons.Children.Add(clearButton);
        buttons.Children.Add(okButton);
        buttons.Children.Add(cancelButton);

        var root = new StackPanel();
        root.Children.Add(promptBlock);
        root.Children.Add(_captureButton);
        root.Children.Add(hintBlock);
        root.Children.Add(buttons);

        Content = root;
    }

    private void CaptureButton_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (!_isCapturing) return;

        switch (HotkeyCaptureHelper.TryCapture(e, out var binding))
        {
            case HotkeyCaptureOutcome.Cancelled:
                _isCapturing = false;
                _captureButton.Content = Result?.DisplayName ?? "Click to set";
                break;
            case HotkeyCaptureOutcome.Captured:
                Result = binding;
                _isCapturing = false;
                _captureButton.Content = Result!.DisplayName;
                break;
            case HotkeyCaptureOutcome.StillWaiting:
                break;
        }
    }
}
