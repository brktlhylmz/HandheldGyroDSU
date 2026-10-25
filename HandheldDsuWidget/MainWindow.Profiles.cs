using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using Microsoft.Win32;
using HandheldDsuServer.Models;
using HandheldDsuServer.Services;
using HandheldDsuWidget.Services;

namespace HandheldDsuWidget;

public partial class MainWindow
{
    private void AppBarProfileButton_Click(object sender, RoutedEventArgs e)
    {
        ProfilesTabButton.IsChecked = true;
        if (ProfileComboBox.SelectedItem is HandheldProfile selected &&
            _lastStatus is not null &&
            !DoesProfileMatchStatus(selected, _lastStatus))
        {
            NewProfileNameTextBox.Focus();
        }
    }

    private static bool DoesProfileMatchStatus(HandheldProfile profile, DsuStatusDto status)
    {
        return profile.InvertX == status.InvertX
            && profile.InvertY == status.InvertY
            && profile.InvertZ == status.InvertZ
            && profile.SwapAxes == status.SwapAxes
            && profile.UsePitchOffset == status.UsePitchOffset
            && profile.GyroMouseEnabled == status.GyroMouseEnabled
            && Math.Abs(profile.Sensitivity - status.Sensitivity) <= 0.04f
            && Math.Abs(profile.Deadzone - status.Deadzone) <= 0.04f;
    }

    private void UpdateProfileIndicatorUi(HandheldProfile? activeProfile, DsuStatusDto? status)
    {
        activeProfile ??= _profiles.FirstOrDefault(p => string.Equals(p.Name, status?.ActiveProfileName, StringComparison.OrdinalIgnoreCase));
        string baseName = activeProfile?.Name ?? status?.ActiveProfileName ?? "Standard Landscape";
        bool isModified = activeProfile is not null && status is not null && !DoesProfileMatchStatus(activeProfile, status);

        if (isModified)
        {
            AppBarProfileNameText.Text = $"{baseName} *";
            AppBarProfileButton.BorderBrush = AccentPrimaryBrush;
            AppBarProfileButton.Background = AccentDimBrush;
            AppBarProfileNameText.Foreground = TextOnAccentBrush;
            AppBarProfileIcon.Stroke = AccentPrimaryBrush;
            AppBarProfileButton.ToolTip = _loc["AppBarProfileModifiedTooltip"];

            ProfileStatusBadgeText.Text = _loc["ProfileBadgeModified"];
            ProfileStatusBadgeText.Foreground = AccentPrimaryBrush;
        }
        else
        {
            AppBarProfileNameText.Text = baseName;
            AppBarProfileButton.BorderBrush = BorderInputBrush;
            AppBarProfileButton.Background = BgSubPanelBrush;
            AppBarProfileNameText.Foreground = TextSecondaryBrush;
            AppBarProfileIcon.Stroke = StatusOnlineBrush;
            AppBarProfileButton.ToolTip = _loc["AppBarProfileTooltip"];

            bool isCustom = activeProfile is not null && !IsSystemPreset(activeProfile);
            ProfileStatusBadgeText.Text = isCustom ? _loc["ProfileBadgeCustom"] : _loc["ProfileBadgeSystem"];
            ProfileStatusBadgeText.Foreground = isCustom ? StatusOnlineBrush : TextSecondaryBrush;
        }
    }

