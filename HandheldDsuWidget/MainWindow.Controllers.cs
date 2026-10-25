using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using Microsoft.Win32;
using HandheldDsuServer.Models;
using HandheldDsuWidget.Services;

namespace HandheldDsuWidget;

public partial class MainWindow
{
    private async void OnAutomationNotification(object? sender, (string Title, string Subtitle, string? ProfileToApply, int ConnectedCount) args)
    {
        if (args.ConnectedCount > 4)
        {
            _ipcClient.SetSecondaryClusterActive(true);
        }

        if (!string.IsNullOrWhiteSpace(args.ProfileToApply))
        {
            DsuStatusDto status = await _ipcClient.ApplyProfileAsync(args.ProfileToApply);
            UpdateUiFromStatus(status);
        }

        RenderControllersAndRulesUi();
        ShowToastNotification(args.Title, args.Subtitle, isError: false, showHudOverlay: true);
    }

    private async void OnRecenterHotkeyTriggered(object? sender, (string ControllerName, string HotkeyDisplay) args)
    {
        DsuStatusDto status = await _ipcClient.RecenterImmediateAsync();
        UpdateUiFromStatus(status);

        ShowToastNotification(
            _loc["ToastRecenterSuccessTitle"],
            _loc.Format("ToastRecenterSuccessSubFormat", args.ControllerName, args.HotkeyDisplay),
            isError: false,
            showHudOverlay: true);
    }

    private void AutoControllerSwitchToggle_Click(object sender, RoutedEventArgs e)
    {
        if (_suppressUiEvents)
            return;

        _automationWatcher.Config.AutoSwitchByController = AutoControllerSwitchToggle.IsChecked == true;
        _automationWatcher.SaveConfig();
    }

    private void AutoEmulatorSwitchToggle_Click(object sender, RoutedEventArgs e)
    {
        if (_suppressUiEvents)
            return;

        _automationWatcher.Config.AutoSwitchByEmulator = AutoEmulatorSwitchToggle.IsChecked == true;
        _automationWatcher.SaveConfig();
    }

    private void AddCustomExeRuleButton_Click(object sender, RoutedEventArgs e)
    {
        BrowseAndAddExeRule();
    }

    private void RefreshRunningAppsButton_Click(object sender, RoutedEventArgs e)
    {
        RefreshRunningAppsList(forceRefresh: true);
    }

    private void AddRunningAppRuleButton_Click(object sender, RoutedEventArgs e)
    {
        if (RunningAppsComboBox?.SelectedItem is not ControllerAndAppWatcherService.RunningAppEntry entry)
            return;

        string activeProfile = _lastStatus?.ActiveProfileName ?? "Standard Landscape";
        _automationWatcher.AddOrUpdateEmulatorRule(
            processName: entry.ProcessName,
            displayName: entry.DisplayName,
            profileName: activeProfile,
            isEnabled: true);

        RenderControllersAndRulesUi();
        ShowToastNotification(_loc["ToastEmulatorRuleAddedTitle"], $"{entry.DisplayName} ({entry.ProcessName}.exe) → {activeProfile}");
    }

    private void RefreshRunningAppsList(bool forceRefresh = false)
    {
        if (RunningAppsComboBox is null)
            return;

        if (!forceRefresh && RunningAppsComboBox.IsDropDownOpen)
            return;

        string? prevProc = (RunningAppsComboBox.SelectedItem as ControllerAndAppWatcherService.RunningAppEntry)?.ProcessName;
        IReadOnlyList<ControllerAndAppWatcherService.RunningAppEntry> apps = _automationWatcher.GetRunningApplications();
        RunningAppsComboBox.ItemsSource = apps;

        if (apps.Count > 0)
        {
            var match = prevProc is not null
                ? apps.FirstOrDefault(a => string.Equals(a.ProcessName, prevProc, StringComparison.OrdinalIgnoreCase))
                : null;
            RunningAppsComboBox.SelectedItem = match ?? apps[0];
            AddRunningAppRuleButton.IsEnabled = true;
        }
        else
        {
            RunningAppsComboBox.SelectedIndex = -1;
            AddRunningAppRuleButton.IsEnabled = false;
        }
    }

