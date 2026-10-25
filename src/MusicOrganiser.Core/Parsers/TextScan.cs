using System.Text;
using System.Text.RegularExpressions;

namespace MusicOrganiser.Core.Parsers;

/// <summary>Helpers shared by the readers.</summary>
public static partial class TextScan
{
    public static readonly string[] AudioExtensions =
        [".wav", ".aif", ".aiff", ".flac", ".mp3", ".ogg", ".rx2", ".rex", ".wv", ".m4a"];

    public static readonly string[] PluginExtensions = [".vst3", ".dll", ".clap", ".component", ".vst", ".aaxplugin"];

    public static bool IsAudioPath(string s) =>
        AudioExtensions.Any(e => s.TrimEnd('\0', ' ').EndsWith(e, StringComparison.OrdinalIgnoreCase));

    public static bool IsPluginPath(string s) =>
        PluginExtensions.Any(e => s.EndsWith(e, StringComparison.OrdinalIgnoreCase));

    public static string PluginNameFromPath(string path)
    {
        var name = path.TrimEnd('\\', '/').Split('\\', '/')[^1];
        foreach (var e in PluginExtensions)
            if (name.EndsWith(e, StringComparison.OrdinalIgnoreCase))
                return name[..^e.Length];
        return name;
    }

    public static string PluginFormatFromPath(string path)
    {
        var p = path.ToLowerInvariant();
        if (p.EndsWith(".vst3")) return "VST3";
        if (p.EndsWith(".clap")) return "CLAP";
        if (p.EndsWith(".component")) return "AU";
        if (p.EndsWith(".aaxplugin")) return "AAX";
        if (p.EndsWith(".dll") || p.EndsWith(".vst")) return "VST2";
        return "";
    }

    /// <summary>Readable ASCII and UTF-16LE runs of 4+ characters inside a binary blob.</summary>
    public static IEnumerable<string> Strings(ReadOnlyMemory<byte> data)
    {
        var bytes = data.Span.ToArray();
        // Latin-1 keeps byte offsets 1:1 with chars, so the regexes see the raw bytes.
        var text = Encoding.Latin1.GetString(bytes);
        foreach (Match m in AsciiRun().Matches(text))
            yield return m.Value;
        foreach (Match m in Utf16Run().Matches(text))
            yield return Encoding.Unicode.GetString(bytes, m.Index, m.Length);
    }

    public static string SafeFileName(string name)
    {
        var cleaned = InvalidFileChars().Replace(name, "_").Trim(' ', '.');
        if (cleaned.Length > 120) cleaned = cleaned[..120];
        return cleaned.Length == 0 ? "untitled" : cleaned;
    }

    [GeneratedRegex(@"[\x20-\x7e]{4,}")]
    private static partial Regex AsciiRun();

    [GeneratedRegex(@"(?:[\x20-\x7e]\x00){4,}")]
    private static partial Regex Utf16Run();

    [GeneratedRegex(@"[<>:""/\\|?*\x00-\x1f]")]
    private static partial Regex InvalidFileChars();
}
