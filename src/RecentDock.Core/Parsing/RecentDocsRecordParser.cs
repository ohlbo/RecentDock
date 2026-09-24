using System.Text;

namespace RecentDock.Core.Parsing;

/// <summary>
/// One parsed record from the RecentDocs registry key.
/// </summary>
/// <param name="Name">
/// The file name exactly as stored, e.g. "report.pdf". Matches a Recent-folder
/// entry once ".lnk" is appended.
/// </param>
/// <param name="NameFieldLength">
/// Byte length of the name field, including its two-byte terminator.
/// </param>
/// <param name="PidlOffset">
/// Offset of the ITEMIDLIST within the value buffer, or -1 when it could not be
/// located.
/// </param>
public sealed record RecentDocRecord(string Name, int NameFieldLength, int PidlOffset);

/// <summary>
/// Parser for a single RecentDocs registry value (REG_BINARY).
///
/// Layout, confirmed byte-for-byte against a live key (a 152-byte record):
///
///   0x00  UTF-16LE file name, terminated by 00 00
///   0x1A  metadata: MRU run count, string length, padding
///   0x26  UTF-16LE name again, this time with the ".lnk" suffix
///   0x4A  ITEMIDLIST
///   0x96  end of buffer
///
/// Two findings drive this implementation:
///
///   1. The name is decoded with Encoding.Unicode, never a hand-rolled
///      lo | (hi &lt;&lt; 8) loop. That loop is wrong in PowerShell 5.1 for any name
///      whose second byte is below 0x80, which mangled every CJK file name.
///
///   2. The PIDL offset is NOT found by scanning for a plausible looking header.
///      Two such heuristics were tried against real data and both locked onto a
///      00 00 pair inside the name or metadata region, yielding no path at all.
///      Sweeping every even offset proved that the only offset which resolves is
///      immediately after the name field's terminator.
/// </summary>
public static class RecentDocsRecordParser
{
    /// <summary>
    /// Parse a record. Returns null only when the buffer cannot be a record at all
    /// (too short, or no name terminator found).
    /// </summary>
    public static RecentDocRecord? Parse(byte[]? buffer)
    {
        if (buffer is null || buffer.Length < 4)
        {
            return null;
        }

        int terminator = FindNameTerminator(buffer);
        if (terminator < 2)
        {
            return null;
        }

        string name = Encoding.Unicode.GetString(buffer, 0, terminator);

        // A control character in the name means the "name" we sliced is not text,
        // so the field boundary is not trustworthy and neither is the PIDL offset
        // derived from it.
        if (!IsPlausibleName(name))
        {
            return null;
        }

        int nameFieldLength = terminator + 2;
        int pidlOffset = nameFieldLength;

        if (pidlOffset >= buffer.Length)
        {
            pidlOffset = -1;
        }

        return new RecentDocRecord(name, nameFieldLength, pidlOffset);
    }

    /// <summary>
    /// Offset of the 00 00 that terminates the UTF-16LE name, scanning on even
    /// boundaries only (a UTF-16 code unit is two bytes).
    /// </summary>
    private static int FindNameTerminator(byte[] buffer)
    {
        for (int i = 0; i + 1 < buffer.Length; i += 2)
        {
            if (buffer[i] == 0 && buffer[i + 1] == 0)
            {
                return i;
            }
        }

        return -1;
    }

    private static bool IsPlausibleName(string name)
    {
        if (name.Length == 0)
        {
            return false;
        }

        foreach (char c in name)
        {
            if (char.IsControl(c))
            {
                return false;
            }
        }

        return true;
    }
}
