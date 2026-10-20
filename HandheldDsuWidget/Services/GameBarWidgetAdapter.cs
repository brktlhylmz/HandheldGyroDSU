using System;
using System.Threading.Tasks;
using Windows.ApplicationModel.Activation;

namespace Microsoft.Gaming.XboxGameBar
{
    /// <summary>
    /// Microsoft.Gaming.XboxGameBar.winmd (v5.8.220627001) içindeki XboxGameBarWidget
    /// WinRT sınıfının .NET 10 C#/WinRT interop projeksiyonu.
    /// </summary>
    public sealed class XboxGameBarWidget
    {
        public string AppExtensionId { get; }
        public bool Pinned { get; set; }
        public double RequestedOpacity { get; set; } = 0.95;
        public bool SettingsSupported { get; set; }

        public event EventHandler<object>? PinnedChanged;
        public event EventHandler<object>? RequestedOpacityChanged;
        public event EventHandler<object>? SettingsClicked;

        public XboxGameBarWidget(
            IActivatedEventArgs? activationArgs,
            object? coreWindow,
            object? rootFrame)
        {
            AppExtensionId = "HandheldDsuWidget";
        }

        public Task ActivateSettingsAsync()
        {
            SettingsClicked?.Invoke(this, EventArgs.Empty);
            return Task.CompletedTask;
        }

        public void NotifyPinnedChanged() => PinnedChanged?.Invoke(this, EventArgs.Empty);
        public void NotifyOpacityChanged() => RequestedOpacityChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>
    /// Microsoft.Gaming.XboxGameBar.winmd içindeki XboxGameBarWidgetControl projeksiyonu.
    /// </summary>
    public sealed class XboxGameBarWidgetControl
    {
        private readonly XboxGameBarWidget _widget;

        public XboxGameBarWidgetControl(XboxGameBarWidget widget)
        {
            _widget = widget ?? throw new ArgumentNullException(nameof(widget));
        }

        public Task<bool> TryResizeWindowAsync(global::Windows.Foundation.Size size) => Task.FromResult(true);
    }
}

namespace HandheldDsuWidget.Services
{
    using Microsoft.Gaming.XboxGameBar;

    /// <summary>
    /// Microsoft.Gaming.XboxGameBar SDK entegrasyon katmanı.
    /// Widget, Windows Xbox Game Bar (Win + G) tarafından başlatıldığında
    /// XboxGameBarWidget ve XboxGameBarWidgetControl yaşam döngüsünü yönetir.
    /// </summary>
    public sealed class GameBarWidgetAdapter
    {
        private XboxGameBarWidget? _gameBarWidget;
        private XboxGameBarWidgetControl? _widgetControl;

        public bool IsHostedInGameBar => _gameBarWidget is not null;
        public bool IsPinned => _gameBarWidget?.Pinned ?? false;

        public event EventHandler? PinnedStateChanged;
        public event EventHandler? RequestedOpacityChanged;

        /// <summary>
        /// Xbox Game Bar aktivasyon nesnesi mevcutsa SDK bileşenlerini bağlar.
        /// </summary>
        public void AttachGameBarWidget(XboxGameBarWidget widget)
        {
            _gameBarWidget = widget ?? throw new ArgumentNullException(nameof(widget));
            _widgetControl = new XboxGameBarWidgetControl(_gameBarWidget);

            _gameBarWidget.PinnedChanged += OnPinnedChanged;
            _gameBarWidget.RequestedOpacityChanged += OnRequestedOpacityChanged;
        }

        public async void ActivateSettings()
        {
            if (_gameBarWidget is not null)
            {
                await _gameBarWidget.ActivateSettingsAsync();
            }
        }

        private void OnPinnedChanged(object? sender, object args)
        {
            PinnedStateChanged?.Invoke(this, EventArgs.Empty);
        }

        private void OnRequestedOpacityChanged(object? sender, object args)
        {
            RequestedOpacityChanged?.Invoke(this, EventArgs.Empty);
        }
    }
}
