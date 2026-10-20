using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using HandheldDsuWidget.Services;

namespace HandheldDsuWidget;

public partial class MainWindow
{
    public void HideToTray()
    {
        _gameBarAdapter.ResetOverlaySession();
        if (!_isPinnedByUser)
        {
            Topmost = false;
        }
        ShowInTaskbar = false;
        Hide();
    }

    public void ShowFromTray()
    {
        ShowInTaskbar = true;
        Show();
        if (WindowState == WindowState.Minimized)
        {
            WindowState = WindowState.Normal;
        }
        if (_lastStatus is not null)
        {
            UpdateUiFromStatus(_lastStatus);
        }
        Activate();
        Focus();
    }

    private void ShowForGameBarOverlay()
    {
        Topmost = true;
        ShowInTaskbar = true;
        Show();
        if (WindowState == WindowState.Minimized)
        {
            WindowState = WindowState.Normal;
        }
        if (_lastStatus is not null)
        {
            UpdateUiFromStatus(_lastStatus);
        }
        Activate();
    }

    private void HideFromGameBarOverlay()
    {
        Topmost = _isPinnedByUser;
        if (!_isPinnedByUser)
        {
            HideToTray();
        }
    }

    private void ToggleWindowVisibility()
    {
        if (IsVisible)
        {
            HideToTray();
        }
        else
        {
            ShowFromTray();
        }
    }

    private MenuItem CreateTrayMenuItem(
        Style itemStyle,
        string iconKey,
        string labelText,
        Brush? iconBrush = null,
        Brush? textBrush = null,
        string? rightBadgeText = null,
        Brush? rightBadgeBrush = null,
        bool isStrokeIcon = true)
    {
        var rowGrid = new Grid();
        rowGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(20) });
        rowGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        rowGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        Brush resolvedIconBrush = iconBrush ?? TextSecondaryBrush;
        var iconPath = new System.Windows.Shapes.Path
        {
            Data = (Geometry)FindResource(iconKey),
            Width = 11,
            Height = 11,
            Stretch = Stretch.Uniform,
            HorizontalAlignment = HorizontalAlignment.Left,
            VerticalAlignment = VerticalAlignment.Center
        };

        if (isStrokeIcon)
        {
            iconPath.Stroke = resolvedIconBrush;
            iconPath.StrokeThickness = 1.4;
            iconPath.StrokeStartLineCap = PenLineCap.Round;
            iconPath.StrokeEndLineCap = PenLineCap.Round;
            iconPath.StrokeLineJoin = PenLineJoin.Round;
        }
        else
        {
            iconPath.Fill = resolvedIconBrush;
        }

        Grid.SetColumn(iconPath, 0);
        rowGrid.Children.Add(iconPath);

        var titleBlock = new TextBlock
        {
            Text = labelText,
            FontSize = 10.5,
            FontWeight = FontWeights.SemiBold,
            Foreground = textBrush ?? TextPrimaryBrush,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, 0, 8, 0)
        };
        Grid.SetColumn(titleBlock, 1);
        rowGrid.Children.Add(titleBlock);

        if (!string.IsNullOrEmpty(rightBadgeText))
        {
            var badgeBorder = new Border
            {
                CornerRadius = new CornerRadius(2.5),
                Padding = new Thickness(4, 1, 4, 1),
                Background = BgSubPanelBrush,
                BorderBrush = BorderInputBrush,
                BorderThickness = new Thickness(1),
                VerticalAlignment = VerticalAlignment.Center,
                Child = new TextBlock
                {
                    Text = rightBadgeText,
                    FontFamily = (FontFamily)FindResource("FontMonospace"),
                    FontSize = 8.5,
                    FontWeight = FontWeights.Bold,
                    Foreground = rightBadgeBrush ?? TextSecondaryBrush
                }
            };
            Grid.SetColumn(badgeBorder, 2);
            rowGrid.Children.Add(badgeBorder);
        }

        return new MenuItem
        {
            Style = itemStyle,
            Header = rowGrid
        };
    }

    private void ShowTrayContextMenu()
    {
        if (_trayContextMenu is not null)
        {
            _trayContextMenu.IsOpen = false;
        }

        var itemStyle = (Style)FindResource("AppTrayMenuItemStyle");
        var sepStyle = (Style)FindResource("AppTraySeparatorStyle");

        var menu = new ContextMenu
        {
            Style = (Style)FindResource("AppTrayContextMenuStyle"),
            Placement = PlacementMode.MousePoint
        };

        var showHideItem = CreateTrayMenuItem(
            itemStyle,
            "IconDashboard",
            IsVisible ? _loc["TrayMenuHideWindow"] : _loc["TrayMenuShowWindow"]);
        showHideItem.Click += (_, _) => ToggleWindowVisibility();
        menu.Items.Add(showHideItem);

        bool isStreaming = _lastStatus?.IsStreamingEnabled ?? true;
        var streamItem = CreateTrayMenuItem(
            itemStyle,
            "IconSignalPulse",
            _loc["TrayMenuStream"],
            iconBrush: isStreaming ? StatusOnlineBrush : TextMutedBrush,
            rightBadgeText: isStreaming ? _loc["TrayBadgeOn"] : _loc["TrayBadgeOff"],
            rightBadgeBrush: isStreaming ? StatusOnlineBrush : TextMutedBrush);
        streamItem.Click += async (_, _) =>
        {
            DsuStatusDto status = await _ipcClient.ToggleStreamingAsync(!isStreaming);
            UpdateUiFromStatus(status);
        };
        menu.Items.Add(streamItem);

        var calibrateItem = CreateTrayMenuItem(
            itemStyle,
            "IconCrosshair",
            _loc["TrayMenuCalibrate"]);
        calibrateItem.Click += async (_, _) => await TriggerCalibrationFromControllerAsync();
        menu.Items.Add(calibrateItem);

        var settingsItem = CreateTrayMenuItem(
            itemStyle,
            "IconSettings",
            _loc["TrayMenuSettings"]);
        settingsItem.Click += (_, _) =>
        {
            ShowFromTray();
            SettingsTabButton.IsChecked = true;
        };
        menu.Items.Add(settingsItem);

        menu.Items.Add(new Separator { Style = sepStyle });

        var exitItem = CreateTrayMenuItem(
            itemStyle,
            "IconClose",
            _loc["TrayMenuExit"],
            iconBrush: StatusOfflineBrush,
            textBrush: StatusOfflineBrush,
            isStrokeIcon: false);
        exitItem.Click += (_, _) => Close();
        menu.Items.Add(exitItem);

        menu.Closed += (_, _) => _trayService.CompleteContextMenu();

        _trayContextMenu = menu;
        _trayService.PrepareForContextMenu();
        menu.IsOpen = true;
    }
}
