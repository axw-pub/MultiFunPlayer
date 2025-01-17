using MultiFunPlayer.Common;
using MultiFunPlayer.Input;
using MultiFunPlayer.Input.RawInput;
using MultiFunPlayer.Input.TCode;
using MultiFunPlayer.Input.XInput;
using MultiFunPlayer.UI.Dialogs.ViewModels;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using NLog;
using Stylet;
using System.ComponentModel;
using System.Threading.Channels;
using System.Windows;
using System.Windows.Data;

namespace MultiFunPlayer.UI.Controls.ViewModels;

[JsonObject(MemberSerialization = MemberSerialization.OptIn)]
internal sealed class ShortcutSettingsViewModel : Screen, IHandle<SettingsMessage>, IDisposable
{
    private static readonly Logger Logger = LogManager.GetCurrentClassLogger();

    private readonly IInputProcessorManager _inputManager;
    private readonly IShortcutManager _shortcutManager;
    private readonly IShortcutBinder _shortcutBinder;
    private readonly Channel<IInputGesture> _captureGestureChannel;
    private CancellationTokenSource _captureGestureCancellationSource;

    public string ActionsFilter { get; set; }
    public ICollectionView AvailableActionsView { get; }
    public IReadOnlyObservableConcurrentCollection<string> AvailableActions => _shortcutManager.AvailableActions;
    public IReadOnlyObservableConcurrentCollection<IShortcutBinding> Bindings => _shortcutBinder.Bindings;

    public bool IsCapturingGesture { get; private set; }
    public IInputGestureDescriptor CapturedGesture { get; set; }
    public IShortcutBinding SelectedBinding { get; set; }

    [JsonProperty] public bool IsKeyboardKeysGestureEnabled { get; set; } = true;
    [JsonProperty] public bool IsMouseAxisGestureEnabled { get; set; } = false;
    [JsonProperty] public bool IsMouseButtonGestureEnabled { get; set; } = false;
    [JsonProperty] public bool IsGamepadAxisGestureEnabled { get; set; } = true;
    [JsonProperty] public bool IsGamepadButtonGestureEnabled { get; set; } = true;
    [JsonProperty] public bool IsTCodeButtonGestureEnabled { get; set; } = true;
    [JsonProperty] public bool IsTCodeAxisGestureEnabled { get; set; } = true;

    public ShortcutSettingsViewModel(IInputProcessorManager inputManager, IShortcutManager shortcutManager, IShortcutBinder shortcutBinder, IEventAggregator eventAggregator)
    {
        DisplayName = "Shortcut";
        _inputManager = inputManager;
        _shortcutManager = shortcutManager;
        _shortcutBinder = shortcutBinder;

        Logger.Debug($"Initialized with {shortcutManager.AvailableActions.Count} available actions");

        eventAggregator.Subscribe(this);

        AvailableActionsView = CollectionViewSource.GetDefaultView(AvailableActions);
        AvailableActionsView.Filter = o =>
        {
            if (o is not string actionName)
                return false;
            if (SelectedBinding == null)
                return false;

            if (!_shortcutManager.ActionAcceptsGesture(actionName, SelectedBinding.Gesture))
                return false;

            if (!string.IsNullOrWhiteSpace(ActionsFilter))
            {
                var filterWords = ActionsFilter.Split(' ');
                if (!filterWords.All(w => actionName.Contains(w, StringComparison.OrdinalIgnoreCase)))
                    return false;
            }

            return true;
        };

        _inputManager.OnGesture += HandleGesture;
        _captureGestureChannel = Channel.CreateBounded<IInputGesture>(new BoundedChannelOptions(1)
        {
            FullMode = BoundedChannelFullMode.DropOldest,
            SingleReader = true,
            SingleWriter = true
        });

        PropertyChanged += (s, e) =>
        {
            if (e.PropertyName == nameof(ActionsFilter) || e.PropertyName == nameof(SelectedBinding))
                AvailableActionsView.Refresh();
        };

        RegisterActions(_shortcutManager);
    }

