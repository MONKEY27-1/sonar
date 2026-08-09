using System.Diagnostics;
using System.Runtime.InteropServices;
using Soundboard.Core.Interfaces;
using Soundboard.Core.Models;

namespace Soundboard.Services;

/// <summary>
/// Global low-level keyboard and mouse hook for hotkeys that work while games are focused.
/// </summary>
public sealed class HotkeyManager : IHotkeyManager
{
    private readonly Dictionary<string, SoundItem> _soundHotkeys = new();
    private GlobalHotkeys _globalHotkeys = new();
    private IntPtr _keyboardHook;
    private IntPtr _mouseHook;
    private LowLevelProc? _keyboardProc;
    private LowLevelProc? _mouseProc;
    private readonly HashSet<int> _heldPushToPlayKeys = [];

    public event EventHandler<(string SoundId, HotkeyAction Action)>? SoundHotkeyPressed;
    public event EventHandler<HotkeyAction>? GlobalHotkeyPressed;

    public HotkeyManager()
    {
        _keyboardProc = KeyboardHookCallback;
        _mouseProc = MouseHookCallback;
        _keyboardHook = SetHook(_keyboardProc, WH_KEYBOARD_LL);
        _mouseHook = SetHook(_mouseProc, WH_MOUSE_LL);
    }

    public void RegisterSoundHotkey(SoundItem sound)
    {
        if (sound.Hotkey is null)
        {
            _soundHotkeys.Remove(sound.Id);
            return;
        }

        _soundHotkeys[sound.Id] = sound;
    }

    public void UnregisterSoundHotkey(string soundId) => _soundHotkeys.Remove(soundId);

    public void RegisterGlobalHotkeys(GlobalHotkeys hotkeys) => _globalHotkeys = hotkeys;

    private IntPtr KeyboardHookCallback(int nCode, IntPtr wParam, IntPtr lParam)
    {
        if (nCode >= 0)
        {
            var hookStruct = Marshal.PtrToStructure<KBDLLHOOKSTRUCT>(lParam);
            var isKeyDown = wParam == (IntPtr)WM_KEYDOWN || wParam == (IntPtr)WM_SYSKEYDOWN;
            var isKeyUp = wParam == (IntPtr)WM_KEYUP || wParam == (IntPtr)WM_SYSKEYUP;

            if (isKeyDown || isKeyUp)
            {
                ProcessKey(hookStruct.vkCode, isKeyDown);
            }
        }

        return CallNextHookEx(_keyboardHook, nCode, wParam, lParam);
    }

    private IntPtr MouseHookCallback(int nCode, IntPtr wParam, IntPtr lParam)
    {
        if (nCode >= 0)
        {
            var isDown = wParam == (IntPtr)WM_LBUTTONDOWN || wParam == (IntPtr)WM_RBUTTONDOWN ||
                         wParam == (IntPtr)WM_MBUTTONDOWN || wParam == (IntPtr)WM_XBUTTONDOWN;
            var isUp = wParam == (IntPtr)WM_LBUTTONUP || wParam == (IntPtr)WM_RBUTTONUP ||
                       wParam == (IntPtr)WM_MBUTTONUP || wParam == (IntPtr)WM_XBUTTONUP;

            if (isDown || isUp)
            {
                var button = wParam switch
                {
                    var x when x == (IntPtr)WM_LBUTTONDOWN || x == (IntPtr)WM_LBUTTONUP => 1,
                    var x when x == (IntPtr)WM_RBUTTONDOWN || x == (IntPtr)WM_RBUTTONUP => 2,
                    var x when x == (IntPtr)WM_MBUTTONDOWN || x == (IntPtr)WM_MBUTTONUP => 3,
                    _ => 4
                };

                ProcessMouse(button, isDown);
            }
        }

        return CallNextHookEx(_mouseHook, nCode, wParam, lParam);
    }

    private void ProcessKey(int vkCode, bool isDown)
    {
        var ctrl = (GetAsyncKeyState(VK_CONTROL) & 0x8000) != 0;
        var alt = (GetAsyncKeyState(VK_MENU) & 0x8000) != 0;
        var shift = (GetAsyncKeyState(VK_SHIFT) & 0x8000) != 0;

        if (TryMatchGlobal(vkCode, false, ctrl, alt, shift, isDown)) return;

        foreach (var (soundId, sound) in _soundHotkeys)
        {
            var hotkey = sound.Hotkey!;
            if (hotkey.IsMouseButton) continue;
            if (!Matches(hotkey, vkCode, ctrl, alt, shift)) continue;

            if (hotkey.PushToPlay)
            {
                if (isDown && _heldPushToPlayKeys.Add(vkCode))
                {
                    SoundHotkeyPressed?.Invoke(this, (soundId, HotkeyAction.PushToPlayDown));
                }
                else if (!isDown && _heldPushToPlayKeys.Remove(vkCode))
                {
                    SoundHotkeyPressed?.Invoke(this, (soundId, HotkeyAction.PushToPlayUp));
                }
            }
            else if (isDown)
            {
                SoundHotkeyPressed?.Invoke(this, (soundId, HotkeyAction.Play));
            }
        }
    }

