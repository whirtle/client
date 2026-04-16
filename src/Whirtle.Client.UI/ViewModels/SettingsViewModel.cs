using System.Collections.ObjectModel;
using System.Text.Json;
using System.Text.Json.Serialization;
using CommunityToolkit.Mvvm.ComponentModel;
using Serilog;
using Whirtle.Client.Audio;
using Whirtle.Client.Codec;

namespace Whirtle.Client.UI.ViewModels;

public sealed partial class SettingsViewModel : ObservableObject
{
    private static readonly string SettingsPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "Whirtle",
        "settings.json");

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        Converters    = { new JsonStringEnumConverter() },
    };

    // Debounce: cancel any pending save and start a fresh 500 ms timer so
    // rapid property changes (e.g. typing in a text box) produce a single write.
    private CancellationTokenSource? _saveCts;
    private readonly object _saveCtsLock = new();

    // Snapshot for OK/Cancel support (connection mode excluded: it changes via the
    // server picker, outside the Settings dialog lifecycle).
    private record SettingsSnapshot(
        string ClientName, string ClientId, string PreferredAudioDeviceId,
        Dictionary<string, DeviceSettings> DeviceSettings,
        ConnectionMode ConnectionMode, string LogLevel,
        bool TermsAccepted, bool TelemetryConsent);

    private SettingsSnapshot? _snapshot;
    private bool _suppressSave;

    // Per-device settings storage
    private Dictionary<string, DeviceSettings> _deviceSettings = new();

    // ── Window position ────────────────────────────────────────────────────
    private int? _windowX;
    private int? _windowY;

    public int? WindowX => _windowX;
    public int? WindowY => _windowY;

    public void SaveWindowPosition(int x, int y)
    {
        _windowX = x;
        _windowY = y;
        Save();
    }

    // ── Logs window bounds ─────────────────────────────────────────────────
    private int? _logsWindowX;
    private int? _logsWindowY;
    private int? _logsWindowWidth;
    private int? _logsWindowHeight;

    public int? LogsWindowX      => _logsWindowX;
    public int? LogsWindowY      => _logsWindowY;
    public int? LogsWindowWidth  => _logsWindowWidth;
    public int? LogsWindowHeight => _logsWindowHeight;

    public void SaveLogsWindowBounds(int x, int y, int width, int height)
    {
        _logsWindowX      = x;
        _logsWindowY      = y;
        _logsWindowWidth  = width;
        _logsWindowHeight = height;
        Save();
    }

    // ── Stats window bounds ────────────────────────────────────────────────
    private int? _statsWindowX;
    private int? _statsWindowY;
    private int? _statsWindowWidth;
    private int? _statsWindowHeight;

    public int? StatsWindowX      => _statsWindowX;
    public int? StatsWindowY      => _statsWindowY;
    public int? StatsWindowWidth  => _statsWindowWidth;
    public int? StatsWindowHeight => _statsWindowHeight;

    public void SaveStatsWindowBounds(int x, int y, int width, int height)
    {
        _statsWindowX      = x;
        _statsWindowY      = y;
        _statsWindowWidth  = width;
        _statsWindowHeight = height;
        Save();
    }

    // ── Last-played server (multi-server tiebreak) ────────────────────────
    private string? _lastPlayedServerId;

    public string? LastPlayedServerId
    {
        get => _lastPlayedServerId;
        set { _lastPlayedServerId = value; Save(); }
    }

    // ── Volume / mute (global, persisted) ─────────────────────────────────
    private double _volume  = 0.8;
    private bool   _isMuted = false;

    public double Volume  => _volume;
    public bool   IsMuted => _isMuted;

    public void SaveVolume(double volume, bool isMuted)
    {
        _volume  = volume;
        _isMuted = isMuted;
        Save();
    }

    // ── Persistent settings ────────────────────────────────────────────────

    [ObservableProperty] private string _clientName = Environment.MachineName;
    [ObservableProperty] private string _clientId   = Guid.NewGuid().ToString("N");

    [ObservableProperty] private string _preferredAudioDeviceId = string.Empty;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(AudioFormatIndex))]
    private AudioFormat _currentDeviceFormat = AudioFormat.Flac;

    [ObservableProperty] private int _currentDeviceStaticDelayMs = 0;

    partial void OnCurrentDeviceStaticDelayMsChanged(int value)
    {
        if (value < 0) CurrentDeviceStaticDelayMs = 0;
    }

    partial void OnTermsAcceptedChanged(bool value)
    {
        if (value) CommitNow();
    }

    [ObservableProperty] private ConnectionMode _connectionMode = ConnectionMode.ServerInitiated;

    [ObservableProperty] private string _logLevel = "Information";

    partial void OnLogLevelChanged(string value)
        => Log.Information("Log level changed to {LogLevel}", value);

    [ObservableProperty] private bool _termsAccepted;
    [ObservableProperty] private bool _telemetryConsent;

    // ── Non-persisted: populated at runtime ───────────────────────────────

    [ObservableProperty] private AudioDeviceInfo? _selectedAudioDevice;

    public ObservableCollection<AudioDeviceInfo>  AudioDevices  { get; } = new();
    public ObservableCollection<PersistedServer>  SavedServers  { get; } = new();
    partial void OnSelectedAudioDeviceChanged(AudioDeviceInfo? value)
    {
        if (value is null) return;
        // Suppress save while loading per-device values so switching devices
        // does not trigger a spurious write.
        _suppressSave = true;
        try
        {
            var ds = GetDeviceSettings(value.Id);
            CurrentDeviceFormat        = ds.PreferredFormat;
            CurrentDeviceStaticDelayMs = ds.StaticDelayMs;
        }
        finally
        {
            _suppressSave = false;
        }
    }

    // ── Static option lists ────────────────────────────────────────────────

    public IReadOnlyList<string> AudioFormatOptions { get; } =
        ["FLAC (lossless, recommended)", "Opus", "PCM (uncompressed)"];

    public IReadOnlyList<string> LogLevelOptions { get; } =
        ["Verbose", "Debug", "Information", "Warning", "Error"];

    // ── Index helpers (for ComboBox SelectedIndex binding) ─────────────────

    public int AudioFormatIndex
    {
        get => _currentDeviceFormat switch
        {
            AudioFormat.Flac => 0,
            AudioFormat.Opus => 1,
            _                => 2,
        };
        set => CurrentDeviceFormat = value switch
        {
            0 => AudioFormat.Flac,
            1 => AudioFormat.Opus,
            _ => AudioFormat.Pcm,
        };
    }

    public int ConnectionModeIndex
    {
        get => ConnectionMode == ConnectionMode.ServerInitiated ? 0 : 1;
        set => ConnectionMode  = value == 0
            ? ConnectionMode.ServerInitiated
            : ConnectionMode.ClientInitiated;
    }

    // ── Clean start ────────────────────────────────────────────────────────

    /// <summary>
    /// Deletes the persisted settings file. Call before constructing
    /// <see cref="SettingsViewModel"/> to force a clean first-run state.
    /// </summary>
    public static void DeleteSettingsFile()
    {
        try
        {
            if (File.Exists(SettingsPath))
                File.Delete(SettingsPath);
        }
        catch { /* best-effort */ }
    }

    // ── Constructor ────────────────────────────────────────────────────────

    public SettingsViewModel()
    {
        Load();

        // Auto-save on every property change, debounced to avoid a disk write
        // for every keystroke when the user edits a text field.
        PropertyChanged += (_, _) => DebouncedSave();

        // Populate device list using the same enumerator as NowPlayingViewModel
        // (best-effort; may be empty on non-Windows or when NAudio not available)
        try
        {
            var enumerator = AudioDeviceEnumerator.Create();
            foreach (var d in enumerator.GetDevices(AudioDeviceKind.Output))
                AudioDevices.Add(d);

            SelectedAudioDevice = AudioDevices.FirstOrDefault(d => d.Id == PreferredAudioDeviceId)
                               ?? AudioDevices.FirstOrDefault(d => d.IsDefault)
                               ?? AudioDevices.FirstOrDefault();
        }
        catch { /* non-Windows build or no audio devices */ }
    }

    // ── Saved server management ────────────────────────────────────────────

    public void AddSavedServer(PersistedServer server)
    {
        SavedServers.Add(server);
        Save();
    }

    public void RemoveSavedServer(PersistedServer server)
    {
        SavedServers.Remove(server);
        Save();
    }

    /// <summary>
    /// Updates the cached <see cref="PersistedServer.ServerId"/> and
    /// <see cref="PersistedServer.ServerName"/> for the saved entry that matches
    /// this server. Matches by <paramref name="serverId"/> first (stable across IP
    /// changes), then falls back to host+port. No-ops if no saved entry matches.
    /// </summary>
    public void UpdateServerInfo(string? serverId, string? serverName, string host, int port)
    {
        if (string.IsNullOrEmpty(serverId)) return;

        var existing = SavedServers.FirstOrDefault(s => s.ServerId == serverId)
                    ?? SavedServers.FirstOrDefault(s => s.Host == host && s.Port == port);
        if (existing is null) return;

        var updated = existing with { ServerId = serverId, ServerName = serverName };
        if (updated == existing) return;

        var idx = SavedServers.IndexOf(existing);
        SavedServers[idx] = updated;
        Save();
    }
    // ── Helpers ────────────────────────────────────────────────────────────

    private DeviceSettings GetDeviceSettings(string deviceId)
        => _deviceSettings.TryGetValue(deviceId, out var ds) ? ds : new DeviceSettings();

    // ── Load / save ────────────────────────────────────────────────────────

    private void Load()
    {
        try
        {
            if (!File.Exists(SettingsPath)) return;

            var json  = File.ReadAllText(SettingsPath);
            var saved = JsonSerializer.Deserialize<SettingsData>(json, JsonOptions);
            if (saved is null) return;

            // Write directly to backing fields to avoid firing PropertyChanged
            // (and triggering Save) during initialisation.
            _clientName             = saved.ClientName;
            _clientId               = saved.ClientId;
            _preferredAudioDeviceId = saved.PreferredAudioDeviceId;
            _deviceSettings         = saved.DeviceSettings ?? new Dictionary<string, DeviceSettings>();
            _connectionMode         = saved.ConnectionMode;
            _logLevel               = saved.LogLevel;
            _termsAccepted          = saved.TermsAccepted;
            _telemetryConsent       = saved.TelemetryConsent;
            _windowX                = saved.WindowX;
            _windowY                = saved.WindowY;
            _logsWindowX            = saved.LogsWindowX;
            _logsWindowY            = saved.LogsWindowY;
            _logsWindowWidth        = saved.LogsWindowWidth;
            _logsWindowHeight       = saved.LogsWindowHeight;
            _statsWindowX           = saved.StatsWindowX;
            _statsWindowY           = saved.StatsWindowY;
            _statsWindowWidth       = saved.StatsWindowWidth;
            _statsWindowHeight      = saved.StatsWindowHeight;
            _volume                 = saved.Volume  ?? 0.8;
            _isMuted                = saved.IsMuted ?? false;
            _lastPlayedServerId     = saved.LastPlayedServerId;

            if (saved.SavedServers is { } servers)
            {
                foreach (var s in servers)
                    SavedServers.Add(s);
            }

            // Pre-load the preferred device's settings so they're ready before
            // the device ComboBox is populated.
            if (!string.IsNullOrEmpty(_preferredAudioDeviceId) &&
                _deviceSettings.TryGetValue(_preferredAudioDeviceId, out var ds))
            {
                _currentDeviceFormat        = ds.PreferredFormat;
                _currentDeviceStaticDelayMs = ds.StaticDelayMs;
            }
        }
        catch { /* first run or corrupted file — use defaults */ }
    }

    public void CaptureSnapshot()
    {
        _snapshot = new SettingsSnapshot(
            ClientName, ClientId, PreferredAudioDeviceId,
            _deviceSettings.ToDictionary(
                kvp => kvp.Key,
                kvp => new DeviceSettings { PreferredFormat = kvp.Value.PreferredFormat, StaticDelayMs = kvp.Value.StaticDelayMs }),
            ConnectionMode, LogLevel, TermsAccepted, TelemetryConsent);
    }

    public void RestoreSnapshot()
    {
        if (_snapshot is null) return;
        _suppressSave = true;
        try
        {
            ClientName             = _snapshot.ClientName;
            ClientId               = _snapshot.ClientId;
            PreferredAudioDeviceId = _snapshot.PreferredAudioDeviceId;
            _deviceSettings        = _snapshot.DeviceSettings.ToDictionary(
                kvp => kvp.Key,
                kvp => new DeviceSettings { PreferredFormat = kvp.Value.PreferredFormat, StaticDelayMs = kvp.Value.StaticDelayMs });
            ConnectionMode         = _snapshot.ConnectionMode;
            LogLevel               = _snapshot.LogLevel;
            TermsAccepted          = _snapshot.TermsAccepted;
            TelemetryConsent       = _snapshot.TelemetryConsent;

            // Reload current device's settings after restoring the dict
            SelectedAudioDevice = AudioDevices.FirstOrDefault(d => d.Id == PreferredAudioDeviceId)
                               ?? SelectedAudioDevice;
        }
        finally
        {
            _suppressSave = false;
        }
        Save();
    }

    public void CommitNow()
    {
        lock (_saveCtsLock)
        {
            _saveCts?.Cancel();
            _saveCts?.Dispose();
            _saveCts = null;
        }
        Save();
    }

    private void DebouncedSave()
    {
        if (_suppressSave) return;

        CancellationTokenSource cts;
        lock (_saveCtsLock)
        {
            _saveCts?.Cancel();
            _saveCts?.Dispose();
            _saveCts = cts = new CancellationTokenSource();
        }

        _ = Task.Delay(500, cts.Token).ContinueWith(
            _ => Save(),
            CancellationToken.None,
            TaskContinuationOptions.OnlyOnRanToCompletion,
            TaskScheduler.Default);
    }

    private void Save()
    {
        try
        {
            var dir = Path.GetDirectoryName(SettingsPath)!;
            Directory.CreateDirectory(dir);

            // Keep preferred device ID and its per-device settings in sync with
            // the live selection before serialising.
            if (SelectedAudioDevice is { } d)
            {
                _preferredAudioDeviceId = d.Id;
                _deviceSettings[d.Id] = new DeviceSettings
                {
                    PreferredFormat = CurrentDeviceFormat,
                    StaticDelayMs   = CurrentDeviceStaticDelayMs,
                };
            }

            var data = new SettingsData(
                ClientName,
                ClientId,
                _preferredAudioDeviceId,
                _deviceSettings,
                ConnectionMode,
                LogLevel,
                SavedServers.ToList(),
                _windowX,
                _windowY,
                TermsAccepted,
                TelemetryConsent,
                _volume,
                _isMuted,
                _logsWindowX,
                _logsWindowY,
                _logsWindowWidth,
                _logsWindowHeight,
                _lastPlayedServerId,
                _statsWindowX,
                _statsWindowY,
                _statsWindowWidth,
                _statsWindowHeight);

            var json    = JsonSerializer.Serialize(data, JsonOptions);
            var tmpPath = SettingsPath + ".tmp";

            // Write to a temp file then rename so the settings file is never
            // left in a partially-written state if the process is killed mid-write.
            File.WriteAllText(tmpPath, json);
            File.Move(tmpPath, SettingsPath, overwrite: true);
        }
        catch { /* best-effort; non-fatal */ }
    }

    // ── Settings data types (serialised to disk) ──────────────────────────

    public sealed class DeviceSettings
    {
        public AudioFormat PreferredFormat { get; set; } = AudioFormat.Flac;
        public int         StaticDelayMs   { get; set; } = 0;
    }

    private sealed record SettingsData(
        string                             ClientName,
        string                             ClientId,
        string                             PreferredAudioDeviceId,
        Dictionary<string, DeviceSettings> DeviceSettings,
        ConnectionMode                     ConnectionMode,
        string                             LogLevel,
        List<PersistedServer>?             SavedServers,
        int?                               WindowX           = null,
        int?                               WindowY           = null,
        bool                               TermsAccepted     = false,
        bool                               TelemetryConsent  = false,
        double?                            Volume            = null,
        bool?                              IsMuted           = null,
        int?                               LogsWindowX        = null,
        int?                               LogsWindowY        = null,
        int?                               LogsWindowWidth    = null,
        int?                               LogsWindowHeight   = null,
        string?                            LastPlayedServerId  = null,
        int?                               StatsWindowX       = null,
        int?                               StatsWindowY       = null,
        int?                               StatsWindowWidth   = null,
        int?                               StatsWindowHeight  = null);
}