    private void BrowseAndAddExeRule()
    {
        string emuDeckDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "emudeck",
            "Emulators");

        var ofd = new OpenFileDialog
        {
            Title = _loc["BtnAddRule"],
            Filter = "Application / Emulator (*.exe)|*.exe|All files (*.*)|*.*",
            Multiselect = false,
            CheckFileExists = true
        };

        if (Directory.Exists(emuDeckDir))
        {
            ofd.InitialDirectory = emuDeckDir;
        }

        if (ofd.ShowDialog(this) != true)
            return;

        string filePath = ofd.FileName;
        string cleanProc = Path.GetFileNameWithoutExtension(filePath).Trim();
        if (string.IsNullOrWhiteSpace(cleanProc))
            return;

        string activeProfile = _lastStatus?.ActiveProfileName ?? "Standard Landscape";
        string friendlyDisplayName = AutomationConfig.ResolveFriendlyProcessDisplayName(cleanProc);

        if (string.Equals(friendlyDisplayName, $"{cleanProc}.exe", StringComparison.OrdinalIgnoreCase))
        {
            try
            {
                FileVersionInfo info = FileVersionInfo.GetVersionInfo(filePath);
                if (!string.IsNullOrWhiteSpace(info.FileDescription))
                {
                    friendlyDisplayName = info.FileDescription.Trim();
                }
                else if (!string.IsNullOrWhiteSpace(info.ProductName))
                {
                    friendlyDisplayName = info.ProductName.Trim();
                }
            }
            catch
            {
                // Keep default process filename if version info is unavailable
            }
        }

        _automationWatcher.AddOrUpdateEmulatorRule(
            processName: cleanProc,
            displayName: friendlyDisplayName,
            profileName: activeProfile,
            isEnabled: true);

