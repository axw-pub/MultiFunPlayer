using MultiFunPlayer.Common;
using MultiFunPlayer.Input;
using MultiFunPlayer.Script;
using MultiFunPlayer.UI;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using NLog;
using Stylet;
using System.ComponentModel;
using System.IO;
using System.Net.WebSockets;
using System.Text;

namespace MultiFunPlayer.MediaSource.ViewModels;

[DisplayName("OFS")]
internal sealed class OfsMediaSource(IShortcutManager shortcutManager, IEventAggregator eventAggregator) : AbstractMediaSource(shortcutManager, eventAggregator)
{
    private static Logger Logger { get; } = LogManager.GetCurrentClassLogger();

    public override ConnectionStatus Status { get; protected set; }

    public Uri Uri { get; set; } = new Uri("ws://127.0.0.1:8080/ofs");

    public bool IsConnected => Status == ConnectionStatus.Connected;
    public bool IsConnectBusy => Status == ConnectionStatus.Connecting || Status == ConnectionStatus.Disconnecting;
    public bool CanToggleConnect => !IsConnectBusy;

    protected override async Task RunAsync(CancellationToken token)
    {
        try
        {
            using var client = new ClientWebSocket();

            Logger.Info("Connecting to {0} at \"{1}\"", Name, Uri.ToString());
            await client.ConnectAsync(Uri, token)
                        .WithCancellation(1000);

            Status = ConnectionStatus.Connected;

            using var cancellationSource = CancellationTokenSource.CreateLinkedTokenSource(token);
            var task = await Task.WhenAny(ReadAsync(client, cancellationSource.Token), WriteAsync(client, cancellationSource.Token));
            cancellationSource.Cancel();

            task.ThrowIfFaulted();
        }
        catch (OperationCanceledException) { }
        catch (Exception e)
        {
            Logger.Error(e, $"{Name} failed with exception");
            _ = DialogHelper.ShowErrorAsync(e, $"{Name} failed with exception", "RootDialog");
        }

        if (IsDisposing)
            return;

        PublishMessage(new MediaPathChangedMessage(null));
        PublishMessage(new MediaPlayingChangedMessage(false));
    }

    private async Task ReadAsync(ClientWebSocket client, CancellationToken token)
    {
        try
        {
            while (!token.IsCancellationRequested && client.State == WebSocketState.Open)
            {
                var message = Encoding.UTF8.GetString(await client.ReceiveAsync(token));
                if (message == null)
                    continue;

                try
                {
                    Logger.Trace("Received \"{0}\" from \"{1}\"", message, Name);

                    var document = JObject.Parse(message);
                    if (!document.TryGetValue<string>("type", out var type) || !string.Equals(type, "event", StringComparison.OrdinalIgnoreCase))
                        continue;

                    if (!document.TryGetValue<string>("name", out var eventName) || !document.TryGetObject(out var dataToken, "data"))
                        continue;

                    switch (eventName)
                    {
                        case "media_change":
                            PublishMessage(new MediaPathChangedMessage(dataToken.TryGetValue<string>("path", out var path) && !string.IsNullOrWhiteSpace(path) ? path : null, ReloadScripts: false));
                            break;
                        case "play_change":
                            if (dataToken.TryGetValue<bool>("playing", out var isPlaying))
                                PublishMessage(new MediaPlayingChangedMessage(isPlaying));
                            break;
                        case "duration_change":
                            if (dataToken.TryGetValue<double>("duration", out var duration) && duration >= 0)
                                PublishMessage(new MediaDurationChangedMessage(TimeSpan.FromSeconds(duration)));
                            break;
                        case "time_change":
                            if (dataToken.TryGetValue<double>("time", out var position) && position >= 0)
                                PublishMessage(new MediaPositionChangedMessage(TimeSpan.FromSeconds(position), ForceSeek: true));
                            break;
                        case "playbackspeed_change":
                            if (dataToken.TryGetValue<double>("speed", out var speed) && speed > 0)
                                PublishMessage(new MediaSpeedChangedMessage(speed));
                            break;
                        case "funscript_change":
                            {
                                if (!dataToken.TryGetObject(out var funscriptToken, "funscript"))
                                    break;
                                if (!dataToken.TryGetValue<string>("name", out var name) || string.IsNullOrWhiteSpace(name))
                                    break;
                                if (!Path.HasExtension(name) || !string.Equals(Path.GetExtension(name), ".funscript", StringComparison.OrdinalIgnoreCase))
                                    name += ".funscript";

                                var readerResult = FunscriptReader.Default.FromText(name, Uri.ToString(), funscriptToken.ToString());
                                if (!readerResult.IsSuccess)
                                    break;

                                if (readerResult.IsMultiAxis)
                                {
                                    PublishMessage(new ChangeScriptMessage(readerResult.Resources));
                                }
                                else
                                {
                                    var axes = DeviceAxisUtils.FindAxesMatchingName(name);
                                    if (axes.Any())
                                        PublishMessage(new ChangeScriptMessage(axes.ToDictionary(a => a, _ => readerResult.Resource)));
                                }
                            }

                            break;
                        case "funscript_remove":
                            {
                                if (!dataToken.TryGetValue<string>("name", out var name) || string.IsNullOrWhiteSpace(name))
                                    break;
                                if (!Path.HasExtension(name) || !string.Equals(Path.GetExtension(name), ".funscript", StringComparison.OrdinalIgnoreCase))
                                    name += ".funscript";

                                var axes = DeviceAxisUtils.FindAxesMatchingName(name);
                                if (!axes.Any())
                                    break;

                                PublishMessage(new ChangeScriptMessage(axes.ToDictionary(a => a, _ => default(IScriptResource))));
                            }

                            break;
                        case "project_change":
                            PublishMessage(new MediaPathChangedMessage(null, ReloadScripts: false));
                            PublishMessage(new MediaPlayingChangedMessage(false));
                            break;
                    }
                }
                catch (JsonException) { }
            }
        }
        catch (OperationCanceledException) { }
    }

