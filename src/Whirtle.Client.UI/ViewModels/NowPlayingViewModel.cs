using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml;
using Serilog;
using Whirtle.Client.Audio;
using Whirtle.Client.Clock;
using Whirtle.Client.Discovery;
using Whirtle.Client.Protocol;
using Whirtle.Client.Role;
using Whirtle.Client.Transport;

namespace Whirtle.Client.UI.ViewModels;

public sealed partial class NowPlayingViewModel : ObservableObject
{
    private readonly IAudioDeviceEnumerator _deviceEnumerator;
    private readonly DispatcherQueue _dispatcher;
    private readonly SettingsViewModel _settings;

    private ProtocolClient? _protocol;
    private ControllerClient? _controller;
    private CancellationTokenSource? _connectionCts;
    private Task _receiveLoopTask = Task.CompletedTask;

    // Signal-strength inputs — updated by clock sync and PlaybackEngine events.
    private TimeSpan _lastRtt        = TimeSpan.MaxValue; // unknown until first sync
    private int      _lastBufferCount = -1;               // -1 = engine not running

    // ── Now-playing metadata ───────────────────────────────────────────────

    [ObservableProperty] private string? _title;
    [ObservableProperty] private string? _artist;
    [ObservableProperty] private string? _album;
    [ObservableProperty] private double  _durationSeconds;
    [ObservableProperty] private double  _positionSeconds;
    [ObservableProperty] private byte[]? _albumArtData;

