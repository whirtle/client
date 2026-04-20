using Whirtle.Client.Protocol;

namespace Whirtle.Client.Tests.Protocol;

public class ConnectionManagerTests
{
    // ── No current connection ─────────────────────────────────────────────────

    [Fact]
    public void ShouldAccept_WhenNoCurrentConnection_ReturnsTrue()
    {
        var mgr = new ConnectionManager();
        Assert.True(mgr.ShouldAccept("srv-1", "discovery"));
    }

    // ── Playback always wins ──────────────────────────────────────────────────

    [Fact]
    public void ShouldAccept_PlaybackBeatsDiscovery()
    {
        var mgr = new ConnectionManager();
        mgr.Accept("srv-1", "discovery");

        Assert.True(mgr.ShouldAccept("srv-2", "playback"));
    }

    [Fact]
    public void ShouldAccept_PlaybackBeatsPlayback()
    {
        var mgr = new ConnectionManager();
        mgr.Accept("srv-1", "playback");

        Assert.True(mgr.ShouldAccept("srv-2", "playback"));
    }

    // ── Discovery cannot displace playback ────────────────────────────────────

    [Fact]
    public void ShouldAccept_DiscoveryCannotReplacePlayback()
    {
        var mgr = new ConnectionManager();
        mgr.Accept("srv-1", "playback");

        Assert.False(mgr.ShouldAccept("srv-2", "discovery"));
    }

    // ── Both discovery: prefer last-played ────────────────────────────────────

    [Fact]
    public void ShouldAccept_BothDiscovery_PrefersLastPlayedServer()
    {
        var mgr = new ConnectionManager { LastPlayedServerId = "srv-2" };
        mgr.Accept("srv-1", "discovery");

        Assert.True(mgr.ShouldAccept("srv-2", "discovery"));
    }

    [Fact]
    public void ShouldAccept_BothDiscovery_RejectsNonLastPlayed()
    {
        var mgr = new ConnectionManager { LastPlayedServerId = "srv-other" };
        mgr.Accept("srv-1", "discovery");

        Assert.False(mgr.ShouldAccept("srv-2", "discovery"));
    }

    // ── Same server reconnects ────────────────────────────────────────────────

    [Fact]
    public void ShouldAccept_SameServer_AlwaysTrue()
    {
        var mgr = new ConnectionManager();
        mgr.Accept("srv-1", "playback");

        Assert.True(mgr.ShouldAccept("srv-1", "discovery"));
    }

    // ── Clear resets state ────────────────────────────────────────────────────

    [Fact]
    public void ShouldAccept_AfterClear_AcceptsAnyServer()
    {
        var mgr = new ConnectionManager();
        mgr.Accept("srv-1", "playback");
        mgr.Clear();

        Assert.True(mgr.ShouldAccept("srv-2", "discovery"));
    }

    // ── Accept does not modify LastPlayedServerId (set externally on playback) ──

    [Fact]
    public void Accept_DoesNotSetLastPlayedServerId()
    {
        var mgr = new ConnectionManager();
        mgr.Accept("srv-1", "playback");

        Assert.Null(mgr.LastPlayedServerId);
    }

    [Fact]
    public void Accept_DoesNotOverwriteExternallySetLastPlayedServerId()
    {
        var mgr = new ConnectionManager { LastPlayedServerId = "srv-old" };
        mgr.Accept("srv-new", "playback");

        Assert.Equal("srv-old", mgr.LastPlayedServerId);
    }
}
