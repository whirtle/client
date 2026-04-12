using System.Collections.ObjectModel;
using System.Text.Json;
using System.Text.Json.Serialization;
using CommunityToolkit.Mvvm.ComponentModel;
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

    // Snapshot for OK/Cancel support
    private record SettingsSnapshot(
        string ClientName, string ClientId, string PreferredAudioDeviceId,
        Dictionary<string, DeviceSettings> DeviceSettings,
        ConnectionMode ConnectionMode, string LogLevel);

    private SettingsSnapshot? _snapshot;
    private bool _suppressSave;

    // Per-device settings storage
    private Dictionary<string, DeviceSettings> _deviceSettings = new();

    // ── Persistent settings ────────────────────────────────────────────────

    [ObservableProperty] private string _clientName = Environment.MachineName;
    [ObservableProperty] private string _clientId   = Guid.NewGuid().ToString("N");

    [ObservableProperty] private string _preferredAudioDeviceId = string.Empty;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(AudioFormatIndex))]
    private AudioFormat _currentDeviceFormat = AudioFormat.Opus;

    [ObservableProperty] private int _currentDeviceStaticDelayMs = 0;

    partial void OnCurrentDeviceStaticDelayMsChanged(int value)
    {
        if (value < 0) CurrentDeviceStaticDelayMs = 0;
    }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ConnectionModeIndex))]
    private ConnectionMode _connectionMode = ConnectionMode.ServerInitiated;

    [ObservableProperty] private string _logLevel = "Information";

    // ── Non-persisted: populated at runtime ───────────────────────────────

    [ObservableProperty] private AudioDeviceInfo? _selectedAudioDevice;

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

    public ObservableCollection<AudioDeviceInfo> AudioDevices { get; } = new();

    // ── Static option lists ────────────────────────────────────────────────

    public IReadOnlyList<string> AudioFormatOptions { get; } =
        ["Opus (recommended)", "FLAC (lossless)", "PCM (uncompressed)"];

    public IReadOnlyList<string> LogLevelOptions { get; } =
        ["Verbose", "Debug", "Information", "Warning", "Error"];

    // ── Index helpers (for ComboBox SelectedIndex binding) ─────────────────

    public int AudioFormatIndex
    {
        get => _currentDeviceFormat switch
        {
            AudioFormat.Opus => 0,
            AudioFormat.Flac => 1,
            _                => 2,
        };
        set => CurrentDeviceFormat = value switch
        {
            0 => AudioFormat.Opus,
            1 => AudioFormat.Flac,
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
            ConnectionMode, LogLevel);
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
                LogLevel);

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
        public AudioFormat PreferredFormat { get; set; } = AudioFormat.Opus;
        public int         StaticDelayMs   { get; set; } = 0;
    }

    private sealed record SettingsData(
        string                             ClientName,
        string                             ClientId,
        string                             PreferredAudioDeviceId,
        Dictionary<string, DeviceSettings> DeviceSettings,
        ConnectionMode                     ConnectionMode,
        string                             LogLevel);
}