    // ── Playback state ─────────────────────────────────────────────────────

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(PlayPauseGlyph))]
    private bool _isPlaying;

    // ── Volume ─────────────────────────────────────────────────────────────

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(VolumePercent), nameof(VolumeGlyph), nameof(MuteButtonTooltip))]
    private bool _isMuted;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(VolumePercent))]
    private double _volume = 0.8;

    // ── Connection ─────────────────────────────────────────────────────────

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsNotConnected), nameof(DisconnectVisibility), nameof(ConnectionInfoSeverity), nameof(TrayTooltip))]
    private bool _isConnected;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(TrayTooltip))]
    private string _connectionStatus = "Not connected";

    // ── Status bar ─────────────────────────────────────────────────────────

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ServerPickerLabel))]
    private string? _serverName;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CodecDisplay))]
    private string? _codecName;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CodecDisplay))]
    private int? _sampleRate;

    [ObservableProperty] private int _signalStrength; // 0–3

    // ── Audio devices ──────────────────────────────────────────────────────

    [ObservableProperty] private AudioDeviceInfo? _selectedDevice;

    // ── Computed / derived properties ──────────────────────────────────────

    public string CodecDisplay
    {
        get
        {
            if (CodecName is null) return "";
            if (SampleRate is null) return CodecName;
            double kHz = SampleRate.Value / 1000.0;
            string kHzStr = kHz == Math.Floor(kHz) ? $"{kHz:0} kHz" : $"{kHz:0.0} kHz";
            return $"{CodecName} · {kHzStr}";
        }
    }

    /// <summary>
    /// Text shown in the status-bar server button.
    /// Shows the target/connected server name as soon as one is chosen,
    /// "Automatically connect" when in server-initiated mode with no target,
    /// or "Select a server" otherwise.
    /// </summary>
    public string ServerPickerLabel
    {
        get
        {
            if (_serverName is not null) return _serverName;
            return _settings.ConnectionMode == ConnectionMode.ServerInitiated
                ? "Automatically connect"
                : "Select a server";
        }
    }

    public int     VolumePercent      => (int)(_volume * 100);
    public string  PlayPauseGlyph     => _isPlaying ? "\uE769" : "\uE768"; // Pause : Play
    public string  VolumeGlyph        => _isMuted   ? "\uE74F" : "\uE767"; // Muted : Speaker
    public string  MuteButtonTooltip  => _isMuted   ? "Unmute" : "Mute";
    public bool    IsNotConnected     => !_isConnected;
    public Visibility DisconnectVisibility => _isConnected ? Visibility.Visible : Visibility.Collapsed;

    public InfoBarSeverity ConnectionInfoSeverity
        => IsConnected ? InfoBarSeverity.Success : InfoBarSeverity.Informational;

    public string TrayTooltip
    {
        get
        {
            if (!IsConnected) return "Whirtle — Not connected";
            var parts = new[] { Title, Artist }.Where(s => s is not null);
            var desc  = string.Join(" — ", parts);
            return string.IsNullOrEmpty(desc) ? "Whirtle" : $"Whirtle — {desc}";
        }
    }

    // ── Collections ────────────────────────────────────────────────────────

    public ObservableCollection<ServiceEndpoint> DiscoveredServers { get; } = new();
    public ObservableCollection<AudioDeviceInfo> AudioDevices      { get; } = new();

    /// <summary>Manually-added servers, persisted in settings.</summary>
    public ObservableCollection<PersistedServer> SavedServers => _settings.SavedServers;

    // ── Constructor ────────────────────────────────────────────────────────

    public NowPlayingViewModel(
        IAudioDeviceEnumerator deviceEnumerator,
        DispatcherQueue        dispatcher,
        SettingsViewModel      settings)
    {
        _deviceEnumerator = deviceEnumerator;
        _dispatcher       = dispatcher;
        _settings         = settings;

        // Keep ServerPickerLabel in sync when connection mode changes in settings.
        _settings.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(SettingsViewModel.ConnectionMode))
                OnPropertyChanged(nameof(ServerPickerLabel));
        };

        LoadAudioDevices();
    }

    private void LoadAudioDevices()
    {
        AudioDevices.Clear();
        foreach (var d in _deviceEnumerator.GetDevices(AudioDeviceKind.Output))
            AudioDevices.Add(d);

        SelectedDevice = _deviceEnumerator.GetDefault(AudioDeviceKind.Output)
                      ?? AudioDevices.FirstOrDefault();
    }

    // ── Commands ───────────────────────────────────────────────────────────

    [RelayCommand]
    private async Task ConnectAsync(ServiceEndpoint endpoint)
    {
        await DisconnectAsync();

        // Show the target in the status bar immediately, before any awaits.
        ServerName = endpoint.DisplayName;

        _connectionCts = new CancellationTokenSource();
        var token = _connectionCts.Token;

        try
        {
            ConnectionStatus = $"Connecting to {endpoint.DisplayName}…";

            var transport = new WebSocketTransport();
            _protocol     = new ProtocolClient(transport);

            await _protocol.ConnectAsync(endpoint.ToWebSocketUri(), token);
            var hello = await _protocol.HandshakeAsync(
                $"whirtle-{Environment.MachineName}", "Whirtle",
                cancellationToken: token);

            Log.Information("Connected to {ServerId} ({ServerName}), reason={Reason}",
                hello.ServerId, hello.Name, hello.ConnectionReason);

            // Initial clock sync — establishes RTT for signal strength.
            var syncer = new ClockSynchronizer(_protocol);
            var sync   = await syncer.SyncOnceAsync(token);
            _lastRtt = sync.RoundTripTime;
            SignalStrength = ComputeSignalStrength(_lastRtt, _lastBufferCount);

            _controller  = new ControllerClient(_protocol);
            IsConnected  = true;
            ConnectionStatus = $"Connected — {endpoint.Host}:{endpoint.Port}";

            // Start background message loop; tracked so DisconnectAsync can await it.
            _receiveLoopTask = ReceiveLoopAsync(token);
        }
        catch (OperationCanceledException)
        {
            ServerName = null;
            ConnectionStatus = "Disconnected";
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Connection to {Host}:{Port} failed", endpoint.Host, endpoint.Port);
            ServerName = null;
            ConnectionStatus = $"Connection failed: {ex.Message}";
            IsConnected = false;
        }
    }

    /// <summary>
    /// Connects to a manually-saved server entry.
    /// Sets connection mode to ClientInitiated before connecting.
    /// </summary>
    [RelayCommand]
    private async Task ConnectToSavedAsync(PersistedServer saved)
    {
        _settings.ConnectionMode = ConnectionMode.ClientInitiated;
        var ep = new ServiceEndpoint(saved.Host, saved.Port, saved.Path, saved.Label);
        await ConnectAsync(ep);
    }

    /// <summary>
    /// Switches to server-initiated (auto-connect) mode and disconnects any
    /// active session so the server can re-initiate via mDNS.
    /// </summary>
    [RelayCommand]
    private async Task SetAutoConnectModeAsync()
    {
        _settings.ConnectionMode = ConnectionMode.ServerInitiated;
        ServerName = null;  // Update status bar immediately; don't wait for graceful close
        if (_isConnected)
            await DisconnectAsync();
    }

    /// <summary>
    /// Parses a raw user input string into a <see cref="ServiceEndpoint"/>,
    /// saves the entry to settings, and connects.
    /// </summary>
    internal async Task AddSavedServerAsync(string input)
    {
        input = input.Trim();
        var ep = ParseServerInput(input);
        var saved = new PersistedServer(input, ep.Host, ep.Port, ep.Path);
        _settings.AddSavedServer(saved);
        _settings.ConnectionMode = ConnectionMode.ClientInitiated;
        await ConnectAsync(ep);
    }

    internal void RemoveSavedServer(PersistedServer saved)
        => _settings.RemoveSavedServer(saved);

    private static ServiceEndpoint ParseServerInput(string input)
    {
        // If the input already contains a scheme, parse it as a URI.
        if (input.Contains("://") && Uri.TryCreate(input, UriKind.Absolute, out var uri))
        {
            var port = uri.Port > 0 ? uri.Port : MdnsAdvertiser.DefaultPort;
            var path = string.IsNullOrEmpty(uri.AbsolutePath) ? MdnsAdvertiser.DefaultPath : uri.AbsolutePath;
            return new ServiceEndpoint(uri.Host, port, path);
        }

        // Otherwise treat the whole string as a hostname or IP address.
        return new ServiceEndpoint(input, MdnsAdvertiser.DefaultPort, MdnsAdvertiser.DefaultPath);
    }

    [RelayCommand]
    private async Task DisconnectAsync()
    {
        _connectionCts?.Cancel();
        _connectionCts = null;

        // Wait for the receive loop to finish before tearing down the protocol,
        // so it cannot access _protocol after it has been disposed.
        try { await _receiveLoopTask.ConfigureAwait(false); } catch { /* already handled inside */ }
        _receiveLoopTask = Task.CompletedTask;

        if (_protocol is not null)
        {
            try   { await _protocol.DisconnectAsync(); } catch { /* best-effort */ }
            await _protocol.DisposeAsync();
            _protocol = null;
        }

        _controller  = null;
        IsConnected  = false;
        ConnectionStatus = "Not connected";

        ServerName     = null;
        CodecName      = null;
        SampleRate     = null;
        SignalStrength  = 0;
        _lastRtt        = TimeSpan.MaxValue;
        _lastBufferCount = -1;
    }

    [RelayCommand]
    private Task PlayPauseAsync() => IsPlaying ? PauseAsync() : PlayAsync();

    [RelayCommand]
    private async Task PlayAsync()
    {
        if (_controller is null) return;
        await _controller.PlayAsync();
        IsPlaying = true;
    }

    [RelayCommand]
    private async Task PauseAsync()
    {
        if (_controller is null) return;
        await _controller.PauseAsync();
        IsPlaying = false;
    }

    [RelayCommand]
    private async Task SkipAsync()
    {
        if (_controller is null) return;
        await _controller.NextAsync();
    }

    [RelayCommand]
    private async Task SetVolumeAsync(double normalised)
    {
        Volume = normalised;
        if (_controller is null || IsMuted) return;
        await _controller.SetVolumeAsync(normalised);
    }

    [RelayCommand]
    private async Task ToggleMuteAsync()
    {
        IsMuted = !IsMuted;
        if (_controller is null) return;
        await _controller.SetVolumeAsync(IsMuted ? 0.0 : Volume);
    }

    [RelayCommand]
    private Task SeekAsync(double positionSeconds)
    {
        // TODO: Send a "seek" command once the Sendspin protocol defines one.
        PositionSeconds = positionSeconds;
        return Task.CompletedTask;
    }

    // ── Receive loop ────────────────────────────────────────────────────────

    private async Task ReceiveLoopAsync(CancellationToken cancellationToken)
    {
        if (_protocol is null) return;

        try
        {
            await foreach (var frame in _protocol.ReceiveAllAsync(cancellationToken))
            {
                switch (frame)
                {
                    case ProtocolFrame { Message: ServerStateMessage msg }:
                        if (msg.Metadata is { } meta)
                        {
                            _dispatcher.TryEnqueue(() =>
                            {
                                Title           = meta.Title;
                                Artist          = meta.Artist;
                                Album           = meta.Album;
                                PositionSeconds = meta.Progress is { } p ? p.TrackProgress / 1000.0 : 0;
                                OnPropertyChanged(nameof(TrayTooltip));
                            });
                        }
                        if (msg.Controller is { } ctrl)
                        {
                            _dispatcher.TryEnqueue(() =>
                            {
                                Volume  = ctrl.Volume / 100.0;
                                IsMuted = ctrl.Muted;
                            });
                        }
                        break;

                    case ProtocolFrame { Message: StreamStartMessage { Player: { } sp } }:
                        var codec      = sp.Codec.ToUpperInvariant();
                        var sampleRate = sp.SampleRate;
                        _dispatcher.TryEnqueue(() =>
                        {
                            CodecName  = codec;
                            SampleRate = sampleRate;
                        });
                        break;

                    case ArtworkFrame artwork:
                        _dispatcher.TryEnqueue(() => AlbumArtData = artwork.Data);
                        break;
                }
            }
        }
        catch (OperationCanceledException) { /* normal shutdown */ }
        catch (Exception ex)
        {
            Log.Warning(ex, "Connection lost");
            _dispatcher.TryEnqueue(() =>
            {
                IsConnected      = false;
                ConnectionStatus = "Connection lost";
            });
        }
    }

    // ── Signal strength ────────────────────────────────────────────────────

    /// <summary>
    /// Updates signal strength from the latest clock sync result.
    /// Call from the UI thread or marshal via the dispatcher.
    /// </summary>
    internal void UpdateSignalStrength(TimeSpan rtt, int bufferCount)
    {
        _lastRtt         = rtt;
        _lastBufferCount = bufferCount;
        SignalStrength   = ComputeSignalStrength(rtt, bufferCount);
    }

    /// <summary>
    /// Maps RTT and buffer count onto a 0–3 signal level.
    /// Both metrics are scored independently; the lower score wins (worst-case).
    /// When the playback engine is not running (<paramref name="bufferCount"/> == -1),
    /// only the sync score is used.
    /// </summary>
    private static int ComputeSignalStrength(TimeSpan rtt, int bufferCount)
    {
        int syncScore = rtt.TotalMilliseconds switch
        {
            <= 5  => 4,
            <= 15 => 3,
            <= 30 => 2,
            <= 50 => 1,
            _     => 0,
        };

        if (bufferCount < 0)
            return syncScore;

        int bufferScore = bufferCount switch
        {
            >= 12 => 4,
            >= 8  => 3,
            >= 4  => 2,
            >= 2  => 1,
            _     => 0,
        };

        return Math.Min(syncScore, bufferScore);
    }
}
