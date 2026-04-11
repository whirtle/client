using Whirtle.Client.Audio;

namespace Whirtle.Client.Tests.Audio;

internal sealed class FakeAudioDeviceEnumerator : IAudioDeviceEnumerator
{
    private readonly IReadOnlyList<AudioDeviceInfo> _devices;

    public FakeAudioDeviceEnumerator(IEnumerable<AudioDeviceInfo> devices)
        => _devices = devices.ToList();

    public IReadOnlyList<AudioDeviceInfo> GetDevices() => _devices;
}
