using Whirtle.Client.Audio;

namespace Whirtle.Client.Tests.Audio;

public class AudioDeviceEnumeratorTests
{
    private static IAudioDeviceEnumerator BuildFake() => new FakeAudioDeviceEnumerator(
    [
        new AudioDeviceInfo("in-1",  "Built-in Microphone", AudioDeviceKind.Input,  IsDefault: true),
        new AudioDeviceInfo("in-2",  "USB Microphone",      AudioDeviceKind.Input,  IsDefault: false),
        new AudioDeviceInfo("out-1", "Built-in Speakers",   AudioDeviceKind.Output, IsDefault: true),
        new AudioDeviceInfo("out-2", "HDMI Audio",          AudioDeviceKind.Output, IsDefault: false),
    ]);

    [Fact]
    public void GetDevices_ReturnsAllDevices()
    {
        Assert.Equal(4, BuildFake().GetDevices().Count);
    }

    [Fact]
    public void GetDevices_FilterByInput_ReturnsOnlyInputs()
    {
        var inputs = BuildFake().GetDevices(AudioDeviceKind.Input);

        Assert.Equal(2, inputs.Count);
        Assert.All(inputs, d => Assert.Equal(AudioDeviceKind.Input, d.Kind));
    }

    [Fact]
    public void GetDevices_FilterByOutput_ReturnsOnlyOutputs()
    {
        var outputs = BuildFake().GetDevices(AudioDeviceKind.Output);

        Assert.Equal(2, outputs.Count);
        Assert.All(outputs, d => Assert.Equal(AudioDeviceKind.Output, d.Kind));
    }

    [Fact]
    public void GetDefault_Input_ReturnsDefaultFlaggedDevice()
    {
        var def = BuildFake().GetDefault(AudioDeviceKind.Input);

        Assert.NotNull(def);
        Assert.Equal("in-1", def!.Id);
        Assert.True(def.IsDefault);
    }

    [Fact]
    public void GetDefault_Output_ReturnsDefaultFlaggedDevice()
    {
        var def = BuildFake().GetDefault(AudioDeviceKind.Output);

        Assert.NotNull(def);
        Assert.Equal("out-1", def!.Id);
    }

    [Fact]
    public void GetDefault_NoDefaultFlagged_ReturnsFallbackFirst()
    {
        IAudioDeviceEnumerator enumerator = new FakeAudioDeviceEnumerator(
        [
            new AudioDeviceInfo("in-1", "Mic A", AudioDeviceKind.Input, IsDefault: false),
            new AudioDeviceInfo("in-2", "Mic B", AudioDeviceKind.Input, IsDefault: false),
        ]);

        var def = enumerator.GetDefault(AudioDeviceKind.Input);

        Assert.NotNull(def);
        Assert.Equal("in-1", def!.Id); // first one returned as fallback
    }

    [Fact]
    public void GetDefault_NoDevices_ReturnsNull()
    {
        var def = ((IAudioDeviceEnumerator)new FakeAudioDeviceEnumerator([])).GetDefault(AudioDeviceKind.Input);
        Assert.Null(def);
    }

    [Fact]
    public void NullEnumerator_GetDevices_ReturnsEmpty()
    {
        Assert.Empty(((IAudioDeviceEnumerator)new NullAudioDeviceEnumerator()).GetDevices());
    }

    [Fact]
    public void NullEnumerator_GetDefault_ReturnsNull()
    {
        Assert.Null(((IAudioDeviceEnumerator)new NullAudioDeviceEnumerator()).GetDefault(AudioDeviceKind.Input));
    }

    [Fact]
    public void AudioDeviceInfo_RecordEquality()
    {
        var a = new AudioDeviceInfo("id", "Name", AudioDeviceKind.Input, IsDefault: true);
        var b = new AudioDeviceInfo("id", "Name", AudioDeviceKind.Input, IsDefault: true);
        Assert.Equal(a, b);
    }
}