        RenderControllersAndRulesUi();
        ShowToastNotification(_loc["ToastEmulatorRuleAddedTitle"], $"{friendlyDisplayName} ({cleanProc}.exe) → {activeProfile}");
    }

    private bool _isRenderingControllersUi;
    private string? _renamingHardwareId;

    private void RenderControllersAndRulesUi()
    {
        if (_isRenderingControllersUi || ControllersListPanel is null || EmulatorRulesListPanel is null)
            return;

        if (!string.IsNullOrWhiteSpace(_renamingHardwareId) && Keyboard.FocusedElement is TextBox)
            return;

        _isRenderingControllersUi = true;
        try
        {
            IReadOnlyList<KnownControllerConfig> controllers = _automationWatcher.GetControllersSnapshot();
            if (controllers.Count == 0)
            {
                int basePort = _lastStatus?.ServerPort ?? 26760;
                int secondaryPort = _lastStatus?.SecondaryPort ?? 26762;
                controllers = _automationWatcher.ScanControllers(basePort, secondaryPort);
            }

            int connectedCount = controllers.Count(c => c.IsConnected);
            if (connectedCount > 4 && _lastStatus?.IsSecondaryClusterActive == false)
            {
                _ipcClient.SetSecondaryClusterActive(true);
            }

            ControllersListPanel.Children.Clear();
            RefreshRunningAppsList();

        foreach (KnownControllerConfig ctrl in controllers)
        {
            var cardBorder = new Border
            {
                CornerRadius = new CornerRadius(8),
                Padding = new Thickness(11, 9, 11, 9),
                Margin = new Thickness(0, 0, 0, 7),
                Background = BgSubPanelBrush,
                BorderBrush = ctrl.IsInputActive
                    ? AccentPrimaryBrush
                    : (ctrl.IsConnected ? StatusOnlineBrush : BorderInputBrush),
                BorderThickness = new Thickness(ctrl.IsInputActive ? 1.6 : 1.0)
            };

            var rootStack = new StackPanel();

            // Row 1: Controller Name + Online/Offline Badge + Rename ('✎') + Forget Button (if offline)
            var topGrid = new Grid();
            topGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            topGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            var titleStack = new StackPanel();
            var nameText = new TextBlock
            {
                Text = ctrl.EffectiveName,
                FontSize = 11.5,
                FontWeight = FontWeights.Bold,
                Foreground = TextPrimaryBrush,
                TextTrimming = TextTrimming.CharacterEllipsis
            };
            titleStack.Children.Add(nameText);

            string statusText = ctrl.IsConnected
                ? _loc.Format("ControllerBadgeConnectedFormat", ctrl.AssignedPlayerIndex + 1, ctrl.AssignedUdpPort)
                : _loc["ControllerBadgeOffline"];
            if (ctrl.IsInputActive)
            {
                statusText = $"{statusText}   •   {_loc["ControllerInputActiveBadge"]}";
            }

            var statusBadgeText = new TextBlock
            {
                Text = statusText,
                FontSize = 9.5,
                FontWeight = FontWeights.Bold,
                Foreground = ctrl.IsInputActive
                    ? AccentPrimaryBrush
                    : (ctrl.IsConnected ? StatusOnlineBrush : TextMutedBrush),
                Margin = new Thickness(0, 2, 0, 0)
            };
            titleStack.Children.Add(statusBadgeText);

            string subtitleHw = !string.IsNullOrWhiteSpace(ctrl.CustomName)
                ? $"{ctrl.DisplayName}  •  {ctrl.HardwareId}"
                : ctrl.HardwareId;

            var hwIdText = new TextBlock
            {
                Text = subtitleHw,
                FontSize = 8.5,
                FontFamily = new FontFamily("Consolas"),
                Foreground = TextMutedBrush,
                Margin = new Thickness(0, 1, 0, 0),
                TextTrimming = TextTrimming.CharacterEllipsis
            };
            titleStack.Children.Add(hwIdText);

            Grid.SetColumn(titleStack, 0);
            topGrid.Children.Add(titleStack);

            var actionButtonsPanel = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                VerticalAlignment = VerticalAlignment.Top
            };

            string hwKey = ctrl.HardwareId;
            var renameBtn = new Button
            {
                Content = "✎",
                Style = (Style)FindResource("AppGhostButtonStyle"),
                Width = 24,
                Height = 24,
                Padding = new Thickness(0),
                FontSize = 10.5,
                Margin = new Thickness(4, 0, 0, 0),
                ToolTip = _loc["ControllerRenameTooltip"]
            };
            renameBtn.Click += (_, _) =>
            {
                _renamingHardwareId = string.Equals(_renamingHardwareId, hwKey, StringComparison.OrdinalIgnoreCase)
                    ? null
                    : hwKey;
                Keyboard.ClearFocus();
                RenderControllersAndRulesUi();
            };
            actionButtonsPanel.Children.Add(renameBtn);

            if (!ctrl.IsConnected)
            {
                var forgetBtn = new Button
                {
                    Content = "✕",
                    Style = (Style)FindResource("AppGhostButtonStyle"),
                    Width = 24,
                    Height = 24,
                    Padding = new Thickness(0),
                    FontSize = 10,
                    Margin = new Thickness(4, 0, 0, 0),
                    ToolTip = _loc["ControllerForgetTooltip"]
                };
                forgetBtn.Click += (_, _) => _automationWatcher.ForgetController(hwKey);
                actionButtonsPanel.Children.Add(forgetBtn);
            }

            Grid.SetColumn(actionButtonsPanel, 1);
            topGrid.Children.Add(actionButtonsPanel);

            rootStack.Children.Add(topGrid);

            // Optional Inline Rename Editor Row
            if (string.Equals(_renamingHardwareId, hwKey, StringComparison.OrdinalIgnoreCase))
            {
                var renameGrid = new Grid { Margin = new Thickness(0, 7, 0, 2) };
                renameGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                renameGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
                renameGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

                var renameBox = new TextBox
                {
                    Style = (Style)FindResource("AppTextBoxStyle"),
                    Text = !string.IsNullOrWhiteSpace(ctrl.CustomName) ? ctrl.CustomName : ctrl.DisplayName,
                    Height = 26,
                    FontSize = 10.5,
                    VerticalContentAlignment = VerticalAlignment.Center,
                    Padding = new Thickness(7, 0, 7, 0),
                    ToolTip = _loc["ControllerRenamePlaceholder"]
                };

                void CommitRename()
                {
                    string entered = renameBox.Text.Trim();
                    if (string.Equals(entered, ctrl.DisplayName, StringComparison.OrdinalIgnoreCase))
                    {
                        entered = string.Empty;
                    }
                    _renamingHardwareId = null;
                    Keyboard.ClearFocus();
                    _automationWatcher.RenameController(hwKey, entered);
                    ShowToastNotification(
                        _loc["ToastControllerRenamedTitle"],
                        $"🎮 {(string.IsNullOrWhiteSpace(entered) ? ctrl.DisplayName : entered)} ({hwKey})");
                }

                renameBox.KeyDown += (_, e) =>
                {
                    if (e.Key == Key.Enter)
                    {
                        e.Handled = true;
                        CommitRename();
                    }
                    else if (e.Key == Key.Escape)
                    {
                        e.Handled = true;
                        _renamingHardwareId = null;
                        Keyboard.ClearFocus();
                        RenderControllersAndRulesUi();
                    }
                };

                Grid.SetColumn(renameBox, 0);
                renameGrid.Children.Add(renameBox);

                var saveNameBtn = new Button
                {
                    Content = _loc["ControllerRenameSaveBtn"],
                    Style = (Style)FindResource("AppPrimaryButtonStyle"),
                    Height = 26,
                    Padding = new Thickness(10, 0, 10, 0),
                    Margin = new Thickness(5, 0, 0, 0),
                    FontSize = 9.5
                };
                saveNameBtn.Click += (_, _) => CommitRename();
                Grid.SetColumn(saveNameBtn, 1);
                renameGrid.Children.Add(saveNameBtn);

                var cancelNameBtn = new Button
                {
                    Content = "✕",
                    Style = (Style)FindResource("AppGhostButtonStyle"),
                    Width = 26,
                    Height = 26,
                    Padding = new Thickness(0),
                    Margin = new Thickness(4, 0, 0, 0),
                    FontSize = 10
                };
                cancelNameBtn.Click += (_, _) =>
                {
                    _renamingHardwareId = null;
                    Keyboard.ClearFocus();
                    RenderControllersAndRulesUi();
                };
                Grid.SetColumn(cancelNameBtn, 2);
                renameGrid.Children.Add(cancelNameBtn);

                rootStack.Children.Add(renameGrid);
            }

            // Row 2: Assigned Profile Selector
            var bottomGrid = new Grid { Margin = new Thickness(0, 7, 0, 0) };
            bottomGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            bottomGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

            var profileLabel = new TextBlock
            {
                Text = _loc["ControllerAutoProfileLabel"],
                FontSize = 10,
                FontWeight = FontWeights.SemiBold,
                Foreground = TextSecondaryBrush,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(0, 0, 8, 0)
            };
            Grid.SetColumn(profileLabel, 0);
            bottomGrid.Children.Add(profileLabel);

            var profileCombo = new ComboBox
            {
                Style = (Style)FindResource("AppComboBoxStyle"),
                ItemsSource = _profiles,
                DisplayMemberPath = "Name",
                SelectedValuePath = "Name",
                SelectedValue = ctrl.AssignedProfileName
            };

            profileCombo.SelectionChanged += async (_, _) =>
            {
                if (profileCombo.SelectedItem is HandheldProfile selectedProf)
                {
                    ctrl.AssignedProfileName = selectedProf.Name;
                    _automationWatcher.SaveConfig();

                    if (ctrl.IsConnected)
                    {
                        _automationWatcher.SetBaseProfileName(selectedProf.Name);
                        DsuStatusDto status = await _ipcClient.ApplyProfileAsync(selectedProf.Name);
                        UpdateUiFromStatus(status);
                        ShowToastNotification(
                            $"🎮 {ctrl.EffectiveName}",
                            _loc.Format("ToastControllerProfileAppliedFormat", selectedProf.Name));
                    }
                    else
                    {
                        ShowToastNotification(
                            $"🎮 {ctrl.EffectiveName}",
                            _loc.Format("ToastControllerProfileSavedFormat", selectedProf.Name));
                    }
                }
            };

            Grid.SetColumn(profileCombo, 1);
            bottomGrid.Children.Add(profileCombo);

            rootStack.Children.Add(bottomGrid);

            // Row 3: Per-Controller Quick Recenter Hotkey Selector
            var hotkeyGrid = new Grid { Margin = new Thickness(0, 6, 0, 0) };
            hotkeyGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            hotkeyGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            hotkeyGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            var hotkeyLabel = new TextBlock
            {
                Text = _loc["ControllerHotkeyLabel"],
                FontSize = 10,
                FontWeight = FontWeights.SemiBold,
                Foreground = TextSecondaryBrush,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(0, 0, 8, 0)
            };
            Grid.SetColumn(hotkeyLabel, 0);
            hotkeyGrid.Children.Add(hotkeyLabel);

            bool isRecordingThis = string.Equals(
                _automationWatcher.RecordingHardwareId,
                ctrl.HardwareId,
                StringComparison.OrdinalIgnoreCase);
            bool hasHotkey = !string.IsNullOrWhiteSpace(ctrl.RecenterHotkey);

            string hotkeyBtnText = isRecordingThis
                ? _loc["ControllerHotkeyListening"]
                : (hasHotkey ? $"🎯 {ctrl.RecenterHotkeyDisplay}" : _loc["ControllerHotkeyBindBtn"]);

            var bindHotkeyBtn = new Button
            {
                Content = hotkeyBtnText,
                Style = (Style)FindResource(isRecordingThis ? "AppPrimaryButtonStyle" : "AppSecondaryButtonStyle"),
                Height = 26,
                Padding = new Thickness(8, 0, 8, 0),
                FontSize = 10,
                HorizontalContentAlignment = HorizontalAlignment.Center
            };

            string ctrlHwId = ctrl.HardwareId;
            bindHotkeyBtn.Click += (_, _) =>
            {
                if (string.Equals(_automationWatcher.RecordingHardwareId, ctrlHwId, StringComparison.OrdinalIgnoreCase))
                {
                    _automationWatcher.CancelRecordingHotkey();
                }
                else
                {
                    _automationWatcher.StartRecordingHotkey(ctrlHwId);
                }
            };

            Grid.SetColumn(bindHotkeyBtn, 1);
            hotkeyGrid.Children.Add(bindHotkeyBtn);

            if (hasHotkey && !isRecordingThis)
            {
                var clearHotkeyBtn = new Button
                {
                    Content = "✕",
                    Style = (Style)FindResource("AppGhostButtonStyle"),
                    Width = 26,
                    Height = 26,
                    Padding = new Thickness(0),
                    Margin = new Thickness(5, 0, 0, 0),
                    FontSize = 10,
                    ToolTip = _loc["ControllerHotkeyClearTooltip"]
                };
                clearHotkeyBtn.Click += (_, _) => _automationWatcher.ClearControllerHotkey(ctrlHwId);
                Grid.SetColumn(clearHotkeyBtn, 2);
                hotkeyGrid.Children.Add(clearHotkeyBtn);
            }

            rootStack.Children.Add(hotkeyGrid);
            cardBorder.Child = rootStack;
            ControllersListPanel.Children.Add(cardBorder);
        }

        // Populate Emulator (.exe) Rules List
        EmulatorRulesListPanel.Children.Clear();
        if (_automationWatcher.Config.EmulatorRules.Count == 0)
        {
            var emptyBorder = new Border
            {
                CornerRadius = new CornerRadius(7),
                Padding = new Thickness(12, 10, 12, 10),
                Margin = new Thickness(0, 0, 0, 6),
                Background = BgSubPanelBrush,
                BorderBrush = BorderInputBrush,
                BorderThickness = new Thickness(1),
                Child = new TextBlock
                {
                    Text = _loc["EmulatorEmptyStateDesc"],
                    FontSize = 10,
                    Foreground = TextMutedBrush,
                    TextWrapping = TextWrapping.Wrap,
                    TextAlignment = TextAlignment.Center
                }
            };
            EmulatorRulesListPanel.Children.Add(emptyBorder);
            return;
        }

        foreach (EmulatorProfileRule rule in _automationWatcher.Config.EmulatorRules)
        {
            var ruleBorder = new Border
            {
                CornerRadius = new CornerRadius(7),
                Padding = new Thickness(10, 7, 10, 7),
                Margin = new Thickness(0, 0, 0, 6),
                Background = BgSubPanelBrush,
                BorderBrush = BorderInputBrush,
                BorderThickness = new Thickness(1)
            };

            var rowGrid = new Grid();
            rowGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            rowGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            rowGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(136) });
            rowGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            var ruleToggle = new CheckBox
            {
                Style = (Style)FindResource("AppToggleSwitchStyle"),
                IsChecked = rule.IsEnabled,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(0, 0, 9, 0)
            };
            ruleToggle.Click += (_, _) =>
            {
                rule.IsEnabled = ruleToggle.IsChecked == true;
                _automationWatcher.SaveConfig();
            };
            Grid.SetColumn(ruleToggle, 0);
            rowGrid.Children.Add(ruleToggle);

            var labelStack = new StackPanel
            {
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(0, 0, 8, 0)
            };
            labelStack.Children.Add(new TextBlock
            {
                Text = rule.DisplayName,
                FontSize = 10.5,
                FontWeight = FontWeights.SemiBold,
                Foreground = TextPrimaryBrush,
                TextTrimming = TextTrimming.CharacterEllipsis
            });
            labelStack.Children.Add(new TextBlock
            {
                Text = $"{rule.ProcessName}.exe",
                FontSize = 8.5,
                FontFamily = new FontFamily("Consolas"),
                Foreground = TextMutedBrush
            });
            Grid.SetColumn(labelStack, 1);
            rowGrid.Children.Add(labelStack);

            var ruleProfileCombo = new ComboBox
            {
                Style = (Style)FindResource("AppComboBoxStyle"),
                ItemsSource = _profiles,
                DisplayMemberPath = "Name",
                SelectedValuePath = "Name",
                SelectedValue = rule.ProfileName,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(0, 0, 6, 0)
            };
            ruleProfileCombo.SelectionChanged += (_, _) =>
            {
                if (ruleProfileCombo.SelectedItem is HandheldProfile selectedProf)
                {
                    rule.ProfileName = selectedProf.Name;
                    _automationWatcher.SaveConfig();
                    ShowToastNotification(
                        $"🚀 {rule.DisplayName}",
                        _loc.Format("ToastEmulatorRuleUpdatedFormat", selectedProf.Name));
                }
            };
            Grid.SetColumn(ruleProfileCombo, 2);
            rowGrid.Children.Add(ruleProfileCombo);

            var removeBtn = new Button
            {
                Content = "✕",
                Style = (Style)FindResource("AppGhostButtonStyle"),
                Width = 26,
                Height = 26,
                Padding = new Thickness(0),
                FontSize = 10,
                VerticalAlignment = VerticalAlignment.Center,
                ToolTip = _loc["EmulatorRemoveTooltip"]
            };
            string procKey = rule.ProcessName;
            removeBtn.Click += (_, _) => _automationWatcher.RemoveEmulatorRule(procKey);
            Grid.SetColumn(removeBtn, 3);
            rowGrid.Children.Add(removeBtn);

            ruleBorder.Child = rowGrid;
            EmulatorRulesListPanel.Children.Add(ruleBorder);
        }
        }
        finally
        {
            _isRenderingControllersUi = false;
        }
    }
}
