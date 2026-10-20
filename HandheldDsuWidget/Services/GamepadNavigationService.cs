using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Threading;

namespace HandheldDsuWidget.Services;

/// <summary>
/// Polls XInput (xinput1_4.dll) when the widget window is active and focused,
/// enabling full D-Pad / Analog Stick navigation, A-button activation, LB/RB tab switching,
/// and Y-button quick calibration on Windows handhelds.
/// </summary>
public sealed class GamepadNavigationService : IDisposable
{
    private const int ERROR_SUCCESS = 0;
    private const short ThumbDeadzone = 16500;

    // XInput Button Masks
    private const ushort XINPUT_GAMEPAD_DPAD_UP = 0x0001;
    private const ushort XINPUT_GAMEPAD_DPAD_DOWN = 0x0002;
    private const ushort XINPUT_GAMEPAD_DPAD_LEFT = 0x0004;
    private const ushort XINPUT_GAMEPAD_DPAD_RIGHT = 0x0008;
    private const ushort XINPUT_GAMEPAD_LEFT_SHOULDER = 0x0100;
    private const ushort XINPUT_GAMEPAD_RIGHT_SHOULDER = 0x0200;
    private const ushort XINPUT_GAMEPAD_A = 0x1000;
    private const ushort XINPUT_GAMEPAD_B = 0x2000;
    private const ushort XINPUT_GAMEPAD_Y = 0x8000;

    [StructLayout(LayoutKind.Sequential)]
    private struct XINPUT_GAMEPAD
    {
        public ushort wButtons;
        public byte bLeftTrigger;
        public byte bRightTrigger;
        public short sThumbLX;
        public short sThumbLY;
        public short sThumbRX;
        public short sThumbRY;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct XINPUT_STATE
    {
        public uint dwPacketNumber;
        public XINPUT_GAMEPAD Gamepad;
    }

    [DllImport("xinput1_4.dll", EntryPoint = "XInputGetState")]
    private static extern int XInputGetState(uint dwUserIndex, out XINPUT_STATE pState);

    private readonly MainWindow _window;
    private readonly DispatcherTimer _timer;
    private ushort _prevButtons;
    private int _prevNavDir; // -1 = Up, 0 = Neutral, +1 = Down
    private DateTime _nextRepeatTimeUtc;
    private int _focusedIndex = -1;

    public GamepadNavigationService(MainWindow window)
    {
        _window = window;
        _timer = new DispatcherTimer
        {
            Interval = TimeSpan.FromMilliseconds(20)
        };
        _timer.Tick += OnTimerTick;
    }

    public void Start() => _timer.Start();

    public void Stop() => _timer.Stop();

    public void ResetFocusToFirstControl()
    {
        IReadOnlyList<Control> controls = _window.GetActiveTabNavigableControls();
        if (controls.Count > 0)
        {
            _focusedIndex = 0;
            FocusControl(controls[0]);
        }
    }

    private void OnTimerTick(object? sender, EventArgs e)
    {
        // Only process controller input when the widget window is visible and focused in foreground
        if (!_window.IsVisible || !_window.IsActive)
        {
            _prevButtons = 0;
            _prevNavDir = 0;
            return;
        }

        if (!TryGetActiveGamepadState(out XINPUT_GAMEPAD pad))
        {
            _prevButtons = 0;
            _prevNavDir = 0;
            return;
        }

        ushort buttons = pad.wButtons;
        ushort justPressed = (ushort)(buttons & ~_prevButtons);
        _prevButtons = buttons;

        // 1. Right Stick Vertical Scroll
        if (Math.Abs((int)pad.sThumbRY) > ThumbDeadzone)
        {
            double delta = pad.sThumbRY > 0 ? -18.0 : 18.0;
            _window.ScrollByVerticalOffset(delta);
        }

        // 2. LB / RB Tab Switching
        if ((justPressed & XINPUT_GAMEPAD_LEFT_SHOULDER) != 0)
        {
            _window.SwitchTab(showSettings: false);
            ResetFocusToFirstControl();
            return;
        }

        if ((justPressed & XINPUT_GAMEPAD_RIGHT_SHOULDER) != 0)
        {
            _window.SwitchTab(showSettings: true);
            ResetFocusToFirstControl();
            return;
        }

        // 3. Y Button Quick Calibration Shortcut
        if ((justPressed & XINPUT_GAMEPAD_Y) != 0)
        {
            _ = _window.TriggerCalibrationFromControllerAsync();
            return;
        }

        // 4. B Button Closes Open Dropdown
        if ((justPressed & XINPUT_GAMEPAD_B) != 0)
        {
            if (Keyboard.FocusedElement is ComboBox openCombo && openCombo.IsDropDownOpen)
            {
                openCombo.IsDropDownOpen = false;
                return;
            }
        }

        // 5. Up / Down Navigation (D-Pad or Left Stick) with Auto-Repeat
        int navDir = 0;
        if ((buttons & (XINPUT_GAMEPAD_DPAD_UP | XINPUT_GAMEPAD_DPAD_LEFT)) != 0 || pad.sThumbLY > ThumbDeadzone)
        {
            navDir = -1;
        }
        else if ((buttons & (XINPUT_GAMEPAD_DPAD_DOWN | XINPUT_GAMEPAD_DPAD_RIGHT)) != 0 || pad.sThumbLY < -ThumbDeadzone)
        {
            navDir = 1;
        }

        DateTime now = DateTime.UtcNow;
        if (navDir != 0)
        {
            if (navDir != _prevNavDir)
            {
                MoveFocus(navDir);
                _nextRepeatTimeUtc = now.AddMilliseconds(320);
            }
            else if (now >= _nextRepeatTimeUtc)
            {
                MoveFocus(navDir);
                _nextRepeatTimeUtc = now.AddMilliseconds(130);
            }
        }
        _prevNavDir = navDir;

        // 6. A Button Activates / Toggles Focused Control
        if ((justPressed & XINPUT_GAMEPAD_A) != 0)
        {
            ActivateFocusedControl();
        }
    }

