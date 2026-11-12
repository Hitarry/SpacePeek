using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace SpacePeek.Interop;

/// <summary>
/// Minimal ZIP central-directory reader. System.IO.Compression decodes entry names as UTF-8,
/// which turns GBK-encoded names (the norm for archives made by Chinese Windows tools) into
/// mojibake. Here the raw name bytes are decoded with the same fallback chain as text files.
/// </summary>
internal static class ZipListing
{
    static ZipListing()
    {
        // GBK / ANSI code pages are not available in .NET without this provider.
        try { Encoding.RegisterProvider(CodePagesEncodingProvider.Instance); } catch { }
    }

    public sealed record Entry(string Name, long Size, long PackedSize, bool IsDirectory);

    public static List<Entry> Read(string path, int limit)
    {
        var entries = new List<Entry>();
        using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        if (fs.Length < 22) return entries;

        long tailLength = Math.Min(fs.Length, 66 * 1024);
        var tail = new byte[tailLength];
        fs.Seek(-tailLength, SeekOrigin.End);
        fs.ReadExactly(tail);

        int eocd = FindSignature(tail, 0x06054b50);
        if (eocd < 0) return entries;

        int count = BitConverter.ToUInt16(tail, eocd + 10);
        uint cdSize = BitConverter.ToUInt32(tail, eocd + 12);
        uint cdOffset = BitConverter.ToUInt32(tail, eocd + 16);
        if (cdOffset == 0xFFFFFFFF) return entries;   // zip64: not worth it for a preview

        var cd = new byte[Math.Min(cdSize, 64 * 1024 * 1024)];
        fs.Seek(cdOffset, SeekOrigin.Begin);
        fs.ReadExactly(cd);

        int p = 0;
        for (int i = 0; i < count && p + 46 <= cd.Length; i++)
        {
            if (BitConverter.ToUInt32(cd, p) != 0x02014b50) break;

            ushort flags = BitConverter.ToUInt16(cd, p + 8);
            long packed = BitConverter.ToUInt32(cd, p + 20);
            long size = BitConverter.ToUInt32(cd, p + 24);
            int nameLength = BitConverter.ToUInt16(cd, p + 28);
            int extraLength = BitConverter.ToUInt16(cd, p + 30);
            int commentLength = BitConverter.ToUInt16(cd, p + 32);

            string name = DecodeName(cd, p + 46, nameLength, (flags & 0x800) != 0);
            p += 46 + nameLength + extraLength + commentLength;

            if (name.Length == 0) continue;
            bool isDir = name.EndsWith("/", StringComparison.Ordinal) ||
                         name.EndsWith("\\", StringComparison.Ordinal);
            entries.Add(new Entry(name, size, packed, isDir));
            if (entries.Count >= limit) break;
        }

        return entries;
    }

    private static int FindSignature(byte[] buffer, uint signature)
    {
        for (int i = buffer.Length - 4; i >= 0; i--)
        {
            if (buffer[i] == (byte)signature &&
                buffer[i + 1] == (byte)(signature >> 8) &&
                buffer[i + 2] == (byte)(signature >> 16) &&
                buffer[i + 3] == (byte)(signature >> 24))
                return i;
        }
        return -1;
    }

    private static string DecodeName(byte[] buffer, int offset, int length, bool declaredUtf8)
    {
        if (length <= 0) return string.Empty;

        if (declaredUtf8)
            return Encoding.UTF8.GetString(buffer, offset, length);

        try
        {
            return new UTF8Encoding(false, true).GetString(buffer, offset, length);
        }
        catch (DecoderFallbackException)
        {
            // not valid UTF-8: almost certainly the system ANSI code page (GBK for zh-CN)
            try { return Encoding.GetEncoding(936).GetString(buffer, offset, length); }
            catch { }
            try { return Encoding.GetEncoding(0).GetString(buffer, offset, length); }
            catch { }
            return Encoding.Latin1.GetString(buffer, offset, length);
        }
    }
}
