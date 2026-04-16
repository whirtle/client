using System.Collections.Concurrent;
using System.Threading;
using Whirtle.Client.Codec;
using Whirtle.Client.Playback;

namespace Whirtle.Client.Tests.Playback;

public class JitterBufferTests
{
    private static AudioFrame Frame() => new(new short[2], 48_000, 1);

    /// <summary>Creates a frame whose duration is exactly <paramref name="ms"/> milliseconds.</summary>
    private static AudioFrame FrameMs(int ms) => new(new short[48 * ms], 48_000, 1);

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

    // ── TotalDuration ─────────────────────────────────────────────────────────

    [Fact]
    public void TotalDuration_StartsAtZero()
    {
        Assert.Equal(TimeSpan.Zero, new JitterBuffer().TotalDuration);
    }

    [Fact]
    public void TotalDuration_AccumulatesOnEnqueue()
    {
        var buf = new JitterBuffer();
        buf.Enqueue(1, FrameMs(20));
        buf.Enqueue(2, FrameMs(20));

        Assert.Equal(TimeSpan.FromMilliseconds(40), buf.TotalDuration);
    }

    [Fact]
    public void TotalDuration_DecreasesOnDequeue()
    {
        var buf = new JitterBuffer();
        buf.Enqueue(1, FrameMs(20));
        buf.Enqueue(2, FrameMs(20));
        buf.TryDequeue(out _, out _);

        Assert.Equal(TimeSpan.FromMilliseconds(20), buf.TotalDuration);
    }

    [Fact]
    public void TotalDuration_ZeroAfterClear()
    {
        var buf = new JitterBuffer();
        buf.Enqueue(1, FrameMs(20));
        buf.Enqueue(2, FrameMs(20));
        buf.Clear();

        Assert.Equal(TimeSpan.Zero, buf.TotalDuration);
    }

    [Fact]
    public void TotalDuration_LateFrameNotCounted()
    {
        var buf = new JitterBuffer();
        buf.Enqueue(10, FrameMs(20));
        buf.TryDequeue(out _, out _); // cursor advances past 10

        buf.Enqueue(5, FrameMs(20)); // late — dropped

        Assert.Equal(TimeSpan.Zero, buf.TotalDuration);
    }

    [Fact]
    public void TotalDuration_EvictedFrameNotCounted()
    {
        var buf = new JitterBuffer(capacity: 2);
        buf.Enqueue(1, FrameMs(20));
        buf.Enqueue(2, FrameMs(20));
        buf.Enqueue(3, FrameMs(20)); // evicts timestamp 1

        Assert.Equal(TimeSpan.FromMilliseconds(40), buf.TotalDuration);
    }

    // ── Concurrent-access stress ───────────────────────────────────────────────

    [Fact]
    public async Task ConcurrentEnqueueAndDequeue_DoesNotThrow()
    {
        var buf        = new JitterBuffer(capacity: 128);
        var exceptions = new System.Collections.Concurrent.ConcurrentBag<Exception>();

        // 4 producers writing non-overlapping timestamp ranges
        var producers = Enumerable.Range(0, 4).Select(i => Task.Run(() =>
        {
            for (int j = 0; j < 200; j++)
            {
                try   { buf.Enqueue(i * 1000L + j, Frame()); }
                catch (Exception ex) { exceptions.Add(ex); }
            }
        }));

        // 2 consumers draining concurrently
        var consumers = Enumerable.Range(0, 2).Select(_ => Task.Run(() =>
        {
            for (int j = 0; j < 400; j++)
            {
                try   { buf.TryDequeue(out long _, out AudioFrame? _); }
                catch (Exception ex) { exceptions.Add(ex); }
            }
        }));

        await Task.WhenAll(producers.Concat(consumers));

        Assert.Empty(exceptions);
    }

    [Fact]
    public async Task ConcurrentClearAndEnqueue_DoesNotThrow()
    {
        var buf        = new JitterBuffer(capacity: 32);
        var exceptions = new System.Collections.Concurrent.ConcurrentBag<Exception>();

        var enqueuer = Task.Run(() =>
        {
            for (int i = 0; i < 1_000; i++)
            {
                try   { buf.Enqueue(i, Frame()); }
                catch (Exception ex) { exceptions.Add(ex); }
            }
        });

        var clearer = Task.Run(() =>
        {
            for (int i = 0; i < 100; i++)
            {
                try   { buf.Clear(); }
                catch (Exception ex) { exceptions.Add(ex); }
                Thread.SpinWait(100);
            }
        });

        await Task.WhenAll(enqueuer, clearer);

        Assert.Empty(exceptions);
    }

    [Fact]
    public async Task Count_RemainsWithinCapacity_UnderConcurrentLoad()
    {
        const int capacity = 16;
        var buf = new JitterBuffer(capacity);

        var writer = Task.Run(() =>
        {
            for (int i = 0; i < 500; i++)
                buf.Enqueue(i, Frame());
        });

        var reader = Task.Run(() =>
        {
            for (int i = 0; i < 500; i++)
                buf.TryDequeue(out long _, out AudioFrame? _);
        });

        await Task.WhenAll(writer, reader);

        Assert.InRange(buf.Count, 0, capacity);
    }
}
