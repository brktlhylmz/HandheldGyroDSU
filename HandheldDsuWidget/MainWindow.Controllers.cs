using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
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
        AddCustomExeRule();
    }

    private void CustomExeTextBox_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
        {
            e.Handled = true;
            AddCustomExeRule();
            Keyboard.ClearFocus();
        }
    }

    private void AddCustomExeRule()
    {
        string rawInput = CustomExeTextBox.Text.Trim();
        if (string.IsNullOrWhiteSpace(rawInput))
            return;

        string cleanProc = rawInput.Replace(".exe", "", StringComparison.OrdinalIgnoreCase);
        string activeProfile = _lastStatus?.ActiveProfileName ?? "Standard Landscape";
        string friendlyDisplayName = AutomationConfig.ResolveFriendlyProcessDisplayName(cleanProc);

        _automationWatcher.AddOrUpdateEmulatorRule(
            processName: cleanProc,
            displayName: friendlyDisplayName,
            profileName: activeProfile,
            isEnabled: true);

        CustomExeTextBox.Text = string.Empty;
        RenderControllersAndRulesUi();
        ShowToastNotification(_loc["ToastEmulatorRuleAddedTitle"], $"{cleanProc}.exe → {activeProfile}");
    }

    private bool _isRenderingControllersUi;

    private void RenderControllersAndRulesUi()
    {
        if (_isRenderingControllersUi || ControllersListPanel is null || EmulatorRulesListPanel is null)
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

        foreach (KnownControllerConfig ctrl in controllers)
        {
            var cardBorder = new Border
            {
                CornerRadius = new CornerRadius(8),
                Padding = new Thickness(11, 9, 11, 9),
                Margin = new Thickness(0, 0, 0, 7),
                Background = BgSubPanelBrush,
                BorderBrush = ctrl.IsConnected ? StatusOnlineBrush : BorderInputBrush,
                BorderThickness = new Thickness(1)
            };

            var rootStack = new StackPanel();

            // Row 1: Controller Name + Online/Offline Badge + Forget Button (if offline)
            var topGrid = new Grid();
            topGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            topGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            var titleStack = new StackPanel();
            var nameText = new TextBlock
            {
                Text = ctrl.DisplayName,
                FontSize = 11.5,
                FontWeight = FontWeights.Bold,
                Foreground = TextPrimaryBrush,
                TextTrimming = TextTrimming.CharacterEllipsis
            };
            titleStack.Children.Add(nameText);

            var statusBadgeText = new TextBlock
            {
                Text = ctrl.IsConnected
                    ? _loc.Format("ControllerBadgeConnectedFormat", ctrl.AssignedPlayerIndex + 1, ctrl.AssignedUdpPort)
                    : _loc["ControllerBadgeOffline"],
                FontSize = 9.5,
                FontWeight = FontWeights.Bold,
                Foreground = ctrl.IsConnected ? StatusOnlineBrush : TextMutedBrush,
                Margin = new Thickness(0, 2, 0, 0)
            };
            titleStack.Children.Add(statusBadgeText);

            var hwIdText = new TextBlock
            {
                Text = ctrl.HardwareId,
                FontSize = 8.5,
                FontFamily = new FontFamily("Consolas"),
                Foreground = TextMutedBrush,
                Margin = new Thickness(0, 1, 0, 0)
            };
            titleStack.Children.Add(hwIdText);

            Grid.SetColumn(titleStack, 0);
            topGrid.Children.Add(titleStack);

            if (!ctrl.IsConnected && !string.Equals(ctrl.HardwareId, "BUILTIN_HANDHELD_IMU", StringComparison.OrdinalIgnoreCase))
            {
                var forgetBtn = new Button
                {
                    Content = "✕",
                    Style = (Style)FindResource("AppGhostButtonStyle"),
                    Width = 24,
                    Height = 24,
                    Padding = new Thickness(0),
                    FontSize = 10,
                    VerticalAlignment = VerticalAlignment.Top,
                    ToolTip = _loc["ControllerForgetTooltip"]
                };
                string hwKey = ctrl.HardwareId;
                forgetBtn.Click += (_, _) => _automationWatcher.ForgetController(hwKey);
                Grid.SetColumn(forgetBtn, 1);
                topGrid.Children.Add(forgetBtn);
            }

            rootStack.Children.Add(topGrid);

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
                        DsuStatusDto status = await _ipcClient.ApplyProfileAsync(selectedProf.Name);
                        UpdateUiFromStatus(status);
                        ShowToastNotification(
                            $"🎮 {ctrl.DisplayName}",
                            _loc.Format("ToastControllerProfileAppliedFormat", selectedProf.Name));
                    }
                    else
                    {
                        ShowToastNotification(
                            $"🎮 {ctrl.DisplayName}",
                            _loc.Format("ToastControllerProfileSavedFormat", selectedProf.Name));
                    }
                }
            };

            Grid.SetColumn(profileCombo, 1);
            bottomGrid.Children.Add(profileCombo);

            rootStack.Children.Add(bottomGrid);
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
