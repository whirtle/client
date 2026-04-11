using Whirtle.Client.Metadata;
using Whirtle.Client.Protocol;

namespace Whirtle.Client.Tests.Metadata;

public class NowPlayingStateTests
{
    private static NowPlayingMessage SampleMsg(
        string? title  = "Song",
        string? artist = "Artist",
        string? album  = "Album",
        double? dur    = 240.0,
        double? pos    = 30.0)
        => new(title, artist, album, dur, pos);

    [Fact]
    public void Update_SetsAllFields()
    {
        var state = new NowPlayingState();
        state.Update(SampleMsg());

        Assert.Equal("Song",   state.Title);
        Assert.Equal("Artist", state.Artist);
        Assert.Equal("Album",  state.Album);
        Assert.Equal(240.0,    state.DurationSeconds);
        Assert.Equal(30.0,     state.PositionSeconds);
    }

    [Fact]
    public void Update_RaisesChangedEvent()
    {
        var state  = new NowPlayingState();
        int raised = 0;
        state.Changed += () => raised++;

        state.Update(SampleMsg());

        Assert.Equal(1, raised);
    }

    [Fact]
    public void Update_OverwritesPreviousValues()
    {
        var state = new NowPlayingState();
        state.Update(SampleMsg(title: "Old"));
        state.Update(SampleMsg(title: "New"));

        Assert.Equal("New", state.Title);
    }

    [Fact]
    public void Update_NullableFields_AreNullWhenAbsent()
    {
        var state = new NowPlayingState();
        state.Update(new NowPlayingMessage(null, null, null, null, null));

        Assert.Null(state.Title);
        Assert.Null(state.Artist);
        Assert.Null(state.Album);
        Assert.Null(state.DurationSeconds);
        Assert.Null(state.PositionSeconds);
    }

    [Fact]
    public void ToString_IncludesPresentFields()
    {
        var state = new NowPlayingState();
        state.Update(SampleMsg("Track", "Band", "Record"));

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
