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

    // ── Persistent settings ────────────────────────────────────────────────

    [ObservableProperty] private string _clientName = Environment.MachineName;
    [ObservableProperty] private string _clientId   = Guid.NewGuid().ToString("N");

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(AudioFormatIndex))]
    private AudioFormat _preferredFormat = AudioFormat.Opus;

    [ObservableProperty] private string _preferredAudioDeviceId = string.Empty;
    [ObservableProperty] private int    _staticDelayMs          = 0;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ConnectionModeIndex))]
    private ConnectionMode _connectionMode = ConnectionMode.ServerInitiated;

    [ObservableProperty] private string _logLevel = "Information";

    // ── Non-persisted: populated at runtime ───────────────────────────────

    [ObservableProperty] private AudioDeviceInfo? _selectedAudioDevice;

    public ObservableCollection<AudioDeviceInfo> AudioDevices { get; } = new();

    // ── Static option lists ────────────────────────────────────────────────

    public IReadOnlyList<string> LogLevelOptions { get; } =
        ["Verbose", "Debug", "Information", "Warning", "Error"];

    // ── Index helpers (for RadioButtons SelectedIndex binding) ─────────────

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

    public int ConnectionModeIndex
    {
        get => _connectionMode == ConnectionMode.ServerInitiated ? 0 : 1;
        set => ConnectionMode  = value == 0
            ? ConnectionMode.ServerInitiated
            : ConnectionMode.ClientInitiated;
    }

    // ── Constructor ────────────────────────────────────────────────────────

    public SettingsViewModel()
    {
        Load();

        // Auto-save on every property change that originates after construction
        PropertyChanged += (_, _) => Save();

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
        }
        catch { /* first run or corrupted file — use defaults */ }
    }

    private void Save()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(SettingsPath)!);

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
                LogLevel);

            File.WriteAllText(SettingsPath, JsonSerializer.Serialize(data, JsonOptions));
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
        string         LogLevel);
}
