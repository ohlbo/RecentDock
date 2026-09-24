using RecentDock.Core;
using RecentDock.Core.Parsing;
using Xunit;

namespace RecentDock.Core.Tests;

/// <summary>
/// MruListEx parser tests.
///
/// The MRU chain is the authoritative ordering source, so a silent misparse here
/// reorders the whole panel. The truncation hazard is real and specific: the
/// original PowerShell implementation read 0xFFFFFFFF as an int32 and compared it
/// to uint.MaxValue, which never matched, so the loop ran to the end of the buffer
/// instead of stopping at the terminator.
/// </summary>
public class MruListExTests
{
    /// <summary>Build a chain the way the registry stores it.</summary>
    private static byte[] Chain(params int[] indices)
    {
        var bytes = new List<byte>(indices.Length * 4 + 4);
        foreach (int index in indices)
        {
            bytes.Add((byte)(index & 0xFF));
            bytes.Add((byte)((index >> 8) & 0xFF));
            bytes.Add((byte)((index >> 16) & 0xFF));
            bytes.Add((byte)((index >> 24) & 0xFF));
        }

        bytes.AddRange(new byte[] { 0xFF, 0xFF, 0xFF, 0xFF });
        return bytes.ToArray();
    }

    [Fact]
    public void Parse_MeasuredSample_ReturnsLiveChain()
    {
        // Verbatim from the verification run: 36 bytes, chain 7 4 3 6 5 2 1 0.
        byte[] raw =
        {
            0x07, 0x00, 0x00, 0x00,
            0x04, 0x00, 0x00, 0x00,
            0x03, 0x00, 0x00, 0x00,
            0x06, 0x00, 0x00, 0x00,
            0x05, 0x00, 0x00, 0x00,
            0x02, 0x00, 0x00, 0x00,
            0x01, 0x00, 0x00, 0x00,
            0x00, 0x00, 0x00, 0x00,
            0xFF, 0xFF, 0xFF, 0xFF,
        };

        Assert.Equal(36, raw.Length);
        Assert.Equal(new[] { 7, 4, 3, 6, 5, 2, 1, 0 }, MruListEx.Parse(raw));
    }

    [Fact]
    public void Parse_StopsAtTerminator_IgnoresTrailingGarbage()
    {
        byte[] raw = Chain(2, 0);
        byte[] withGarbage = raw.Concat(new byte[] { 0xAA, 0xBB, 0xCC, 0xDD }).ToArray();

        Assert.Equal(new[] { 2, 0 }, MruListEx.Parse(withGarbage));
    }

    [Fact]
    public void Parse_TerminatorOnly_ReturnsEmpty()
    {
        byte[] raw = { 0xFF, 0xFF, 0xFF, 0xFF };
        Assert.Empty(MruListEx.Parse(raw));
    }

    [Theory]
    [InlineData(new byte[0])]
    [InlineData(new byte[] { 0x01 })]
    [InlineData(new byte[] { 0x01, 0x02, 0x03 })]
    public void Parse_IncompleteDword_ReturnsEmpty(byte[] raw)
    {
        Assert.Empty(MruListEx.Parse(raw));
    }

    [Fact]
    public void Parse_Null_ReturnsEmpty()
    {
        Assert.Empty(MruListEx.Parse(null));
    }

    [Fact]
    public void Parse_NoTerminator_ReturnsWholeBuffer()
    {
        // A truncated value with no terminator should still yield its complete
        // DWORDs rather than throwing.
        byte[] raw = { 0x01, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00 };
        Assert.Equal(new[] { 1, 0 }, MruListEx.Parse(raw));
    }

    [Fact]
    public void Parse_TrailingPartialDword_IsIgnored()
    {
        byte[] raw = Chain(5).Concat(new byte[] { 0x01, 0x02 }).ToArray();
        Assert.Equal(new[] { 5 }, MruListEx.Parse(raw));
    }
}