    protected override void OnActivate() => _shortcutBinder.HandleGestures = false;
    protected override void OnDeactivate() => _shortcutBinder.HandleGestures = true;

    private void HandleGesture(object sender, IInputGesture gesture)
    {
        if (!IsCapturingGesture)
            return;

        switch (gesture)
        {
            case KeyboardGesture when !IsKeyboardKeysGestureEnabled:
            case MouseAxisGesture when !IsMouseAxisGestureEnabled:
            case MouseButtonGesture when !IsMouseButtonGestureEnabled:
            case GamepadAxisGesture when !IsGamepadAxisGestureEnabled:
            case GamepadButtonGesture when !IsGamepadButtonGestureEnabled:
            case TCodeButtonGesture when !IsTCodeButtonGestureEnabled:
            case TCodeAxisGesture when !IsTCodeAxisGestureEnabled:
            case IAxisInputGesture axisGesture when Math.Abs(axisGesture.Delta) < 0.01:
                return;
        }

        _captureGestureChannel.Writer.TryWrite(gesture);
    }

    public async void CaptureGesture(object sender, RoutedEventArgs e)
    {
        if (IsCapturingGesture)
            return;

        if (!IsKeyboardKeysGestureEnabled && !IsMouseAxisGestureEnabled
        && !IsMouseButtonGestureEnabled && !IsGamepadAxisGestureEnabled
        && !IsGamepadButtonGestureEnabled && !IsTCodeButtonGestureEnabled
        && !IsTCodeAxisGestureEnabled)
            return;

        _captureGestureCancellationSource = new CancellationTokenSource();
        await TryCaptureGestureAsync(_captureGestureCancellationSource.Token);
        _captureGestureCancellationSource.Dispose();
        _captureGestureCancellationSource = null;
    }

    public void AddGesture(object sender, RoutedEventArgs e)
    {
        if (_captureGestureCancellationSource?.IsCancellationRequested == false)
            _captureGestureCancellationSource?.Cancel();

        if (CapturedGesture == null)
            return;

        SelectedBinding = _shortcutBinder.GetOrCreateBinding(CapturedGesture);
        CapturedGesture = null;
    }

    public void RemoveGesture(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement element || element.DataContext is not IShortcutBinding binding)
            return;

