using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml;
using Serilog;
using Whirtle.Client.Audio;
using Whirtle.Client.Discovery;
using Whirtle.Client.Protocol;
using Whirtle.Client.Role;
using Whirtle.Client.Transport;

namespace Whirtle.Client.UI.ViewModels;

public sealed partial class NowPlayingViewModel : ObservableObject
{
    private readonly IAudioDeviceEnumerator _deviceEnumerator;
    private readonly DispatcherQueue _dispatcher;

    private ProtocolClient? _protocol;
    private ControllerClient? _controller;
    private CancellationTokenSource? _connectionCts;
    private Task _receiveLoopTask = Task.CompletedTask;

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

    [ObservableProperty] private string? _manualUrl;

    // ── Audio devices ──────────────────────────────────────────────────────

    [ObservableProperty] private AudioDeviceInfo? _selectedDevice;

    // ── Computed / derived properties ──────────────────────────────────────

    public int     VolumePercent      => (int)(_volume * 100);
    public string  PlayPauseGlyph     => _isPlaying ? "\uE769" : "\uE768"; // Pause : Play
    public string  VolumeGlyph        => _isMuted   ? "\uE74F" : "\uE767"; // Muted : Speaker
    public string  MuteButtonTooltip  => _isMuted   ? "Unmute" : "Mute";
    public bool    IsNotConnected     => !_isConnected;
    public Visibility DisconnectVisibility => _isConnected ? Visibility.Visible : Visibility.Collapsed;

    public InfoBarSeverity ConnectionInfoSeverity
        => _isConnected ? InfoBarSeverity.Success : InfoBarSeverity.Informational;

    public string TrayTooltip
    {
        get
        {
            if (!_isConnected) return "Whirtle — Not connected";
            var parts = new[] { _title, _artist }.Where(s => s is not null);
            var desc  = string.Join(" — ", parts);
            return string.IsNullOrEmpty(desc) ? "Whirtle" : $"Whirtle — {desc}";
        }
    }

    // ── Collections ────────────────────────────────────────────────────────

    public ObservableCollection<ServiceEndpoint> DiscoveredServers { get; } = new();
    public ObservableCollection<AudioDeviceInfo> AudioDevices      { get; } = new();

    // ── Constructor ────────────────────────────────────────────────────────

    public NowPlayingViewModel(IAudioDeviceEnumerator deviceEnumerator, DispatcherQueue dispatcher)
    {
        _deviceEnumerator = deviceEnumerator;
        _dispatcher       = dispatcher;
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

        _connectionCts = new CancellationTokenSource();
        var token = _connectionCts.Token;

        try
        {
            ConnectionStatus = $"Connecting to {endpoint.Name ?? endpoint.Host}…";

            var transport = new WebSocketTransport();
            _protocol     = new ProtocolClient(transport);

            await _protocol.ConnectAsync(endpoint.ToWebSocketUri(), token);
            var hello = await _protocol.HandshakeAsync(
                $"whirtle-{Environment.MachineName}", "Whirtle",
                cancellationToken: token);

            Log.Information("Connected to {ServerId} ({ServerName}), reason={Reason}",
                hello.ServerId, hello.Name, hello.ConnectionReason);

            _controller = new ControllerClient(_protocol);
            IsConnected  = true;
            ConnectionStatus = $"Connected — {endpoint.Name ?? endpoint.Host}:{endpoint.Port}";

            // Start background message loop; tracked so DisconnectAsync can await it.
            _receiveLoopTask = ReceiveLoopAsync(token);
        }
        catch (OperationCanceledException)
        {
            ConnectionStatus = "Disconnected";
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Connection to {Host}:{Port} failed", endpoint.Host, endpoint.Port);
            ConnectionStatus = $"Connection failed: {ex.Message}";
            IsConnected = false;
        }
    }

    [RelayCommand]
    private async Task ConnectManualAsync()
    {
        if (string.IsNullOrWhiteSpace(ManualUrl))
            return;

        if (!Uri.TryCreate(ManualUrl, UriKind.Absolute, out var uri))
        {
            ConnectionStatus = "Invalid URL";
            return;
        }

        var host = uri.Host;
        var port = uri.Port > 0 ? uri.Port : MdnsAdvertiser.DefaultPort;
        var path = string.IsNullOrEmpty(uri.AbsolutePath) ? MdnsAdvertiser.DefaultPath : uri.AbsolutePath;
        var ep   = new ServiceEndpoint(host, port, path);

        await ConnectAsync(ep);
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
    }

    [RelayCommand]
    private Task PlayPauseAsync() => _isPlaying ? PauseAsync() : PlayAsync();

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
        await _controller.SkipAsync();
    }

    [RelayCommand]
    private async Task SetVolumeAsync(double normalised)
    {
        Volume = normalised;
        if (_controller is null || _isMuted) return;
        await _controller.SetVolumeAsync(normalised);
    }

    [RelayCommand]
    private async Task ToggleMuteAsync()
    {
        IsMuted = !_isMuted;
        if (_controller is null) return;
        await _controller.SetVolumeAsync(_isMuted ? 0.0 : _volume);
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
                                PositionSeconds = meta.Progress ?? 0;
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
}
