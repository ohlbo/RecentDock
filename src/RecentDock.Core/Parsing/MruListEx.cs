namespace RecentDock.Core.Parsing;

/// <summary>
/// Parser for the RecentDocs MRUListEx value.
///
/// Layout, confirmed against a live key: a sequence of little-endian 32-bit
/// integers terminated by 0xFFFFFFFF. Each integer names a value in the same key
/// (the values are called "0", "1", "2", ...). Position in the sequence is the
/// access rank: the first integer is the most recently used record.
///
/// Measured sample: 36 bytes -> chain 7 4 3 6 5 2 1 0 (8 indices + terminator).
/// This is the authoritative access order and is preferred over the .lnk
/// LastWriteTime approximation.
/// </summary>
public static class MruListEx
{
    private const uint Terminator = 0xFFFFFFFF;

    /// <summary>
    /// Cap on the chain length. A real key holds tens of entries; this only exists
    /// so that a corrupt or hostile buffer cannot produce unbounded output.
    /// </summary>
    private const int MaxEntries = 4096;

    /// <summary>
    /// Parse the chain into value indices, most-recently-used first. Stops at the
    /// terminator, at the end of the buffer, or at <see cref="MaxEntries"/>.
    /// Trailing bytes that do not form a complete DWORD are ignored.
    /// </summary>
    public static IReadOnlyList<int> Parse(byte[]? bytes)
    {
        if (bytes is null || bytes.Length < 4)
        {
            return Array.Empty<int>();
        }

        var indices = new List<int>();
        for (int offset = 0; offset + 4 <= bytes.Length; offset += 4)
        {
            // Assemble explicitly. In C# the byte operands promote to int, so this
            // is correct; the equivalent PowerShell expression silently truncated
            // because -shl keeps a [byte] operand in Byte range (0x65 -shl 8 == 0),
            // which corrupted every non-ASCII name. Do not "simplify" this.
            uint value = (uint)(bytes[offset]
                                | (bytes[offset + 1] << 8)
                                | (bytes[offset + 2] << 16)
                                | (bytes[offset + 3] << 24));

            if (value == Terminator)
            {
                break;
            }

            indices.Add((int)value);

            if (indices.Count >= MaxEntries)
            {
                break;
            }
        }

        return indices;
    }
}