        _shortcutBinder.RemoveBinding(binding);
    }

    public void AssignAction(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement element || element.DataContext is not string actionName)
            return;
        if (SelectedBinding == null)
            return;

        _shortcutBinder.BindAction(SelectedBinding.Gesture, actionName);
    }

    public void RemoveAssignedAction(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement element || element.DataContext is not IShortcutActionConfiguration configuration)
            return;
        if (SelectedBinding == null)
            return;

        _shortcutBinder.UnbindAction(SelectedBinding.Gesture, configuration);
    }

    public void MoveAssignedActionUp(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement element || element.DataContext is not IShortcutActionConfiguration configuration)
            return;
        if (SelectedBinding == null)
            return;

        var configurations = SelectedBinding.Configurations;
        var index = configurations.IndexOf(configuration);
        if (index == 0)
            return;

        configurations.Move(index, index - 1);
    }

    public void ConfigureAssignedAction(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement element || element.DataContext is not IShortcutActionConfiguration configuration)
            return;

        _ = DialogHelper.ShowOnUIThreadAsync(new ShortcutActionConfigurationDialog(configuration), "SettingsDialog");
    }

    public void MoveAssignedActionDown(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement element || element.DataContext is not IShortcutActionConfiguration configuration)
            return;
        if (SelectedBinding == null)
            return;

        var configurations = SelectedBinding.Configurations;
        var index = configurations.IndexOf(configuration);
        if (index == configurations.Count - 1)
            return;

        configurations.Move(index, index + 1);
    }

    private async Task TryCaptureGestureAsync(CancellationToken token)
    {
        bool ValidateGesture(IInputGesture gesture)
            => !_shortcutBinder.ContainsBinding(gesture.Descriptor);

        var tryCount = 0;
        var gesture = default(IInputGesture);

        while (_captureGestureChannel.Reader.TryRead(out var _));

        IsCapturingGesture = true;

        try
        {
            do
            {
                _ = await _captureGestureChannel.Reader.WaitToReadAsync(token);
                gesture = await _captureGestureChannel.Reader.ReadAsync(token);
            } while (!token.IsCancellationRequested && !ValidateGesture(gesture) && tryCount++ < 5);
        }
        catch (OperationCanceledException) { }

        IsCapturingGesture = false;
        if (token.IsCancellationRequested || tryCount >= 5)
            gesture = null;

        CapturedGesture = gesture?.Descriptor;
    }

    public void Handle(SettingsMessage message)
    {
        if (message.Action == SettingsAction.Saving)
        {
            var settings = JObject.FromObject(this);
            settings[nameof(Bindings)] = JArray.FromObject(Bindings);

            message.Settings["Shortcuts"] = settings;
        }
        else if (message.Action == SettingsAction.Loading)
        {
            if (!message.Settings.TryGetObject(out var settings, "Shortcuts"))
                return;

            if (settings.TryGetValue<bool>(nameof(IsKeyboardKeysGestureEnabled), out var isKeyboardKeysGestureEnabled))
                IsKeyboardKeysGestureEnabled = isKeyboardKeysGestureEnabled;
            if (settings.TryGetValue<bool>(nameof(IsMouseAxisGestureEnabled), out var isMouseAxisGestureEnabled))
                IsMouseAxisGestureEnabled = isMouseAxisGestureEnabled;
            if (settings.TryGetValue<bool>(nameof(IsMouseButtonGestureEnabled), out var isMouseButtonGestureEnabled))
                IsMouseButtonGestureEnabled = isMouseButtonGestureEnabled;
            if (settings.TryGetValue<bool>(nameof(IsGamepadAxisGestureEnabled), out var isGamepadAxisGestureEnabled))
                IsGamepadAxisGestureEnabled = isGamepadAxisGestureEnabled;
            if (settings.TryGetValue<bool>(nameof(IsGamepadButtonGestureEnabled), out var isGamepadButtonGestureEnabled))
                IsGamepadButtonGestureEnabled = isGamepadButtonGestureEnabled;
            if (settings.TryGetValue<bool>(nameof(IsTCodeButtonGestureEnabled), out var isTCodeButtonGestureEnabled))
                IsTCodeButtonGestureEnabled = isTCodeButtonGestureEnabled;

            if (settings.TryGetValue<List<ShortcutBinding>>(nameof(Bindings), out var bindings))
            {
                _shortcutBinder.Clear();
                foreach (var binding in bindings)
                    _shortcutBinder.AddBinding(binding);
            }
        }
    }

    private void RegisterActions(IShortcutManager s)
    {
        #region Shortcut::Enabled
        var bindingGesturesView = Bindings.CreateView(x => x.Gesture);
        s.RegisterAction<IInputGestureDescriptor, bool>("Shortcut::Enabled::Set",
            s => s.WithLabel("Target shortcut").WithItemsSource(bindingGesturesView, true),
            s => s.WithLabel("Enabled"),
            (descriptor, enabled) =>
            {
                var binding = _shortcutBinder.GetBinding(descriptor);
                if (binding != null)
                    binding.Enabled = enabled;
            });

        s.RegisterAction<IInputGestureDescriptor>("Shortcut::Enabled::Toggle",
            s => s.WithLabel("Target shortcut").WithItemsSource(bindingGesturesView, true),
            descriptor =>
            {
                var binding = _shortcutBinder.GetBinding(descriptor);
                if (binding != null)
                    binding.Enabled = !binding.Enabled;
            });
        #endregion
    }

    private void Dispose(bool disposing)
    {
        _inputManager.OnGesture -= HandleGesture;
    }

    public void Dispose()
    {
        Dispose(disposing: true);
        GC.SuppressFinalize(this);
    }
}