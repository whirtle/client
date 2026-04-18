// Copyright (c) 2026 Steve Peterson
// SPDX-License-Identifier: MIT

using Whirtle.Client.Codec;

namespace Whirtle.Client.Playback;

/// <summary>
/// A bounded reorder buffer keyed by server-assigned timestamps.
/// Frames are inserted out-of-order and retrieved in monotonically
/// increasing timestamp order.
///
/// Thread-safety: all public members are guarded by an internal lock
/// and may be called from any thread.
/// </summary>
internal sealed class JitterBuffer
{
    private readonly SortedList<long, AudioFrame> _frames = new();
    private readonly int _capacity;
    private long _nextExpected = long.MinValue;
    private TimeSpan _totalDuration = TimeSpan.Zero;

    /// <param name="capacity">Maximum number of frames held before older frames are evicted.</param>
    public JitterBuffer(int capacity = 64)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(capacity, 1);
        _capacity = capacity;
    }

    /// <summary>Number of frames currently buffered.</summary>
    public int Count { get { lock (_frames) return _frames.Count; } }

    /// <summary>Total audio duration of all frames currently buffered.</summary>
    public TimeSpan TotalDuration { get { lock (_frames) return _totalDuration; } }

    /// <summary>
    /// Inserts a frame. If the buffer is full the oldest frame is evicted
    /// to make room. Late frames (timestamp before the last dequeued
    /// timestamp) are silently dropped.
    /// </summary>
    public void Enqueue(long timestamp, AudioFrame frame)
    {
        lock (_frames)
        {
            if (timestamp < _nextExpected)
                return; // late arrival — discard

            if (_frames.Count >= _capacity)
            {
                _totalDuration -= _frames.Values[0].Duration;
                _frames.RemoveAt(0); // evict oldest
            }

            _frames[timestamp] = frame;
            _totalDuration += frame.Duration;
        }
    }

    /// <summary>
    /// Tries to dequeue the frame with the lowest timestamp.
    /// Updates the internal cursor so equal/older frames are dropped on
    /// future <see cref="Enqueue"/> calls.
    /// </summary>
    /// <remarks>
    /// After dequeuing a frame at timestamp <c>T</c> the cursor is set to
    /// <c>T + 1</c>, meaning any subsequently enqueued frame with timestamp
    /// ≤ <c>T</c> is treated as a duplicate or late arrival and silently dropped.
    /// Frames whose timestamps skip values (e.g. <c>T</c>, <c>T+5</c>, <c>T+10</c>)
    /// are handled correctly — the gap is simply not present in the sorted list and
    /// no phantom frames are inserted.
    /// </remarks>
    public bool TryDequeue(out long timestamp, out AudioFrame? frame)
    {
        lock (_frames)
        {
            if (_frames.Count == 0)
            {
                timestamp = 0;
                frame     = null;
                return false;
            }

            timestamp      = _frames.Keys[0];
            frame          = _frames.Values[0];
            _nextExpected  = timestamp + 1; // drop duplicates and late arrivals
            _totalDuration -= frame.Duration;
            _frames.RemoveAt(0);
            return true;
        }
    }

    /// <summary>
    /// Returns the timestamp of the oldest buffered frame without removing it.
    /// Returns <c>false</c> if the buffer is empty.
    /// </summary>
    public bool TryPeekFirstTimestamp(out long timestamp)
    {
        lock (_frames)
        {
            if (_frames.Count == 0) { timestamp = 0; return false; }
            timestamp = _frames.Keys[0];
            return true;
        }
    }

    /// <summary>
    /// Returns the number of buffered frames whose timestamp is strictly less than
    /// <paramref name="threshold"/>.  Useful for reporting how many frames would be
    /// discarded by a bulk-drop operation.
    /// </summary>
    public int CountBefore(long threshold)
    {
        lock (_frames)
        {
            int count = 0;
            for (int i = 0; i < _frames.Count; i++)
            {
                if (_frames.Keys[i] >= threshold) break;
                count++;
            }
            return count;
        }
    }

    /// <summary>Removes all buffered frames and resets the cursor.</summary>
    public void Clear()
    {
        lock (_frames)
        {
            _frames.Clear();
            _totalDuration = TimeSpan.Zero;
            _nextExpected  = long.MinValue;
        }
    }
}
