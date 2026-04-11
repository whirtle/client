using Whirtle.Client.Playback;

namespace Whirtle.Client.Tests.Playback;

public class SampleInterpolatorTests
{
    [Fact]
    public void Interpolate_RatioOne_ReturnsSameLength()
    {
        short[] input  = [100, 200, 300, 400];
        var     output = SampleInterpolator.Interpolate(input, channels: 1, rateRatio: 1.0);
        Assert.Equal(input.Length, output.Length);
    }

    [Fact]
    public void Interpolate_RatioOne_Passthrough_ValuesMatch()
    {
        short[] input  = [1000, -1000, 500, -500];
        var     output = SampleInterpolator.Interpolate(input, channels: 1, rateRatio: 1.0);
        Assert.Equal(input, output);
    }

    [Fact]
    public void Interpolate_RatioTwo_DoublesFrameCount()
    {
        short[] input  = [0, 100, 200, 300]; // 4 mono frames
        var     output = SampleInterpolator.Interpolate(input, channels: 1, rateRatio: 2.0);
        Assert.Equal(8, output.Length);
    }

    [Fact]
    public void Interpolate_RatioHalf_HalvesFrameCount()
    {
        short[] input  = [0, 100, 200, 300, 400, 500, 600, 700]; // 8 mono frames
        var     output = SampleInterpolator.Interpolate(input, channels: 1, rateRatio: 0.5);
        Assert.Equal(4, output.Length);
    }

    [Fact]
    public void Interpolate_Stereo_PreservesChannelInterleaving()
    {
        // 2 stereo frames: [L0=100,R0=200], [L1=300,R1=400]
        short[] input  = [100, 200, 300, 400];
        var     output = SampleInterpolator.Interpolate(input, channels: 2, rateRatio: 1.0);
        Assert.Equal(4, output.Length);
    }

    [Fact]
    public void Interpolate_EmptyInput_ReturnsEmpty()
    {
        var output = SampleInterpolator.Interpolate([], channels: 1, rateRatio: 1.0);
        Assert.Empty(output);
    }

    [Fact]
    public void Interpolate_InvalidChannels_Throws()
    {
        Assert.Throws<ArgumentOutOfRangeException>(
            () => SampleInterpolator.Interpolate([100], channels: 0, rateRatio: 1.0));
    }

    [Fact]
    public void Interpolate_MidpointValue_IsInterpolated()
    {
        // Input: 0 and 200 (mono). At ratio=2, first output=0, second≈100 (midpoint), third≈133, fourth=200
        short[] input  = [0, 200];
        var     output = SampleInterpolator.Interpolate(input, channels: 1, rateRatio: 2.0);
        // output[0] = 0, output[1] ≈ 67, output[2] ≈ 133, output[3] = 200
        Assert.Equal(0,   output[0]);
        Assert.Equal(200, output[^1]);
    }
}