    private void ProcessMouse(int button, bool isDown)
    {
        var ctrl = (GetAsyncKeyState(VK_CONTROL) & 0x8000) != 0;
        var alt = (GetAsyncKeyState(VK_MENU) & 0x8000) != 0;
        var shift = (GetAsyncKeyState(VK_SHIFT) & 0x8000) != 0;

        foreach (var (soundId, sound) in _soundHotkeys)
        {
            var hotkey = sound.Hotkey!;
            if (!hotkey.IsMouseButton || hotkey.KeyCode != button) continue;
            if (hotkey.Ctrl != ctrl || hotkey.Alt != alt || hotkey.Shift != shift) continue;

            if (hotkey.PushToPlay)
            {
                SoundHotkeyPressed?.Invoke(this, (soundId, isDown ? HotkeyAction.PushToPlayDown : HotkeyAction.PushToPlayUp));
            }
            else if (isDown)
            {
                SoundHotkeyPressed?.Invoke(this, (soundId, HotkeyAction.Play));
            }
        }
    }

    private bool TryMatchGlobal(int keyCode, bool isMouse, bool ctrl, bool alt, bool shift, bool isDown)
    {
        if (!isDown) return false;

        if (Matches(_globalHotkeys.StopAll, keyCode, ctrl, alt, shift, isMouse))
        {
            GlobalHotkeyPressed?.Invoke(this, HotkeyAction.StopAll);
            return true;
        }

        if (Matches(_globalHotkeys.PauseAll, keyCode, ctrl, alt, shift, isMouse))
        {
            GlobalHotkeyPressed?.Invoke(this, HotkeyAction.PauseAll);
            return true;
        }

        if (Matches(_globalHotkeys.ResumeAll, keyCode, ctrl, alt, shift, isMouse))
        {
            GlobalHotkeyPressed?.Invoke(this, HotkeyAction.ResumeAll);
            return true;
        }

        if (Matches(_globalHotkeys.ToggleLoop, keyCode, ctrl, alt, shift, isMouse))
        {
            GlobalHotkeyPressed?.Invoke(this, HotkeyAction.ToggleLoop);
            return true;
        }

        if (Matches(_globalHotkeys.ToggleVoiceChanger, keyCode, ctrl, alt, shift, isMouse))
        {
            GlobalHotkeyPressed?.Invoke(this, HotkeyAction.ToggleVoiceChanger);
            return true;
        }

        if (Matches(_globalHotkeys.ToggleQuickPlayOverlay, keyCode, ctrl, alt, shift, isMouse))
        {
            GlobalHotkeyPressed?.Invoke(this, HotkeyAction.ToggleQuickPlayOverlay);
            return true;
        }

        return false;
    }

    private static bool Matches(HotkeyBinding? binding, int keyCode, bool ctrl, bool alt, bool shift, bool isMouse = false)
    {
        if (binding is null) return false;
        if (binding.IsMouseButton != isMouse) return false;
        return binding.KeyCode == keyCode &&
               binding.Ctrl == ctrl &&
               binding.Alt == alt &&
               binding.Shift == shift;
    }

    private static bool Matches(HotkeyBinding binding, int keyCode, bool ctrl, bool alt, bool shift)
        => binding.KeyCode == keyCode && binding.Ctrl == ctrl && binding.Alt == alt && binding.Shift == shift;

    private static IntPtr SetHook(LowLevelProc proc, int hookType)
    {
        using var curProcess = Process.GetCurrentProcess();
        using var curModule = curProcess.MainModule!;
        return SetWindowsHookEx(hookType, proc, GetModuleHandle(curModule.ModuleName), 0);
    }

    public void Dispose()
    {
        if (_keyboardHook != IntPtr.Zero)
        {
            UnhookWindowsHookEx(_keyboardHook);
            _keyboardHook = IntPtr.Zero;
        }

        if (_mouseHook != IntPtr.Zero)
        {
            UnhookWindowsHookEx(_mouseHook);
            _mouseHook = IntPtr.Zero;
        }
    }

    private delegate IntPtr LowLevelProc(int nCode, IntPtr wParam, IntPtr lParam);

    [StructLayout(LayoutKind.Sequential)]
    private struct KBDLLHOOKSTRUCT
    {
        public int vkCode;
        public int scanCode;
        public int flags;
        public int time;
        public IntPtr dwExtraInfo;
    }

    private const int WH_KEYBOARD_LL = 13;
    private const int WH_MOUSE_LL = 14;
    private const int WM_KEYDOWN = 0x0100;
    private const int WM_KEYUP = 0x0101;
    private const int WM_SYSKEYDOWN = 0x0104;
    private const int WM_SYSKEYUP = 0x0105;
    private const int WM_LBUTTONDOWN = 0x0201;
    private const int WM_LBUTTONUP = 0x0202;
    private const int WM_RBUTTONDOWN = 0x0204;
    private const int WM_RBUTTONUP = 0x0205;
    private const int WM_MBUTTONDOWN = 0x0207;
    private const int WM_MBUTTONUP = 0x0208;
    private const int WM_XBUTTONDOWN = 0x020B;
    private const int WM_XBUTTONUP = 0x020C;
    private const int VK_CONTROL = 0x11;
    private const int VK_MENU = 0x12;
    private const int VK_SHIFT = 0x10;

    [DllImport("user32.dll", CharSet = CharSet.Auto, SetLastError = true)]
    private static extern IntPtr SetWindowsHookEx(int idHook, LowLevelProc lpfn, IntPtr hMod, uint dwThreadId);

    [DllImport("user32.dll", CharSet = CharSet.Auto, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool UnhookWindowsHookEx(IntPtr hhk);

    [DllImport("user32.dll", CharSet = CharSet.Auto, SetLastError = true)]
    private static extern IntPtr CallNextHookEx(IntPtr hhk, int nCode, IntPtr wParam, IntPtr lParam);

    [DllImport("kernel32.dll", CharSet = CharSet.Auto, SetLastError = true)]
    private static extern IntPtr GetModuleHandle(string lpModuleName);

    [DllImport("user32.dll")]
    private static extern short GetAsyncKeyState(int vKey);
}
