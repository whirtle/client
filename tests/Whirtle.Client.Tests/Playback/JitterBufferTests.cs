using Whirtle.Client.Codec;
using Whirtle.Client.Playback;

namespace Whirtle.Client.Tests.Playback;

public class JitterBufferTests
{
    private static AudioFrame Frame() => new(new short[2], 48_000, 1);

    [Fact]
    public void Enqueue_ThenDequeue_ReturnsFrameInOrder()
    {
        var buf = new JitterBuffer();
        buf.Enqueue(10, Frame());
        buf.Enqueue(5, Frame());

        buf.TryDequeue(out long ts1, out _);
        buf.TryDequeue(out long ts2, out _);

        Assert.Equal(5, ts1);
        Assert.Equal(10, ts2);
    }

    [Fact]
    public void TryDequeue_Empty_ReturnsFalse()
    {
        Assert.False(new JitterBuffer().TryDequeue(out _, out _));
    }

    [Fact]
    public void Enqueue_LateFrame_IsDropped()
    {
        var buf = new JitterBuffer();
        buf.Enqueue(10, Frame());
        buf.TryDequeue(out _, out _); // advances cursor past 10

        buf.Enqueue(5, Frame()); // late — before cursor

        Assert.Equal(0, buf.Count);
    }

    [Fact]
    public void Enqueue_AtCapacity_EvictsOldest()
    {
        var buf = new JitterBuffer(capacity: 2);
        buf.Enqueue(1, Frame());
        buf.Enqueue(2, Frame());
        buf.Enqueue(3, Frame()); // evicts timestamp 1

        buf.TryDequeue(out long ts, out _);
        Assert.Equal(2, ts); // oldest surviving
    }

    [Fact]
    public void Clear_RemovesAllFrames()
    {
        var buf = new JitterBuffer();
        buf.Enqueue(1, Frame());
        buf.Enqueue(2, Frame());
        buf.Clear();

        Assert.Equal(0, buf.Count);
    }

    [Fact]
    public void Count_ReflectsEnqueuedFrames()
    {
        var buf = new JitterBuffer();
        buf.Enqueue(1, Frame());
        buf.Enqueue(2, Frame());
        Assert.Equal(2, buf.Count);
    }

    [Fact]
    public void Constructor_ZeroCapacity_Throws()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new JitterBuffer(0));
    }
}
