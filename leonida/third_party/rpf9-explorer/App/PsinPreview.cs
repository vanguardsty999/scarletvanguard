using RpfPreview.Core;
using System.IO;
using System.Text;

namespace RpfPreview.App;

internal static class PsinPreview
{
    public static string Describe(byte[] bytes)
    {
        var inspection = PsinMetadata.Inspect(bytes)
            ?? throw new InvalidDataException("This metadata container is incomplete or uses an unsupported section layout.");
        var text = new StringBuilder();
        text.AppendLine("PSIN binary metadata").AppendLine();
        text.AppendLine($"Complete container: {MainWindow.FormatSize(inspection.Length)}");
        text.AppendLine($"Sections: {inspection.Sections.Count}").AppendLine();
        text.AppendLine("Section   Offset       Bytes");
        text.AppendLine("───────   ──────────   ──────────");
        foreach (var section in inspection.Sections)
            text.AppendLine($"{section.Tag,-9} 0x{section.Offset:X8}   {section.Length,10:N0}");
        text.AppendLine().AppendLine("The enclosing zlib checksum, section boundaries and CHKS total length were validated.");
        text.AppendLine("The internal CHKS checksum and the meaning of every binary field are not decoded.");
        text.AppendLine().AppendLine("This is a recovered metadata resource. Its original filename is unknown.");
        text.AppendLine("A PSIN entry in script.rpf is metadata; it is not a decompiled executable script.");
        var strings = ReadStrings(bytes);
        text.AppendLine().AppendLine("Embedded text (bounded preview)");
        text.AppendLine("──────────────────────────────");
        if (strings.Count == 0) text.AppendLine("No readable text strings found. Use Hex to inspect the binary data.");
        foreach (var value in strings) text.AppendLine(value);
        return text.ToString();
    }

    private static List<string> ReadStrings(byte[] bytes)
    {
        var strings = new List<string>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        for (int p = 0; p < bytes.Length && strings.Count < 100; p++)
        {
            if (bytes[p] is < 32 or > 126) continue;
            int start = p;
            while (p < bytes.Length && bytes[p] is >= 32 and <= 126) p++;
            int length = p - start;
            if (length < 8 || length > 512 || p == bytes.Length || bytes[p] != 0) continue;
            var value = Encoding.ASCII.GetString(bytes, start, length);
            if (value.All(c => char.IsAsciiLetterOrDigit(c) || c is '_' or '.' or '/' or '\\' or ':' or '-' or ' ')
                && value.Count(char.IsAsciiLetter) >= 4 && value.Distinct().Count() > 3 && seen.Add(value)) strings.Add(value);
        }
        return strings;
    }
}
