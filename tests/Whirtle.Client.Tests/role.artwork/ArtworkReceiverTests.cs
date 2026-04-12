using Whirtle.Client.Protocol;
using Whirtle.Client.Role;

namespace Whirtle.Client.Tests.Role;

public class ArtworkReceiverTests
{
    private static readonly byte[] JpegMagic = [0xFF, 0xD8, 0x00, 0x00];
    private static readonly byte[] PngMagic  = [0x89, 0x50, 0x4E, 0x47, 0x00];
    private static readonly byte[] Unknown   = [0x00, 0x01, 0x02, 0x03];

    // 8-byte big-endian representation of timestamp 1000 (microseconds).
    private static readonly byte[] Timestamp1000 = [0, 0, 0, 0, 0, 0, 0x03, 0xE8];

    [Fact]
    public void ProcessFrame_SetsDataAndMimeType()
    {
        var receiver = new ArtworkReceiver();
        var frame    = new ArtworkFrame(0L, JpegMagic, "image/jpeg");

        receiver.ProcessFrame(frame);

        Assert.Equal(JpegMagic,    receiver.Data);
        Assert.Equal("image/jpeg", receiver.MimeType);
    }

    [Fact]
    public void ProcessFrame_SetsTimestamp()
    {
        var receiver = new ArtworkReceiver();
        receiver.ProcessFrame(new ArtworkFrame(42_000L, JpegMagic, "image/jpeg"));

        Assert.Equal(42_000L, receiver.Timestamp);
    }

    [Fact]
    public void ProcessFrame_RaisesChangedEvent()
    {
        var receiver = new ArtworkReceiver();
        int raised   = 0;
        receiver.Changed += () => raised++;

        receiver.ProcessFrame(new ArtworkFrame(0L, JpegMagic, "image/jpeg"));

        Assert.Equal(1, raised);
    }

    [Fact]
    public void ProcessFrame_OverwritesPreviousArtwork()
    {
        var receiver = new ArtworkReceiver();
        receiver.ProcessFrame(new ArtworkFrame(0L, JpegMagic, "image/jpeg"));
        receiver.ProcessFrame(new ArtworkFrame(0L, PngMagic,  "image/png"));

        Assert.Equal("image/png", receiver.MimeType);
        Assert.Equal(PngMagic,    receiver.Data);
    }

    [Fact]
    public void ProcessFrame_EmptyData_ClearsArtwork()
    {
        var receiver = new ArtworkReceiver();
        receiver.ProcessFrame(new ArtworkFrame(0L, JpegMagic, "image/jpeg"));

        // Empty data = server is clearing the channel.
        receiver.ProcessFrame(new ArtworkFrame(1L, [], string.Empty));

        Assert.Null(receiver.Data);
        Assert.Equal(string.Empty, receiver.MimeType);
    }

    [Fact]
    public void ProcessFrame_EmptyData_StillRaisesChangedEvent()
    {
        var receiver = new ArtworkReceiver();
        int raised   = 0;
        receiver.Changed += () => raised++;

        receiver.ProcessFrame(new ArtworkFrame(0L, [], string.Empty));

        Assert.Equal(1, raised);
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

    [Fact]
    public async Task ProtocolClient_ParsesTimestamp_FromBinaryFrame()
    {
        var frame = await ReceiveOneArtworkFrameAsync(JpegMagic, timestampUs: 1_000L);
        Assert.Equal(1_000L, frame.Timestamp);
    }

    [Fact]
    public async Task ProtocolClient_YieldsClearFrame_WhenNoImageData()
    {
        // A clear message has the type byte and timestamp but no image bytes.
        var frame = await ReceiveOneArtworkFrameAsync(imageData: [], timestampUs: 99L);
        Assert.Empty(frame.Data);
        Assert.Equal(99L, frame.Timestamp);
    }

    private static async Task<ArtworkFrame> ReceiveOneArtworkFrameAsync(
        byte[] imageData,
        long   timestampUs = 0L)
    {
        var transport = new Whirtle.Client.Tests.Protocol.FakeTransport();
        var protocol  = new Whirtle.Client.Protocol.ProtocolClient(transport);

        // Build binary frame: type byte (8) + 8-byte big-endian timestamp + image bytes.
        byte[] tsBytes = [
            (byte)(timestampUs >> 56), (byte)(timestampUs >> 48),
            (byte)(timestampUs >> 40), (byte)(timestampUs >> 32),
            (byte)(timestampUs >> 24), (byte)(timestampUs >> 16),
            (byte)(timestampUs >>  8), (byte) timestampUs,
        ];
        transport.EnqueueInbound([8, .. tsBytes, .. imageData]);
        transport.CloseInbound();

        await foreach (var frame in protocol.ReceiveAllAsync())
        {
            if (frame is ArtworkFrame art) return art;
        }

        throw new InvalidOperationException("No ArtworkFrame received.");
    }
}
