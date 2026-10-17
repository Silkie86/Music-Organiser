using System.Globalization;
using System.Text;
using MusicOrganiser.Core.Parsers;

namespace MusicOrganiser.Core.Midi;

/// <summary>
/// MIDI as CLaMP 3 reads it: MTF ("MIDI text format"), one line per MIDI message in the order a player meets them, written
/// the way the CLaMP 3 repository's batch_midi2mtf.py writes them with mido (its "M3 compatible" form, without names or
/// other text). A line is the message type, then its values in mido's order: for channel messages the time since the
/// last message (in ticks) and the channel first, for meta messages the time last.
/// <code>
/// ticks_per_beat 96
/// set_tempo 500000 0
/// note_on 0 0 60 100
/// note_off 96 0 60 0
/// end_of_track 0
/// </code>
/// </summary>
public static class MtfText
{
    /// <summary>
    /// A part's notes (at <paramref name="ppq"/>) as one MTF text: tempo, 4/4, a piano program (as CLaMP 3's training data
    /// is cleaned to), drums on channel 10, then every note on and off.
    /// </summary>
    public static string FromNotes(IReadOnlyList<NoteEvent> notes, int ppq, double? tempo = null, bool drums = false)
    {
        var channel = drums ? 9 : 0;
        var bpm = tempo is > 10 and < 1000 ? tempo.Value : 120;
        var sb = new StringBuilder();
        sb.Append("ticks_per_beat ").Append(ppq).Append('\n');
        sb.Append("set_tempo ").Append((int)Math.Round(60_000_000 / bpm)).Append(" 0\n");
        sb.Append("time_signature 4 4 24 8 0\n");
        if (!drums) sb.Append("program_change 0 0 0\n");

        // Offs before ons at the same tick (a repeated note ends before it starts again), then low notes first.
        var events = new List<(long Tick, bool On, int Key, int Velocity)>(notes.Count * 2);
        foreach (var n in notes)
        {
            if (n.Muted) continue;
            var key = Math.Clamp(n.Key, 0, 127);
            events.Add((Math.Max(0, n.Start), true, key, Math.Clamp(n.Velocity, 1, 127)));
            events.Add((Math.Max(0, n.Start) + Math.Max(1, n.Length), false, key, 0));
        }
        events.Sort((a, b) => a.Tick != b.Tick ? a.Tick.CompareTo(b.Tick) : a.On != b.On ? (a.On ? 1 : -1) : a.Key.CompareTo(b.Key));

        var last = 0L;
        foreach (var e in events)
        {
            var delta = e.Tick - last;
            last = e.Tick;
            sb.Append(e.On ? "note_on " : "note_off ").Append(delta.ToString(CultureInfo.InvariantCulture)).Append(' ')
              .Append(channel).Append(' ').Append(e.Key).Append(' ').Append(e.Velocity).Append('\n');
        }
        sb.Append("end_of_track 0");
        return sb.ToString();
    }
}

/// <summary>
/// C# port of CLaMP 3's M3Patchilizer for MTF: lines become "patches" of 64 character codes. Consecutive lines of the
/// same message type share a patch (tab-separated, the type written once) while they fit; each patch is framed by a
/// start (1) and end (2) code and padded with 0. The whole piece is framed by a patch of start codes and one of end codes.
/// </summary>
public static class M3Patches
{
    public const int PatchSize = 64, PatchLength = 512;
    const int Pad = 0, Bos = 1, Eos = 2;

    /// <summary>Every patch of the piece, special start and end patches included.</summary>
    public static List<int[]> Encode(string mtf)
    {
        var lines = SplitLines(mtf);
        var texts = new List<string> { new((char)Bos, PatchSize) };
        var patch = "";
        foreach (var line in lines)
        {
            var space = line.IndexOf(' ');
            var type = space < 0 ? line : line[..space];
            var rest = space < 0 ? "" : line[(space + 1)..];
            if (patch.Length > 0 && patch.StartsWith(type, StringComparison.Ordinal) && patch.Length + rest.Length <= PatchSize - 2)
                patch = patch[..^1] + "\t" + rest;
            else
            {
                if (patch.Length > 0) texts.Add(patch);
                patch = line;
            }
        }
        if (patch.Length > 0) texts.Add(patch);
        texts.Add(new string((char)Eos, PatchSize));
        return texts.Select(ToPatch).ToList();
    }

    /// <summary>Lines with their line breaks kept (the last may have none), as Python's re.findall(r'.*?\n|.*$') gives them.</summary>
    static List<string> SplitLines(string text)
    {
        var lines = new List<string>();
        var start = 0;
        for (var i = 0; i < text.Length; i++)
            if (text[i] == '\n')
            {
                lines.Add(text[start..(i + 1)]);
                start = i + 1;
            }
        if (start < text.Length) lines.Add(text[start..]);
        return lines;
    }

    static int[] ToPatch(string text)
    {
        var patch = new int[PatchSize];
        patch[0] = Bos;
        var i = 1;
        foreach (var c in text)
        {
            if (i >= PatchSize) break;
            patch[i++] = c < 128 ? c : '?';   // the model knows 128 codes; MTF without names is plain ASCII anyway
        }
        if (i < PatchSize) patch[i] = Eos;
        return patch;
    }

    /// <summary>
    /// How CLaMP 3 reads a long piece: windows of <see cref="PatchLength"/> patches, the last one ending at the piece's
    /// end, each weighted by how many new patches it covers when the window results are averaged.
    /// </summary>
    public static List<(int Start, int Count, int Weight)> Windows(int patches)
    {
        var list = new List<(int, int, int)>();
        if (patches <= PatchLength)
        {
            list.Add((0, patches, patches));
            return list;
        }
        for (var s = 0; s + PatchLength <= patches; s += PatchLength) list.Add((s, PatchLength, PatchLength));
        var remain = patches % PatchLength;
        if (remain > 0) list.Add((patches - PatchLength, PatchLength, remain));
        return list;
    }
}