    private void MoveFocus(int direction)
    {
        // If a ComboBox dropdown is open, cycle its selected item with Up/Down
        if (Keyboard.FocusedElement is ComboBox combo && combo.IsDropDownOpen && combo.Items.Count > 0)
        {
            int nextItem = Math.Clamp(combo.SelectedIndex + direction, 0, combo.Items.Count - 1);
            combo.SelectedIndex = nextItem;
            return;
        }

        IReadOnlyList<Control> controls = _window.GetActiveTabNavigableControls();
        if (controls.Count == 0)
            return;

        // Sync _focusedIndex with current Keyboard.FocusedElement if possible
        if (Keyboard.FocusedElement is Control currentFocused)
        {
            for (int i = 0; i < controls.Count; i++)
            {
                if (ReferenceEquals(controls[i], currentFocused))
                {
                    _focusedIndex = i;
                    break;
                }
            }
        }

        if (_focusedIndex < 0 || _focusedIndex >= controls.Count)
        {
            _focusedIndex = 0;
        }
        else
        {
            _focusedIndex = (_focusedIndex + direction + controls.Count) % controls.Count;
        }

        FocusControl(controls[_focusedIndex]);
    }

    private static void FocusControl(Control control)
    {
        if (!control.IsEnabled || !control.IsVisible)
            return;

        control.Focus();
        Keyboard.Focus(control);
        control.BringIntoView();
    }

    private void ActivateFocusedControl()
    {
        IReadOnlyList<Control> controls = _window.GetActiveTabNavigableControls();
        if (controls.Count == 0)
            return;

        Control? target = Keyboard.FocusedElement as Control;
        if (target is null || !controls.Contains(target))
        {
            _focusedIndex = Math.Clamp(_focusedIndex, 0, controls.Count - 1);
            target = controls[_focusedIndex];
            FocusControl(target);
        }

        switch (target)
        {
            case CheckBox checkBox when checkBox.IsEnabled:
                checkBox.IsChecked = !(checkBox.IsChecked == true);
                checkBox.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent, checkBox));
                break;

            case RadioButton radioButton when radioButton.IsEnabled:
                radioButton.IsChecked = true;
                break;

            case Button button when button.IsEnabled:
                button.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent, button));
                break;

            case ComboBox comboBox when comboBox.IsEnabled:
                comboBox.IsDropDownOpen = !comboBox.IsDropDownOpen;
                break;
        }
    }

    private static bool TryGetActiveGamepadState(out XINPUT_GAMEPAD activePad)
    {
        activePad = default;
        for (uint i = 0; i < 4; i++)
        {
            try
            {
                if (XInputGetState(i, out XINPUT_STATE state) == ERROR_SUCCESS)
                {
                    if (state.Gamepad.wButtons != 0 ||
                        Math.Abs((int)state.Gamepad.sThumbLX) > ThumbDeadzone ||
                        Math.Abs((int)state.Gamepad.sThumbLY) > ThumbDeadzone ||
                        Math.Abs((int)state.Gamepad.sThumbRX) > ThumbDeadzone ||
                        Math.Abs((int)state.Gamepad.sThumbRY) > ThumbDeadzone)
                    {
                        activePad = state.Gamepad;
                        return true;
                    }
                }
            }
            catch
            {
                // Ignore if xinput1_4.dll is unavailable
                return false;
            }
        }
        return false;
    }

    public void Dispose()
    {
        _timer.Stop();
    }
}
