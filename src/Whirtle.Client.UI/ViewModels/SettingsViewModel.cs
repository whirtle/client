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

    // Snapshot for OK/Cancel support (connection mode excluded: it changes via the
    // server picker, outside the Settings dialog lifecycle).
    private record SettingsSnapshot(
        string ClientName, string ClientId, string PreferredAudioDeviceId,
        AudioFormat PreferredFormat, int StaticDelayMs, string LogLevel);

    private SettingsSnapshot? _snapshot;
    private bool _suppressSave;

    // ── Persistent settings ────────────────────────────────────────────────

    [ObservableProperty] private string _clientName = Environment.MachineName;
    [ObservableProperty] private string _clientId   = Guid.NewGuid().ToString("N");

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(AudioFormatIndex))]
    private AudioFormat _preferredFormat = AudioFormat.Opus;

    [ObservableProperty] private string _preferredAudioDeviceId = string.Empty;
    [ObservableProperty] private int    _staticDelayMs          = 0;

    [ObservableProperty] private ConnectionMode _connectionMode = ConnectionMode.ServerInitiated;

    [ObservableProperty] private string _logLevel = "Information";

    // ── Non-persisted: populated at runtime ───────────────────────────────

    [ObservableProperty] private AudioDeviceInfo? _selectedAudioDevice;

    public ObservableCollection<AudioDeviceInfo>  AudioDevices  { get; } = new();
    public ObservableCollection<PersistedServer>  SavedServers  { get; } = new();

    // ── Static option lists ────────────────────────────────────────────────

    public IReadOnlyList<string> AudioFormatOptions { get; } =
        ["Opus (recommended)", "FLAC (lossless)", "PCM (uncompressed)"];

    public IReadOnlyList<string> LogLevelOptions { get; } =
        ["Verbose", "Debug", "Information", "Warning", "Error"];

    // ── Index helpers ──────────────────────────────────────────────────────

    public int AudioFormatIndex
    {
        get => _preferredFormat switch
        {
            AudioFormat.Opus => 0,
            AudioFormat.Flac => 1,
            _                => 2,
        };
        set => PreferredFormat = value switch
        {
            0 => AudioFormat.Opus,
            1 => AudioFormat.Flac,
            _ => AudioFormat.Pcm,
        };
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

    // ── Load / save ────────────────────────────────────────────────────────

    private void Load()
    {
        try
        {
            if (!File.Exists(SettingsPath)) return;

            var json     = File.ReadAllText(SettingsPath);
            var saved    = JsonSerializer.Deserialize<SettingsData>(json, JsonOptions);
            if (saved is null) return;

            // Write directly to backing fields to avoid firing PropertyChanged
            // (and triggering Save) during initialisation.
            _clientName             = saved.ClientName;
            _clientId               = saved.ClientId;
            _preferredAudioDeviceId = saved.PreferredAudioDeviceId;
            _preferredFormat        = saved.PreferredFormat;
            _staticDelayMs          = saved.StaticDelayMs;
            _connectionMode         = saved.ConnectionMode;
            _logLevel               = saved.LogLevel;

            if (saved.SavedServers is { } servers)
            {
                foreach (var s in servers)
                    SavedServers.Add(s);
            }
        }
        catch { /* first run or corrupted file — use defaults */ }
    }

    public void CaptureSnapshot()
    {
        _snapshot = new SettingsSnapshot(
            ClientName, ClientId, PreferredAudioDeviceId,
            PreferredFormat, StaticDelayMs, LogLevel);
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
            PreferredFormat        = _snapshot.PreferredFormat;
            StaticDelayMs          = _snapshot.StaticDelayMs;
            LogLevel               = _snapshot.LogLevel;
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

            // Keep preferred device ID in sync with the live selection
            if (SelectedAudioDevice is { } d)
                _preferredAudioDeviceId = d.Id;

            var data = new SettingsData(
                ClientName,
                ClientId,
                _preferredAudioDeviceId,
                PreferredFormat,
                StaticDelayMs,
                ConnectionMode,
                LogLevel,
                SavedServers.ToList());

            var json    = JsonSerializer.Serialize(data, JsonOptions);
            var tmpPath = SettingsPath + ".tmp";

            // Write to a temp file then rename so the settings file is never
            // left in a partially-written state if the process is killed mid-write.
            File.WriteAllText(tmpPath, json);
            File.Move(tmpPath, SettingsPath, overwrite: true);
        }
        catch { /* best-effort; non-fatal */ }
    }

    // ── Settings data record (serialised to disk) ─────────────────────────

    private sealed record SettingsData(
        string         ClientName,
        string         ClientId,
        string         PreferredAudioDeviceId,
        AudioFormat    PreferredFormat,
        int            StaticDelayMs,
        ConnectionMode ConnectionMode,
        string         LogLevel,
        IReadOnlyList<PersistedServer>? SavedServers = null);
}
