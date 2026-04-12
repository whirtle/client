using Whirtle.Client.Playback;

namespace Whirtle.Client.Tests.Playback;

public class ChannelDownmixerTests
{
    // ── Passthrough / simple cases ────────────────────────────────────────────

    [Fact]
    public void Stereo_PassedThrough_Unchanged()
    {
        short[] input  = [100, 200, 300, 400];
        var     output = ChannelDownmixer.Downmix(input, sourceChannels: 2);
        Assert.Equal(input, output);
    }

    [Fact]
    public void Mono_DuplicatedToBothChannels()
    {
        short[] input  = [1000, 2000];
        var     output = ChannelDownmixer.Downmix(input, sourceChannels: 1);
        Assert.Equal([1000, 1000, 2000, 2000], output);
    }

    // ── Output length ─────────────────────────────────────────────────────────

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    [InlineData(5)]
    [InlineData(6)]
    [InlineData(7)]
    [InlineData(8)]
    public void OutputLength_IsAlwaysTwoChannels(int sourceChannels)
    {
        int frames = 10;
        var input  = new short[frames * sourceChannels];
        var output = ChannelDownmixer.Downmix(input, sourceChannels);
        Assert.Equal(frames * 2, output.Length);
    }

    // ── 5-channel mix (the motivating case) ───────────────────────────────────

    [Fact]
    public void FiveChannel_CenterMixedToBothOutputs()
    {
        // Layout: L, C, R, Ls, Rs — one frame of all zeros except center
        short[] input  = [0, 10000, 0, 0, 0];  // only center active
        var     output = ChannelDownmixer.Downmix(input, sourceChannels: 5);

        // Center is mixed at -3 dB (×0.7071) into both L and R
        short expected = (short)Math.Round(10000 * 0.7071);
        Assert.Equal(expected, output[0]);  // left out
        Assert.Equal(expected, output[1]);  // right out
    }

    [Fact]
    public void FiveChannel_SurroundMixedIntoBothOutputs()
    {
        // Only left-surround active
        short[] input  = [0, 0, 0, 8000, 0];  // Ls = 8000
        var     output = ChannelDownmixer.Downmix(input, sourceChannels: 5);

        short expectedL = (short)Math.Round(8000 * 0.7071);
        Assert.Equal(expectedL, output[0]);  // Ls feeds left
        Assert.Equal((short)0,  output[1]);  // nothing feeds right
    }

    [Fact]
    public void FiveChannel_DirectChannelsPassThrough()
    {
        // Only L and R active, all others zero
        short[] input  = [1000, 0, 2000, 0, 0];
        var     output = ChannelDownmixer.Downmix(input, sourceChannels: 5);

        Assert.Equal((short)1000, output[0]);
        Assert.Equal((short)2000, output[1]);
    }

    // ── 6.1 (7-channel) ───────────────────────────────────────────────────────

    [Fact]
    public void SevenChannel_CsSplitsEquallyBetweenOutputs()
    {
        // Layout: L C R Ls Rs LFE Cs — only Cs active
        short[] input  = [0, 0, 0, 0, 0, 0, 10000];
        var     output = ChannelDownmixer.Downmix(input, sourceChannels: 7);

        // Cs is split at Surround3dB * 0.5 into both outputs
        short expected = (short)Math.Round(10000 * 0.7071 * 0.5);
        Assert.Equal(expected, output[0]);
        Assert.Equal(expected, output[1]);
    }

    [Fact]
    public void SevenChannel_LfeIsDropped()
    {
        // Only LFE active — should produce silence
        short[] input  = [0, 0, 0, 0, 0, 30000, 0];
        var     output = ChannelDownmixer.Downmix(input, sourceChannels: 7);

        Assert.Equal((short)0, output[0]);
        Assert.Equal((short)0, output[1]);
    }

    // ── 7.1 (8-channel) ───────────────────────────────────────────────────────

    [Fact]
    public void EightChannel_BackSurroundsContributeToCorrectSide()
    {
        // Layout: L C R Ls Rs LFE Lb Rb — only Lb active
        short[] input  = [0, 0, 0, 0, 0, 0, 8000, 0];
        var     output = ChannelDownmixer.Downmix(input, sourceChannels: 8);

        short expectedL = (short)Math.Round(8000 * 0.7071);
        Assert.Equal(expectedL, output[0]);  // Lb feeds left
        Assert.Equal((short)0,  output[1]);  // nothing feeds right
    }

    [Fact]
    public void EightChannel_LfeIsDropped()
    {
        // Only LFE active — should produce silence
        short[] input  = [0, 0, 0, 0, 0, 30000, 0, 0];
        var     output = ChannelDownmixer.Downmix(input, sourceChannels: 8);

        Assert.Equal((short)0, output[0]);
        Assert.Equal((short)0, output[1]);
    }

    [Fact]
    public void EightChannel_AllSurroundsStack_ClampedCorrectly()
    {
        // All surround channels at max on left side — must not overflow
        short max      = short.MaxValue;
        short[] input  = [max, 0, 0, max, 0, 0, max, 0];
        var     output = ChannelDownmixer.Downmix(input, sourceChannels: 8);

        Assert.Equal(short.MaxValue, output[0]);
        Assert.Equal((short)0,       output[1]);
    }

    // ── Clipping guard ────────────────────────────────────────────────────────

    [Fact]
    public void Mix_DoesNotOverflowShort()
    {
        // All channels at max — mixing should clamp to short.MaxValue
        short max     = short.MaxValue;
        short[] input = [max, max, max, max, max];  // 5ch
        var output    = ChannelDownmixer.Downmix(input, sourceChannels: 5);

        Assert.True(output[0] <= short.MaxValue);
        Assert.True(output[1] <= short.MaxValue);
    }
}
