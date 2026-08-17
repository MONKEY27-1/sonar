using CommunityToolkit.Mvvm.ComponentModel;

namespace Soundboard.ViewModels;

/// <summary>One row in a MultiDeviceSelector — a device plus whether it's currently selected.
/// Deliberately doesn't write its own IsChecked back into settings (stays reusable/decoupled);
/// SettingsViewModel subscribes to PropertyChanged on each item it creates and does that
/// itself, the same shape reused for both the headphone and microphone lists.</summary>
public sealed partial class DeviceCheckItem : ObservableObject
{
    public DeviceCheckItem(string id, string name, bool isDefault, bool isChecked)
    {
        Id = id;
        Name = name;
        IsDefault = isDefault;
        _isChecked = isChecked;
    }

    public string Id { get; }
    public string Name { get; }
    public bool IsDefault { get; }

    [ObservableProperty] private bool _isChecked;
}