    private static bool IsCoreOrientationPreset(HandheldProfile profile)
    {
        return string.Equals(profile.Name, "Standard Landscape", StringComparison.OrdinalIgnoreCase)
            || string.Equals(profile.Name, "Portrait Sensor (Legion Go)", StringComparison.OrdinalIgnoreCase)
            || string.Equals(profile.Name, "Flight & Racing (Inverted X)", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsSystemPreset(HandheldProfile profile)
    {
        return string.Equals(profile.Author, "System", StringComparison.OrdinalIgnoreCase)
            || string.Equals(profile.Name, "Standard Landscape", StringComparison.OrdinalIgnoreCase);
    }

    private static string FormatProfileTagSummary(HandheldProfile profile)
    {
        var tags = new List<string>();
        if (profile.UsePitchOffset) tags.Add("0° PITCH");
        if (profile.InvertX) tags.Add("INV-X");
        if (profile.InvertY) tags.Add("INV-Y");
        if (profile.InvertZ) tags.Add("INV-Z");
        if (profile.SwapAxes) tags.Add("SWAP X⇄Y");
        if (Math.Abs(profile.Sensitivity - 1.0f) > 0.04f) tags.Add(FormattableString.Invariant($"{profile.Sensitivity:0.0}x"));
        if (profile.Deadzone > 0.04f) tags.Add(FormattableString.Invariant($"DZ {profile.Deadzone:0.0}°/s"));
        if (profile.GyroMouseEnabled)
        {
            string mouseMode = profile.GyroMouseAlwaysActive
                ? "MOUSE: ALWAYS"
                : $"MOUSE: [{(string.IsNullOrWhiteSpace(profile.GyroMouseTriggerDisplay) ? "LT" : profile.GyroMouseTriggerDisplay)}]";
            tags.Add(mouseMode);
        }

        return tags.Count > 0
            ? string.Join("  •  ", tags)
            : "RAW (1.0x / 0.0° DZ)";
    }

    private void UpdateSelectedProfileDetails(HandheldProfile profile)
    {
        ProfileDescriptionText.Text = FormatProfileTagSummary(profile);
        DeleteProfileButton.IsEnabled = !IsSystemPreset(profile);
        UpdateProfileIndicatorUi(profile, _lastStatus);
    }

    private void ShowProfileStatus(string message, bool isError = false, string? subtitle = null)
    {
        string detail = subtitle ?? (ProfileComboBox.SelectedItem is HandheldProfile p
            ? FormatProfileTagSummary(p)
            : $"UDP 0.0.0.0:{_lastStatus?.ServerPort ?? 26760}");

        ShowToastNotification(message, detail, isError);
    }

    private async Task RefreshProfilesAsync(string? selectProfileName = null)
    {
        _suppressUiEvents = true;
        try
        {
            IReadOnlyList<HandheldProfile> loaded = await _ipcClient.GetProfilesAsync();
            _profiles = loaded.ToList();
            ProfileComboBox.ItemsSource = null;
            ProfileComboBox.ItemsSource = _profiles;

            string target = selectProfileName ?? _lastStatus?.ActiveProfileName ?? _profiles.FirstOrDefault()?.Name ?? "Standard Landscape";
            var match = _profiles.FirstOrDefault(p => string.Equals(p.Name, target, StringComparison.OrdinalIgnoreCase))
                        ?? _profiles.FirstOrDefault();

            if (match is not null)
            {
                ProfileComboBox.SelectedValue = match.Name;
                UpdateSelectedProfileDetails(match);
            }

            RenderControllersAndRulesUi();
        }
        catch
        {
            // Ignore directory read error
        }
        finally
        {
            _suppressUiEvents = false;
        }
    }

    private async void ProfileComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (ProfileComboBox.SelectedItem is not HandheldProfile selected)
            return;

        UpdateSelectedProfileDetails(selected);

        if (_suppressUiEvents || !IsLoaded)
            return;

        DsuStatusDto status = await _ipcClient.ApplyProfileAsync(selected.Name);
        _automationWatcher.SetBaseProfileName(selected.Name);
        UpdateUiFromStatus(status);
        ShowProfileStatus(_loc.Format("ProfileAppliedFormat", selected.Name));
    }

    private async void DeleteProfileButton_Click(object sender, RoutedEventArgs e)
    {
        if (ProfileComboBox.SelectedItem is not HandheldProfile profile || IsSystemPreset(profile))
            return;

        DeleteProfileButton.IsEnabled = false;
        try
        {
            DsuStatusDto status = await _ipcClient.DeleteProfileAsync(profile.Name);
            UpdateUiFromStatus(status);
            await RefreshProfilesAsync("Standard Landscape");
            ShowProfileStatus(_loc.Format("ProfileDeleteSuccess", profile.Name));
        }
        finally
        {
            if (ProfileComboBox.SelectedItem is HandheldProfile current)
            {
                DeleteProfileButton.IsEnabled = !IsSystemPreset(current);
            }
        }
    }

    private async void SaveProfileButton_Click(object sender, RoutedEventArgs e)
    {
        await SaveCurrentProfileAsync();
    }

    private async void NewProfileNameTextBox_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
        {
            e.Handled = true;
            await SaveCurrentProfileAsync();
            Keyboard.ClearFocus();
        }
    }

