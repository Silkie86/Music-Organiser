using System.Globalization;
using System.Text;
using MusicOrganiser.Core.Parsers;

namespace MusicOrganiser.Core.Formats;

public enum LabFormat { Bitwig, FlStudio }

/// <summary>
/// One thing Format Lab found in a file: an object or field (Bitwig), an event or part of one (FL Studio),
/// a metadata entry, or a file in a Bitwig project's zip.
/// </summary>
/// <param name="Key">
/// Names the same thing in two saves of a project, so they can be compared. Parts are separated by "/", and
/// a thing's parts start with its own key.
/// </param>
/// <param name="Path">Where it is, with the registry's names where it has them.</param>
/// <param name="Depth">How deep it sits, for showing the file as a tree.</param>
/// <param name="Label">Its own part of the path.</param>
/// <param name="Value">What it holds, as text.</param>
/// <param name="Data">
/// What it holds: a string, a number (long or double), bytes, an array of numbers or strings, or null for
/// something that only holds other things.
/// </param>
/// <param name="IsGroup">It holds other things (an object, a list, an event made of parts) rather than a value.</param>
/// <param name="Start">Where its bytes sit in the data of the item that holds it, when that's known (FL Studio's parts); else -1.</param>
/// <param name="Length">How many bytes it takes there.</param>
public sealed record LabItem(string Key, string Path, int Depth, string Label, string Value, object? Data, bool IsGroup = false,
                             int Start = -1, int Length = 0);

/// <summary>A Bitwig or FL Studio file laid out for Format Lab: everything in it, in file order.</summary>
public sealed class LabFile
{
    public required string Name { get; init; }
    public required LabFormat Format { get; init; }
    public string Version { get; init; } = "";
    public required IReadOnlyList<LabItem> Items { get; init; }
    /// <summary>Why part of the file couldn't be read, or null when all of it was.</summary>
    public string? Problem { get; init; }

    public string Daw => Format == LabFormat.Bitwig ? "Bitwig Studio" : "FL Studio";

    public static LabFile Open(string path) => Read(File.ReadAllBytes(path), System.IO.Path.GetFileName(path));

    /// <summary>Lays out a Bitwig file (.bwproject, .bwpreset, .bwclip) or an FL Studio project.</summary>
    public static LabFile Read(byte[] raw, string name)
    {
        if (BitwigFormat.IsBitwig(raw)) return BitwigLab.Read(raw, name);
        if (raw.Length >= 4 && raw.AsSpan(0, 4).SequenceEqual("FLhd"u8)) return FlLab.Read(raw, name);
        throw new InvalidDataException($"{name} isn't a Bitwig or FL Studio file");
    }
}

/// <summary>How Format Lab writes values.</summary>
static class LabText
{
    public static string Number(double d) => d.ToString("R", CultureInfo.InvariantCulture);
    public static string Number(long l) => l.ToString(CultureInfo.InvariantCulture);

    public static string Quote(string s)
    {
        var sb = new StringBuilder(s.Length + 2).Append('"');
        foreach (var c in s.Length > 300 ? s[..300] : s)
            switch (c)
            {
                case '"': sb.Append("\\\""); break;
                case '\\': sb.Append("\\\\"); break;
                case '\n': sb.Append("\\n"); break;
                case '\r': sb.Append("\\r"); break;
                case '\t': sb.Append("\\t"); break;
                default:
                    if (char.IsControl(c)) sb.Append("\\u").Append(((int)c).ToString("x4"));
                    else sb.Append(c);
                    break;
            }
        if (s.Length > 300) sb.Append('…');
        return sb.Append('"').ToString();
    }

    /// <summary>Bytes as "&lt;N bytes&gt;" with a look at what they start with: text when they read as text, else hex.</summary>
    public static string Bytes(ReadOnlySpan<byte> b)
    {
        var size = b.Length == 1 ? "<1 byte>" : $"<{b.Length:N0} bytes>";
        if (b.Length == 0) return size;
        if (AsText(b) is { } text) return $"{size} {Quote(text.Length > 60 ? text[..60] + "…" : text)}";
        return $"{size} {Hex(b[..Math.Min(16, b.Length)])}{(b.Length > 16 ? " …" : "")}";
    }

    public static string Hex(ReadOnlySpan<byte> b)
    {
        var sb = new StringBuilder(b.Length * 3);
        foreach (var x in b) sb.Append(sb.Length > 0 ? " " : "").Append(x.ToString("x2"));
        return sb.ToString();
    }

    /// <summary>The text bytes hold, when all of them are printable UTF-8 or UTF-16 (little-endian); else null.</summary>
    public static string? AsText(ReadOnlySpan<byte> b)
    {
        var trimmed = b;
        while (trimmed.Length > 0 && trimmed[^1] == 0) trimmed = trimmed[..^1];
        if (trimmed.Length < 2) return null;
        // UTF-16: every other byte zero for Latin text, an even length with its terminator.
        if (b.Length % 2 == 0 && b.Length >= 4 && b[1] == 0)
        {
            var s = Encoding.Unicode.GetString(b).TrimEnd('\0');
            if (s.Length > 0 && s.All(Printable)) return s;
        }
        try
        {
            var s = new UTF8Encoding(false, true).GetString(trimmed);
            return s.All(Printable) ? s : null;
        }
        catch (DecoderFallbackException) { return null; }
    }

    static bool Printable(char c) => !char.IsControl(c) || c is '\n' or '\r' or '\t';
}
