using Whirtle.Client.Artwork;
using Whirtle.Client.Protocol;

namespace Whirtle.Client.Tests.Artwork;

public class ArtworkReceiverTests
{
    private static readonly byte[] JpegMagic = [0xFF, 0xD8, 0x00, 0x00];
    private static readonly byte[] PngMagic  = [0x89, 0x50, 0x4E, 0x47, 0x00];
    private static readonly byte[] Unknown   = [0x00, 0x01, 0x02, 0x03];

    [Fact]
    public void ProcessFrame_SetsDataAndMimeType()
    {
        var receiver = new ArtworkReceiver();
        var frame    = new ArtworkFrame(JpegMagic, "image/jpeg");

        receiver.ProcessFrame(frame);

        Assert.Equal(JpegMagic,   receiver.Data);
        Assert.Equal("image/jpeg", receiver.MimeType);
    }

    [Fact]
    public void ProcessFrame_RaisesChangedEvent()
    {
        var receiver = new ArtworkReceiver();
        int raised   = 0;
        receiver.Changed += () => raised++;

        receiver.ProcessFrame(new ArtworkFrame(JpegMagic, "image/jpeg"));

        Assert.Equal(1, raised);
    }

    [Fact]
    public void ProcessFrame_OverwritesPreviousArtwork()
    {
        var receiver = new ArtworkReceiver();
        receiver.ProcessFrame(new ArtworkFrame(JpegMagic, "image/jpeg"));
        receiver.ProcessFrame(new ArtworkFrame(PngMagic,  "image/png"));

        Assert.Equal("image/png", receiver.MimeType);
        Assert.Equal(PngMagic,    receiver.Data);
    }

    [Fact]
    public void Data_IsNull_BeforeFirstFrame()
    {
        var receiver = new ArtworkReceiver();
        Assert.Null(receiver.Data);
    }

    // ── DetectMimeType (via ProtocolClient) ──────────────────────────────────

    [Fact]
    public async Task ProtocolClient_DetectsJpeg_FromBinaryFrame()
    {
        var frame = await ReceiveOneArtworkFrameAsync(JpegMagic);
        Assert.Equal("image/jpeg", frame.MimeType);
    }

    [Fact]
    public async Task ProtocolClient_DetectsPng_FromBinaryFrame()
    {
        var frame = await ReceiveOneArtworkFrameAsync(PngMagic);
        Assert.Equal("image/png", frame.MimeType);
    }

    [Fact]
    public async Task ProtocolClient_FallsBackToOctetStream_ForUnknownBinary()
    {
        var frame = await ReceiveOneArtworkFrameAsync(Unknown);
        Assert.Equal("application/octet-stream", frame.MimeType);
    }

    private static async Task<ArtworkFrame> ReceiveOneArtworkFrameAsync(byte[] binaryData)
    {
        var transport = new Whirtle.Client.Tests.Protocol.FakeTransport();
        var protocol  = new Whirtle.Client.Protocol.ProtocolClient(transport);

        transport.EnqueueInbound(binaryData);
        transport.CloseInbound();

        await foreach (var frame in protocol.ReceiveAllAsync())
        {
            if (frame is ArtworkFrame art) return art;
        }

        throw new InvalidOperationException("No ArtworkFrame received.");
    }
}