    private async Task WriteAsync(ClientWebSocket client, CancellationToken token)
    {
        static string CreateMessage(string commandName, string dataName, string dataValue)
            => @$"{{ ""type"": ""command"", ""name"": ""{commandName}"", ""data"": {{ ""{dataName}"": {dataValue} }} }}";

        try
        {
            while (!token.IsCancellationRequested && client.State == WebSocketState.Open)
            {
                await WaitForMessageAsync(token);
                var message = await ReadMessageAsync(token);

                var messageString = message switch
                {
                    MediaPlayPauseMessage playPauseMessage => CreateMessage("change_play", "playing", playPauseMessage.ShouldBePlaying.ToString().ToLower()),
                    MediaSeekMessage seekMessage => CreateMessage("change_time", "time", seekMessage.Position.TotalSeconds.ToString("F4").Replace(',', '.')),
                    MediaChangeSpeedMessage changeSpeedMessage => CreateMessage("change_playbackspeed", "speed", changeSpeedMessage.Speed.ToString("F4").Replace(',', '.')),
                    _ => null
                };

                if (string.IsNullOrWhiteSpace(messageString))
                    continue;

                Logger.Trace("Sending \"{0}\" to \"{1}\"", messageString, Name);
                await client.SendAsync(Encoding.UTF8.GetBytes(messageString), WebSocketMessageType.Text, true, token);
            }
        }
        catch (OperationCanceledException) { }
    }
    public override void HandleSettings(JObject settings, SettingsAction action)
    {
        base.HandleSettings(settings, action);

        if (action == SettingsAction.Saving)
        {
            settings[nameof(Uri)] = Uri?.ToString();
        }
        else if (action == SettingsAction.Loading)
        {
            if (settings.TryGetValue<Uri>(nameof(Uri), out var uri))
                Uri = uri;
        }
    }

    public override async ValueTask<bool> CanConnectAsync(CancellationToken token)
    {
        if (Uri == null)
            return false;

        try
        {
            using var client = new ClientWebSocket();
            await client.ConnectAsync(Uri, token);

            var result = client.State == WebSocketState.Open;
            await client.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, null, token);
            return result;
        }
        catch
        {
            return false;
        }
    }

    protected override void RegisterActions(IShortcutManager s)
    {
        base.RegisterActions(s);

        #region Uri
        s.RegisterAction<string>($"{Name}::Uri::Set", s => s.WithLabel("Uri").WithDescription("ofs websocket uri"), uriString =>
        {
            if (Uri.TryCreate(uriString, UriKind.Absolute, out var uri))
                Uri = uri;
        });
        #endregion
    }
}
