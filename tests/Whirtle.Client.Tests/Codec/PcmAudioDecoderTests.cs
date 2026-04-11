using System.Runtime.InteropServices;
using Whirtle.Client.Codec;

namespace Whirtle.Client.Tests.Codec;

public class PcmAudioDecoderTests
{
    [Fact]
    public void Decode_TwoBytes_ReturnsSingleSample()
    {
        var decoder = new PcmAudioDecoder(48_000, 1);
        // 0x0100 little-endian = 256
        var frame = decoder.Decode(new byte[] { 0x00, 0x01 });

        Assert.Single(frame.Samples);
        Assert.Equal(256, frame.Samples[0]);
    }

    [Fact]
    public void Decode_PreservesAllSamples()
    {
        var decoder  = new PcmAudioDecoder(48_000, 2);
        short[] orig = [100, -200, 300, -400];
        var bytes    = new byte[orig.Length * 2];
        MemoryMarshal.Cast<short, byte>(orig).CopyTo(bytes);

        var frame = decoder.Decode(bytes);

        Assert.Equal(orig, frame.Samples);
    }

    [Fact]
    public void Decode_ReturnsCorrectSampleRate()
    {
        var frame = new PcmAudioDecoder(44_100, 2).Decode(new byte[4]);
        Assert.Equal(44_100, frame.SampleRate);
    }

    [Fact]
    public void Decode_ReturnsCorrectChannelCount()
    {
        var frame = new PcmAudioDecoder(48_000, 1).Decode(new byte[2]);
        Assert.Equal(1, frame.Channels);
    }

    [Fact]
    public void Decode_OddLengthData_Throws()
    {
        Assert.Throws<ArgumentException>(
            () => new PcmAudioDecoder().Decode(new byte[3]));
    }

    [Fact]
    public void Decode_EmptyData_ReturnsEmptyFrame()
    {
        var frame = new PcmAudioDecoder().Decode(ReadOnlyMemory<byte>.Empty);
        Assert.Empty(frame.Samples);
    }

    [Fact]
    public void AudioFrame_SamplesPerChannel_IsCorrect()
    {
        var frame = new PcmAudioDecoder(48_000, 2).Decode(new byte[8]); // 4 shorts, 2 channels
        Assert.Equal(2, frame.SamplesPerChannel);
    }

    [Fact]
    public void AudioFrame_Duration_IsCorrect()
    {
        // 48000 samples/sec, 1 channel, 480 samples = 10 ms
        short[] samples = new short[480];
        var frame = new AudioFrame(samples, 48_000, 1);
        Assert.Equal(TimeSpan.FromMilliseconds(10), frame.Duration);
    }

    [Fact]
    public void Constructor_InvalidSampleRate_Throws()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new PcmAudioDecoder(0));
    }

    [Fact]
    public void Constructor_InvalidChannels_Throws()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new PcmAudioDecoder(48_000, 0));
    }
}
