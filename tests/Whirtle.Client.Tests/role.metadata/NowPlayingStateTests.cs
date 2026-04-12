using Whirtle.Client.Protocol;
using Whirtle.Client.Role;

namespace Whirtle.Client.Tests.Role;

public class NowPlayingStateTests
{
    private static ServerMetadataState SampleState(
        string? title    = "Song",
        string? artist   = "Artist",
        string? album    = "Album",
        double? progress = 30.0)
        => new(Title: title, Artist: artist, Album: album, Progress: progress);

    [Fact]
    public void Update_SetsAllFields()
    {
        var state = new NowPlayingState();
        state.Update(SampleState());

        Assert.Equal("Song",   state.Title);
        Assert.Equal("Artist", state.Artist);
        Assert.Equal("Album",  state.Album);
        Assert.Equal(30.0,     state.ProgressSeconds);
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
        Assert.Null(state.Album);
        Assert.Null(state.ProgressSeconds);
    }

    [Fact]
    public void ToString_IncludesPresentFields()
    {
        var state = new NowPlayingState();
        state.Update(SampleState("Track", "Band", "Record"));

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