    private async Task SaveCurrentProfileAsync()
    {
        string profileName = NewProfileNameTextBox.Text.Trim();
        if (string.IsNullOrWhiteSpace(profileName))
            return;

        var profile = new HandheldProfile
        {
            Name = profileName,
            Author = Environment.UserName,
            Description = $"Custom user profile ({DateTime.Now:yyyy-MM-dd})",
            InvertX = InvertXToggle.IsChecked == true,
            InvertY = InvertYToggle.IsChecked == true,
            InvertZ = InvertZToggle.IsChecked == true,
            SwapAxes = SwapAxesToggle.IsChecked == true,
            UsePitchOffset = PitchOffsetToggle.IsChecked == true,
            Sensitivity = (float)Math.Round(SensitivitySlider.Value, 1),
            Deadzone = (float)Math.Round(DeadzoneSlider.Value, 1),
            GyroMouseEnabled = _gyroMouseService.IsEnabled,
            GyroMouseAlwaysActive = _gyroMouseService.AlwaysActive,
            GyroMouseTriggerMask = _gyroMouseService.TriggerMask,
            GyroMouseTriggerDisplay = _gyroMouseService.TriggerDisplay,
            GyroMouseSpeed = (float)Math.Round(_gyroMouseService.SpeedMultiplier, 1)
        };

        SaveProfileButton.IsEnabled = false;
        try
        {
            DsuStatusDto status = await _ipcClient.SaveProfileAsync(profile);
            NewProfileNameTextBox.Text = string.Empty;
            UpdateUiFromStatus(status);
            await RefreshProfilesAsync(profile.Name);
            ShowProfileStatus(_loc.Format("ProfileSaveSuccess", profile.Name));
        }
        finally
        {
            SaveProfileButton.IsEnabled = true;
        }
    }

    private void ExportProfileButton_Click(object sender, RoutedEventArgs e)
    {
        HandheldProfile? profile = ProfileComboBox.SelectedItem as HandheldProfile;
        if (profile is null)
        {
            profile = new HandheldProfile
            {
                Name = "Custom Profile",
                InvertX = InvertXToggle.IsChecked == true,
                InvertY = InvertYToggle.IsChecked == true,
                InvertZ = InvertZToggle.IsChecked == true,
                SwapAxes = SwapAxesToggle.IsChecked == true,
                UsePitchOffset = PitchOffsetToggle.IsChecked == true,
                Sensitivity = (float)Math.Round(SensitivitySlider.Value, 1),
                Deadzone = (float)Math.Round(DeadzoneSlider.Value, 1),
            GyroMouseEnabled = _gyroMouseService.IsEnabled,
            GyroMouseAlwaysActive = _gyroMouseService.AlwaysActive,
            GyroMouseTriggerMask = _gyroMouseService.TriggerMask,
            GyroMouseTriggerDisplay = _gyroMouseService.TriggerDisplay,
            GyroMouseSpeed = (float)Math.Round(_gyroMouseService.SpeedMultiplier, 1)
            };
        }

        var sfd = new SaveFileDialog
        {
            Title = _loc["BtnExportFile"],
            Filter = "JSON Profile (*.json)|*.json|All files (*.*)|*.*",
            FileName = $"{profile.Name}.json"
        };

        if (sfd.ShowDialog(this) == true)
        {
            try
            {
                string json = ProfileManager.SerializeProfile(profile);
                File.WriteAllText(sfd.FileName, json);
                ShowProfileStatus(_loc.Format("ProfileExportSuccess", Path.GetFileName(sfd.FileName)));
            }
            catch (Exception ex)
            {
                ShowProfileStatus($"Export failed: {ex.Message}", isError: true);
            }
        }
    }

    private async void ImportProfileButton_Click(object sender, RoutedEventArgs e)
    {
        var ofd = new OpenFileDialog
        {
            Title = _loc["BtnImportFile"],
            Filter = "JSON Profile (*.json)|*.json|All files (*.*)|*.*",
            Multiselect = false
        };

        if (ofd.ShowDialog(this) == true)
        {
            try
            {
                string json = File.ReadAllText(ofd.FileName);
                HandheldProfile? imported = ProfileManager.DeserializeProfile(json);
                if (imported is null)
                {
                    ShowProfileStatus(_loc["ProfileInvalidError"], isError: true);
                    return;
                }

                DsuStatusDto status = await _ipcClient.SaveProfileAsync(imported);
                await _ipcClient.ApplyProfileAsync(imported.Name);
                UpdateUiFromStatus(status);
                await RefreshProfilesAsync(imported.Name);
                ShowProfileStatus(_loc.Format("ProfileImportSuccess", imported.Name));
            }
            catch (Exception ex)
            {
                ShowProfileStatus($"Import failed: {ex.Message}", isError: true);
            }
        }
    }
}
