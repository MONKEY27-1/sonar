using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using Soundboard.Core.Models;
using Soundboard.ViewModels;

namespace Soundboard.Views;

public partial class SettingsWindow : Window
{
    private string? _capturingHotkeySlot;

    public SettingsWindow(SettingsViewModel viewModel)
    {
        InitializeComponent();
        DataContext = viewModel;
        viewModel.ChangePasswordFieldsCleared += (_, _) =>
        {
            ChangePasswordCurrentBox.Password = string.Empty;
            ChangePasswordNewBox.Password = string.Empty;
            ChangePasswordConfirmBox.Password = string.Empty;
        };
    }

    private async void Window_Loaded(object sender, RoutedEventArgs e)
    {
        if (DataContext is not SettingsViewModel vm) return;

        await vm.LoadDevicesCommand.ExecuteAsync(null).ConfigureAwait(true);

        RefreshHotkeyButtonContent(HotkeyButtonStopAll, vm);
        RefreshHotkeyButtonContent(HotkeyButtonPauseAll, vm);
        RefreshHotkeyButtonContent(HotkeyButtonResumeAll, vm);
        RefreshHotkeyButtonContent(HotkeyButtonToggleLoop, vm);
        RefreshHotkeyButtonContent(HotkeyButtonToggleVoiceChanger, vm);
        RefreshHotkeyButtonContent(HotkeyButtonToggleQuickPlayOverlay, vm);
    }

    private void ChangePasswordCurrentBox_PasswordChanged(object sender, RoutedEventArgs e)
    {
        if (DataContext is SettingsViewModel vm) vm.ChangePasswordCurrent = ChangePasswordCurrentBox.Password;
    }

    private void ChangePasswordNewBox_PasswordChanged(object sender, RoutedEventArgs e)
    {
        if (DataContext is SettingsViewModel vm) vm.ChangePasswordNew = ChangePasswordNewBox.Password;
    }

    private void ChangePasswordConfirmBox_PasswordChanged(object sender, RoutedEventArgs e)
    {
        if (DataContext is SettingsViewModel vm) vm.ChangePasswordConfirm = ChangePasswordConfirmBox.Password;
    }

    private void HotkeyCapture_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button button) return;
        _capturingHotkeySlot = button.Tag as string;
        button.Content = "Press a key (Esc to cancel)...";
    }

    private void HotkeyCapture_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (sender is not Button button || DataContext is not SettingsViewModel vm) return;
        if (_capturingHotkeySlot is null || _capturingHotkeySlot != button.Tag as string) return;

        e.Handled = true;

        if (e.Key == Key.Escape)
        {
            _capturingHotkeySlot = null;
            RefreshHotkeyButtonContent(button, vm);
            return;
        }

        // Bare modifier presses (and the "System" pseudo-key WPF uses for Alt combos)
        // aren't a complete binding on their own — keep waiting for the actual key.
        var key = e.Key == Key.System ? e.SystemKey : e.Key;
        if (key is Key.LeftCtrl or Key.RightCtrl or Key.LeftAlt or Key.RightAlt or Key.LeftShift or Key.RightShift)
        {
            return;
        }

        var binding = new HotkeyBinding
        {
            KeyCode = KeyInterop.VirtualKeyFromKey(key),
            Ctrl = Keyboard.Modifiers.HasFlag(ModifierKeys.Control),
            Alt = Keyboard.Modifiers.HasFlag(ModifierKeys.Alt),
            Shift = Keyboard.Modifiers.HasFlag(ModifierKeys.Shift)
        };

        SetHotkeyBinding(vm, _capturingHotkeySlot, binding);
        _capturingHotkeySlot = null;
        RefreshHotkeyButtonContent(button, vm);
    }

    private void HotkeyClear_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: string slot } || DataContext is not SettingsViewModel vm) return;

        SetHotkeyBinding(vm, slot, null);

        var captureButton = GetCaptureButtonForSlot(slot);
        if (captureButton is not null)
        {
            RefreshHotkeyButtonContent(captureButton, vm);
        }
    }

    private Button? GetCaptureButtonForSlot(string slot) => slot switch
    {
        "StopAll" => HotkeyButtonStopAll,
        "PauseAll" => HotkeyButtonPauseAll,
        "ResumeAll" => HotkeyButtonResumeAll,
        "ToggleLoop" => HotkeyButtonToggleLoop,
        "ToggleVoiceChanger" => HotkeyButtonToggleVoiceChanger,
        "ToggleQuickPlayOverlay" => HotkeyButtonToggleQuickPlayOverlay,
        _ => null
    };

    private static void SetHotkeyBinding(SettingsViewModel vm, string slot, HotkeyBinding? binding)
    {
        switch (slot)
        {
            case "StopAll": vm.Settings.GlobalHotkeys.StopAll = binding; break;
            case "PauseAll": vm.Settings.GlobalHotkeys.PauseAll = binding; break;
            case "ResumeAll": vm.Settings.GlobalHotkeys.ResumeAll = binding; break;
            case "ToggleLoop": vm.Settings.GlobalHotkeys.ToggleLoop = binding; break;
            case "ToggleVoiceChanger": vm.Settings.GlobalHotkeys.ToggleVoiceChanger = binding; break;
            case "ToggleQuickPlayOverlay": vm.Settings.GlobalHotkeys.ToggleQuickPlayOverlay = binding; break;
        }
    }

    private static void RefreshHotkeyButtonContent(Button button, SettingsViewModel vm)
    {
        var binding = (button.Tag as string) switch
        {
            "StopAll" => vm.Settings.GlobalHotkeys.StopAll,
            "PauseAll" => vm.Settings.GlobalHotkeys.PauseAll,
            "ResumeAll" => vm.Settings.GlobalHotkeys.ResumeAll,
            "ToggleLoop" => vm.Settings.GlobalHotkeys.ToggleLoop,
            "ToggleVoiceChanger" => vm.Settings.GlobalHotkeys.ToggleVoiceChanger,
            "ToggleQuickPlayOverlay" => vm.Settings.GlobalHotkeys.ToggleQuickPlayOverlay,
            _ => null
        };

        button.Content = binding is null ? "Click to set" : binding.DisplayName;
    }
}
