using Whirtle.Client.Codec;

namespace Whirtle.Client.Tests.Codec;

/// <summary>
/// Verifies the factory and the <see cref="IAudioDecoder"/> contract
/// using a fake decoder, keeping tests free of codec-library dependencies.
/// </summary>
public class AudioDecoderContractTests
{
    private sealed class FakeDecoder(AudioFormat format, int sampleRate, int channels)
        : IAudioDecoder
    {
        public AudioFormat Format     => format;
        public int         SampleRate => sampleRate;
        public int         Channels   => channels;

        public AudioFrame Decode(ReadOnlyMemory<byte> data) =>
            new(new short[data.Length / 2], SampleRate, Channels);

        public void Dispose() { }
    }

    [Theory]
    [InlineData(AudioFormat.Pcm)]
    [InlineData(AudioFormat.Opus)]
    [InlineData(AudioFormat.Flac)]
    public void Factory_Create_ReturnsCorrectFormat(AudioFormat format)
    {
        using var decoder = AudioDecoderFactory.Create(format);
        Assert.Equal(format, decoder.Format);
    }

    [Fact]
    public void Factory_Create_UnknownFormat_Throws()
    {
        Assert.Throws<ArgumentOutOfRangeException>(
            () => AudioDecoderFactory.Create((AudioFormat)99));
    }

    [Fact]
    public void FakeDecoder_Decode_ReturnsSamplesMatchingByteLength()
    {
        IAudioDecoder decoder = new FakeDecoder(AudioFormat.Pcm, 48_000, 2);
        var frame = decoder.Decode(new byte[20]);
        Assert.Equal(10, frame.Samples.Length);
    }

    [Fact]
    public void FlacDecoder_Decode_ThrowsNotSupported()
    {
        using var decoder = new FlacAudioDecoder();
        Assert.Throws<NotSupportedException>(() => decoder.Decode(new byte[4]));
    }

    [Fact]
    public void PcmDecoder_Format_IsCorrect()
    {
        using var d = AudioDecoderFactory.Create(AudioFormat.Pcm);
        Assert.Equal(AudioFormat.Pcm, d.Format);
    }

    [Fact]
    public void OpusDecoder_Format_IsCorrect()
    {
        using var d = AudioDecoderFactory.Create(AudioFormat.Opus);
        Assert.Equal(AudioFormat.Opus, d.Format);
    }
}
