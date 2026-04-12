using Whirtle.Client.Protocol;
using Whirtle.Client.Role;

namespace Whirtle.Client.Tests.Role;

public class NowPlayingStateTests
{
    private static PlaybackProgress SampleProgress(
        int trackProgress  = 5_000,
        int trackDuration  = 180_000,
        int playbackSpeed  = 1_000)
        => new(trackProgress, trackDuration, playbackSpeed);

    private static ServerMetadataState SampleState(
        long    timestamp   = 1_000_000L,
        string? title       = "Song",
        string? artist      = "Artist",
        string? albumArtist = "Various",
        string? album       = "Album",
        string? artworkUrl  = null,
        int?    year        = 2024,
        int?    track       = 3,
        PlaybackProgress? progress = null,
        string? repeat      = "off",
        bool?   shuffle     = false)
        => new(
            Timestamp:   timestamp,
            Title:       title,
            Artist:      artist,
            AlbumArtist: albumArtist,
            Album:       album,
            ArtworkUrl:  artworkUrl,
            Year:        year,
            Track:       track,
            Progress:    progress ?? SampleProgress(),
            Repeat:      repeat,
            Shuffle:     shuffle);

    [Fact]
    public void Update_SetsAllFields()
    {
        var state = new NowPlayingState();
        state.Update(SampleState());

        Assert.Equal(1_000_000L, state.Timestamp);
        Assert.Equal("Song",     state.Title);
        Assert.Equal("Artist",   state.Artist);
        Assert.Equal("Various",  state.AlbumArtist);
        Assert.Equal("Album",    state.Album);
        Assert.Equal(2024,       state.Year);
        Assert.Equal(3,          state.Track);
        Assert.Equal("off",      state.Repeat);
        Assert.False(state.Shuffle);
        Assert.NotNull(state.Progress);
    }

    [Fact]
    public void Update_RaisesChangedEvent()
    {
        var state  = new NowPlayingState();
        int raised = 0;
        state.Changed += () => raised++;

        state.Update(SampleState());

        Assert.Equal(1, raised);
    }

    [Fact]
    public void Update_OverwritesPreviousValues()
    {
        var state = new NowPlayingState();
        state.Update(SampleState(title: "Old"));
        state.Update(SampleState(title: "New"));

        Assert.Equal("New", state.Title);
    }

    [Fact]
    public void Update_NullableFields_AreNullWhenAbsent()
    {
        var state = new NowPlayingState();
        state.Update(new ServerMetadataState());

        Assert.Null(state.Title);
        Assert.Null(state.Artist);
        Assert.Null(state.AlbumArtist);
        Assert.Null(state.Album);
        Assert.Null(state.ArtworkUrl);
        Assert.Null(state.Year);
        Assert.Null(state.Track);
        Assert.Null(state.Repeat);
        Assert.Null(state.Shuffle);
    }

    [Fact]
    public void Update_RetainsExistingProgress_WhenNewStateHasNone()
    {
        var state    = new NowPlayingState();
        var progress = SampleProgress(trackProgress: 10_000);
        state.Update(SampleState(progress: progress));
        state.Update(new ServerMetadataState(Title: "New"));

        // progress must be preserved across partial updates
        Assert.Same(progress, state.Progress);
    }

    [Fact]
    public void CalculatePositionMs_ReturnsZero_WhenNoProgress()
    {
        var state = new NowPlayingState();
        Assert.Equal(0, state.CalculatePositionMs(currentTimestampUs: 2_000_000L));
    }

    [Fact]
    public void CalculatePositionMs_AtTimestamp_ReturnsTrackProgress()
    {
        var state = new NowPlayingState();
        state.Update(SampleState(
            timestamp: 1_000_000L,
            progress:  SampleProgress(trackProgress: 5_000, playbackSpeed: 1_000)));

        // current_time == metadata.timestamp → no elapsed time
        Assert.Equal(5_000, state.CalculatePositionMs(1_000_000L));
    }

    [Fact]
    public void CalculatePositionMs_AdvancesAtNormalSpeed()
    {
        var state = new NowPlayingState();
        // timestamp = 0 µs, track at 0 ms, normal speed
        state.Update(new ServerMetadataState(
            Timestamp: 0L,
            Progress:  new PlaybackProgress(0, 60_000, 1_000)));

        // 10 seconds elapsed (10 000 000 µs) → expect 10 000 ms
        Assert.Equal(10_000, state.CalculatePositionMs(10_000_000L));
    }

    [Fact]
    public void CalculatePositionMs_AdvancesAtDoubleSpeed()
    {
        var state = new NowPlayingState();
        state.Update(new ServerMetadataState(
            Timestamp: 0L,
            Progress:  new PlaybackProgress(0, 120_000, 2_000)));

        // 5 seconds elapsed → 10 000 ms at 2× speed
        Assert.Equal(10_000, state.CalculatePositionMs(5_000_000L));
    }

    [Fact]
    public void CalculatePositionMs_ClampsToTrackDuration()
    {
        var state = new NowPlayingState();
        state.Update(new ServerMetadataState(
            Timestamp: 0L,
            Progress:  new PlaybackProgress(0, 30_000, 1_000)));

        // 60 seconds elapsed, but track is only 30 000 ms
        Assert.Equal(30_000, state.CalculatePositionMs(60_000_000L));
    }

    [Fact]
    public void CalculatePositionMs_ClampsToZero()
    {
        var state = new NowPlayingState();
        state.Update(new ServerMetadataState(
            Timestamp: 5_000_000L,
            Progress:  new PlaybackProgress(0, 30_000, 1_000)));

        // current_time < timestamp → negative elapsed → clamp to 0
        Assert.Equal(0, state.CalculatePositionMs(0L));
    }

    [Fact]
    public void CalculatePositionMs_Paused_DoesNotAdvance()
    {
        var state = new NowPlayingState();
        state.Update(new ServerMetadataState(
            Timestamp: 0L,
            Progress:  new PlaybackProgress(15_000, 60_000, 0)));

        // playback_speed == 0 → position stays at track_progress
        Assert.Equal(15_000, state.CalculatePositionMs(10_000_000L));
    }

    [Fact]
    public void CalculatePositionMs_UnlimitedDuration_NoUpperClamp()
    {
        var state = new NowPlayingState();
        state.Update(new ServerMetadataState(
            Timestamp: 0L,
            Progress:  new PlaybackProgress(0, 0, 1_000)));

        // track_duration == 0 means no upper bound
        Assert.Equal(3_600_000, state.CalculatePositionMs(3_600_000_000L));
    }

    [Fact]
    public void ToString_IncludesPresentFields()
    {
        var state = new NowPlayingState();
        state.Update(SampleState(title: "Track", artist: "Band", album: "Record"));

        var text = state.ToString();
        Assert.Contains("Track",  text);
        Assert.Contains("Band",   text);
        Assert.Contains("Record", text);
    }

    [Fact]
    public void ToString_WhenNoMetadata_ReturnsDefault()
    {
        var state = new NowPlayingState();
        Assert.Equal("(no metadata)", state.ToString());
    }
}
