using System.Buffers.Binary;
using System.IO.Compression;
using System.Text;

namespace MusicOrganiser.Core.Tests;

/// <summary>Builds small but structurally faithful project files for the tests.</summary>
public static class Fixtures
{
    public static string TempDir()
    {
        var d = Path.Combine(Path.GetTempPath(), "mo-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(d);
        return d;
    }

    /// <summary>
    /// A temp folder for tests that scan sounds. The organiser reads a sound's type from its name and up to
    /// four folders above it, and a random temp name can read as one ("aoSD9a" says snare, "ohQCpE" open hat),
    /// so four plain folders keep the random part out of reach.
    /// </summary>
    public static string SoundsDir()
    {
        var d = Path.Combine(TempDir(), "nothing", "to", "read", "here");
        Directory.CreateDirectory(d);
        return d;
    }

    /// <summary>A file in the repository (e.g. extensions/bitwig/test/serve.js), found from where the tests run.</summary>
    public static string? RepoFile(string relative)
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
            if (File.Exists(Path.Combine(dir.FullName, relative))) return Path.Combine(dir.FullName, relative);
        return null;
    }

    // ---- FL Studio ---------------------------------------------------------

    public sealed class FlpBuilder
    {
        readonly MemoryStream _events = new();
        public int Ppq = 96;

        public FlpBuilder Byte(int id, byte v) { _events.WriteByte((byte)id); _events.WriteByte(v); return this; }
        public FlpBuilder Word(int id, ushort v) { _events.WriteByte((byte)id); _events.Write(BitConverter.GetBytes(v)); return this; }
        public FlpBuilder Dword(int id, uint v) { _events.WriteByte((byte)id); _events.Write(BitConverter.GetBytes(v)); return this; }
        public FlpBuilder Raw(params byte[] bytes) { _events.Write(bytes); return this; }

        public FlpBuilder Data(int id, byte[] data)
        {
            _events.WriteByte((byte)id);
            var n = data.Length;
            do
            {
                var b = (byte)(n & 0x7F);
                n >>= 7;
                if (n > 0) b |= 0x80;
                _events.WriteByte(b);
            } while (n > 0);
            _events.Write(data);
            return this;
        }

        public FlpBuilder Text(int id, string s) => Data(id, Encoding.Unicode.GetBytes(s + "\0"));
        public FlpBuilder Ascii(int id, string s) => Data(id, Encoding.ASCII.GetBytes(s + "\0"));

        public byte[] Build()
        {
            using var ms = new MemoryStream();
            ms.Write("FLhd"u8);
            ms.Write(BitConverter.GetBytes(6));
            ms.Write(BitConverter.GetBytes((short)0));
            ms.Write(BitConverter.GetBytes((ushort)3));
            ms.Write(BitConverter.GetBytes((ushort)Ppq));
            ms.Write("FLdt"u8);
            ms.Write(BitConverter.GetBytes((int)_events.Length));
            ms.Write(_events.ToArray());
            return ms.ToArray();
        }
    }

    public static byte[] WrapperBlob(string name, string path, string vendor, byte[] state,
                                     uint version = 10, byte? kind = null, string? fourCc = null, byte[]? classId = null)
    {
        using var ms = new MemoryStream();
        ms.Write(BitConverter.GetBytes(version));
        void Field(uint id, byte[] data)
        {
            ms.Write(BitConverter.GetBytes(id));
            ms.Write(BitConverter.GetBytes((ulong)data.Length));
            ms.Write(data);
        }
        if (kind is { } k) Field(50, [k, 0, 0, 0]);
        if (fourCc is not null) Field(51, Encoding.ASCII.GetBytes(fourCc));
        if (classId is not null) Field(52, classId);
        Field(54, Encoding.UTF8.GetBytes(name));
        Field(55, Encoding.UTF8.GetBytes(path));
        Field(56, Encoding.UTF8.GetBytes(vendor));
        Field(53, state);
        return ms.ToArray();
    }

    /// <summary>An FL 20 playlist item (32 bytes) placing a pattern; offset -1 = from its start.</summary>
    public static byte[] PlaylistItem(uint pos, ushort pattern, uint length, int offset = -1)
    {
        var b = new byte[32];
        BinaryPrimitives.WriteUInt32LittleEndian(b, pos);
        BinaryPrimitives.WriteUInt16LittleEndian(b.AsSpan(4), 20480);
        BinaryPrimitives.WriteUInt16LittleEndian(b.AsSpan(6), (ushort)(20480 + pattern));
        BinaryPrimitives.WriteUInt32LittleEndian(b.AsSpan(8), length);
        BinaryPrimitives.WriteInt32LittleEndian(b.AsSpan(24), offset);
        BinaryPrimitives.WriteInt32LittleEndian(b.AsSpan(28), -1);
        return b;
    }

    /// <summary>
    /// An FL 25 playlist item (80 bytes): index 20480 + n is pattern n, a smaller index a channel's clip. An audio
    /// clip trimmed in the playlist has where it starts and ends in its sample, in milliseconds (-1 = not trimmed).
    /// </summary>
    public static byte[] PlaylistItem25(uint pos, ushort index, uint length, ushort track = 499, bool muted = false, float from = -1, float to = -1)
    {
        var b = new byte[80];
        BinaryPrimitives.WriteUInt32LittleEndian(b, pos);
        BinaryPrimitives.WriteUInt16LittleEndian(b.AsSpan(4), 20480);
        BinaryPrimitives.WriteUInt16LittleEndian(b.AsSpan(6), index);
        BinaryPrimitives.WriteUInt32LittleEndian(b.AsSpan(8), length);
        BinaryPrimitives.WriteUInt16LittleEndian(b.AsSpan(12), track);
        BinaryPrimitives.WriteUInt16LittleEndian(b.AsSpan(18), (ushort)(muted ? 0x2040 : 0x0040));
        BinaryPrimitives.WriteSingleLittleEndian(b.AsSpan(24), from);
        BinaryPrimitives.WriteSingleLittleEndian(b.AsSpan(28), to);
        return b;
    }

    /// <summary>
    /// An FL 25 playlist track's settings (event 238, 70 bytes): its number (1 at the top), colour, icon, then 1 at
    /// byte 12 when it plays, 0 when it's muted.
    /// </summary>
    public static byte[] PlaylistTrack(int number, bool muted = false)
    {
        var b = new byte[70];
        BinaryPrimitives.WriteInt32LittleEndian(b, number);
        BinaryPrimitives.WriteInt32LittleEndian(b.AsSpan(4), 0x485156);
        b[12] = (byte)(muted ? 0 : 1);
        BinaryPrimitives.WriteSingleLittleEndian(b.AsSpan(13), 1);
        return b;
    }

    /// <summary>
    /// A channel's sampler settings (event 215, 168 bytes) as FL 25 writes them for an audio clip, with its time
    /// stretching: Time at 96, pitch in cents at 100, the multiplier at 104 (10000 = twice as long), the mode at 108.
    /// </summary>
    public static byte[] ChannelParams(int time = 0, int cents = 0, int multiplier = 0, int mode = 1)
    {
        int[] v = [140, 0, 1, -1, 60, 1065353216, 1065353216, 1065353216, 1065353216, 1065353216, 0, 3, 0, 1024, 48, 65536, 1447, 0, 256, 0,
                   16777216, 0, 0, 1, time, cents, multiplier, mode, 2, -2, -1, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0];
        var b = new byte[v.Length * 4];
        for (var i = 0; i < v.Length; i++) BinaryPrimitives.WriteInt32LittleEndian(b.AsSpan(i * 4), v[i]);
        return b;
    }

    /// <summary>A channel's levels (event 219): pan and volume out of 12800, pitch in cents.</summary>
    /// <summary>
    /// A channel envelope (event 218) as FL 25 writes it, FL's defaults unless given: flags, on, delay, attack,
    /// hold, decay, sustain (0..128), release, then the LFO and the curves.
    /// </summary>
    public static byte[] Envelope(bool on = false, int attack = 20000, int sustain = 50, int release = 20000)
    {
        int[] v = [4, on ? 1 : 0, 100, attack, 20000, 30000, sustain, release, 0, 100, 20000, 0, 32950, 0, 0, 0, -101];
        var b = new byte[v.Length * 4];
        for (var i = 0; i < v.Length; i++) BinaryPrimitives.WriteInt32LittleEndian(b.AsSpan(i * 4), v[i]);
        return b;
    }

    public static byte[] Levels(int pan, int volume, int pitchCents)
    {
        var b = new byte[24];
        BinaryPrimitives.WriteInt32LittleEndian(b, pan);
        BinaryPrimitives.WriteInt32LittleEndian(b.AsSpan(4), volume);
        BinaryPrimitives.WriteInt32LittleEndian(b.AsSpan(8), pitchCents);
        BinaryPrimitives.WriteInt32LittleEndian(b.AsSpan(12), 256);
        return b;
    }

    /// <summary>An automation clip's points (event 234), each (beats since the previous point, value).</summary>
    public static byte[] AutomationPoints(params (double Step, double Value)[] points)
    {
        var b = new byte[21 + 24 * points.Length];
        BinaryPrimitives.WriteUInt32LittleEndian(b, 1);
        BinaryPrimitives.WriteUInt32LittleEndian(b.AsSpan(4), 64);
        BinaryPrimitives.WriteUInt32LittleEndian(b.AsSpan(9), 4);
        BinaryPrimitives.WriteUInt32LittleEndian(b.AsSpan(13), 3);
        BinaryPrimitives.WriteUInt32LittleEndian(b.AsSpan(17), (uint)points.Length);
        for (var k = 0; k < points.Length; k++)
        {
            BinaryPrimitives.WriteDoubleLittleEndian(b.AsSpan(21 + 24 * k), points[k].Step);
            BinaryPrimitives.WriteDoubleLittleEndian(b.AsSpan(29 + 24 * k), points[k].Value);
        }
        return b;
    }

    /// <summary>The mixer's settings (event 225): u32 0, u32 target, i32 value for each.</summary>
    public static byte[] MixerSettings(params (uint Target, int Value)[] settings)
    {
        var b = new byte[12 * settings.Length];
        for (var k = 0; k < settings.Length; k++)
        {
            BinaryPrimitives.WriteUInt32LittleEndian(b.AsSpan(12 * k + 4), settings[k].Target);
            BinaryPrimitives.WriteInt32LittleEndian(b.AsSpan(12 * k + 8), settings[k].Value);
        }
        return b;
    }

    /// <summary>What an automation clip moves (event 227): its channel, and the target (channel &lt;&lt; 16 | control).</summary>
    public static byte[] AutomationLink(ushort clip, uint target)
    {
        var b = new byte[20];
        BinaryPrimitives.WriteUInt16LittleEndian(b.AsSpan(2), clip);
        BinaryPrimitives.WriteUInt32LittleEndian(b.AsSpan(8), target);
        BinaryPrimitives.WriteUInt32LittleEndian(b.AsSpan(12), 8);
        return b;
    }

    public static byte[] Note(uint pos, ushort channel, uint length, ushort key, byte velocity, bool muted = false)
    {
        var b = new byte[24];
        BinaryPrimitives.WriteUInt32LittleEndian(b, pos);
        BinaryPrimitives.WriteUInt16LittleEndian(b.AsSpan(4), 0x4000);
        BinaryPrimitives.WriteUInt16LittleEndian(b.AsSpan(6), channel);
        BinaryPrimitives.WriteUInt32LittleEndian(b.AsSpan(8), length);
        BinaryPrimitives.WriteUInt16LittleEndian(b.AsSpan(12), key);
        if (muted) b[19] = 0x20;
        b[21] = velocity;
        return b;
    }

    /// <summary>
    /// Three channels (Serum VST on "Lead", a sampler "Kick", native 3x Osc "Bass"),
    /// two patterns with notes, and a mixer with Fruity Limiter and a Pro-Q 3 VST.
    /// </summary>
    public static string FlStudioProject(string dir, string name = "Night Drive", byte[]? playlist = null)
    {
        var b = new FlpBuilder()
            .Ascii(199, "21.2.3.4004")
            .Dword(156, 128_000)
            .Text(194, "Night Drive (title)")
            .Word(64, 0).Byte(21, 2)
            .Text(201, "Fruity Wrapper").Text(203, "Lead")
            .Data(213, WrapperBlob("Serum", @"C:\Program Files\VstPlugins\Serum_x64.dll", "Xfer Records", [1, 2, 3, 4]))
            .Word(64, 1).Byte(21, 0)
            .Text(203, "Kick").Text(196, @"C:\Samples\Drums\Kick 01.wav")
            .Word(64, 2).Byte(21, 2)
            .Text(201, "3x Osc").Text(203, "Bass").Data(213, [9, 9, 9])
            .Word(65, 1).Text(193, "Intro")
            .Data(224, [.. Note(0, 0, 96, 60, 100), .. Note(96, 0, 96, 64, 128), .. Note(0, 1, 24, 36, 100)])
            .Word(65, 2).Text(193, "Drop")
            .Data(224, Note(0, 2, 192, 41, 64))
            .Data(236, [0, 0, 0, 0])
            .Text(201, "Fruity Limiter").Data(213, [5, 5]).Word(98, 0)
            .Text(201, "Fruity Wrapper")
            .Data(213, WrapperBlob("Pro-Q 3", @"C:\VstPlugins\FabFilter Pro-Q 3.dll", "FabFilter", [7, 7]))
            .Word(98, 1);
        if (playlist is not null) b.Data(233, playlist);
        var path = Path.Combine(dir, name + ".flp");
        File.WriteAllBytes(path, b.Build());
        return path;
    }

    /// <summary>FL's VST2 state: a 21-byte header in front of the plugin's own chunk.</summary>
    public static byte[] FlVst2State(byte[] chunk, uint program = 0)
    {
        var b = new byte[21 + chunk.Length];
        BinaryPrimitives.WriteInt32LittleEndian(b, -9);
        b[4] = 0x0d;
        BinaryPrimitives.WriteInt32LittleEndian(b.AsSpan(5), -2);
        BinaryPrimitives.WriteUInt32LittleEndian(b.AsSpan(9), (uint)chunk.Length);
        BinaryPrimitives.WriteUInt32LittleEndian(b.AsSpan(17), program);
        chunk.CopyTo(b, 21);
        return b;
    }

    /// <summary>
    /// FL's VST2 state for a plugin without chunks: the 21-byte header (chunk length 0,
    /// flags without 0x08), the parameter count and values, then u32 1 and the program name.
    /// </summary>
    public static byte[] FlVst2ParamState(float[] values, string program)
    {
        using var ms = new MemoryStream();
        var header = new byte[21];
        BinaryPrimitives.WriteInt32LittleEndian(header, -9);
        header[4] = 0x05;
        BinaryPrimitives.WriteInt32LittleEndian(header.AsSpan(5), -2);
        ms.Write(header);
        ms.Write(BitConverter.GetBytes((uint)values.Length));
        foreach (var v in values) ms.Write(BitConverter.GetBytes(v));
        ms.Write(BitConverter.GetBytes(1u));
        var name = new byte[24];
        Encoding.ASCII.GetBytes(program).CopyTo(name, 0);
        ms.Write(name);
        return ms.ToArray();
    }

    /// <summary>
    /// FL 9's VST2 state: a 13-byte header (i32 -9, flags, u32 chunk length, u32 program) in front of the plugin's
    /// chunk, or, for a plugin without chunks, chunk length 0 and its parameters as in <see cref="FlVst2ParamState"/>.
    /// </summary>
    public static byte[] FlVst2StateFl9(byte[] chunk, uint program, float[]? values = null, string name = "")
    {
        using var ms = new MemoryStream();
        ms.Write(BitConverter.GetBytes(-9));
        ms.WriteByte(values is null ? (byte)0x0d : (byte)0x05);
        ms.Write(BitConverter.GetBytes((uint)chunk.Length));
        ms.Write(BitConverter.GetBytes(program));
        ms.Write(chunk);
        if (values is not null)
        {
            ms.Write(BitConverter.GetBytes((uint)values.Length));
            foreach (var v in values) ms.Write(BitConverter.GetBytes(v));
            ms.Write(BitConverter.GetBytes(1u));
            var text = new byte[24];
            Encoding.ASCII.GetBytes(name).CopyTo(text, 0);
            ms.Write(text);
        }
        return ms.ToArray();
    }

    /// <summary>
    /// Like Solomon's funk step_8 (FL 9.0.0, 2010): channel "Cell" plays KORG LegacyCell, whose 300-byte chunk sits
    /// behind FL 9's shorter header with program 42, and the mixer has Fruity Compressor saved as parameters.
    /// FL 9 keeps no plugin path or vendor, and its text is ASCII.
    /// </summary>
    public static string FlStudio9Project(string dir, byte[] chunk)
    {
        var b = new FlpBuilder()
            .Ascii(199, "9.0.0")
            .Dword(156, 98_000)
            .Word(64, 0).Byte(21, 2)
            .Ascii(201, "Fruity Wrapper").Ascii(203, "Cell")
            .Data(213, WrapperBlob("LegacyCell", "", "", FlVst2StateFl9(chunk, 42), version: 6, kind: 0, fourCc: "VCLK"))
            .Data(236, [0, 0, 0, 0])
            .Ascii(201, "Fruity Wrapper")
            .Data(213, WrapperBlob("Fruity Compressor", "", "", FlVst2StateFl9([], 0, [0.5f, 0.02f, 0.5f], "Full Reset"),
                                   version: 6, kind: 0, fourCc: "CFfU"))
            .Word(98, 0);
        var path = Path.Combine(dir, "funk step_8.flp");
        File.WriteAllBytes(path, b.Build());
        return path;
    }

    /// <summary>
    /// Like funk step_8's drums (FL 9.0.0): shuffle 36, and sampler "Kick" with step sequencer dots in pattern 1 kept in
    /// its channel events: steps 0, 5 (volume 52) and 7 (shifted 24). The playlist (FL 9's 32-byte items, row 999 - n
    /// for pattern n) plays pattern 1 from beat 8, one bar long.
    /// </summary>
    public static string FlStudio9Steps(string dir, bool pattern4AtStart = false)
    {
        // Pattern 1 (row 998) at beat 8; with pattern4AtStart, pattern 4 (row 995) at the start too.
        var item = new byte[pattern4AtStart ? 64 : 32];
        for (var off = 0; off < item.Length; off += 32)
        {
            BinaryPrimitives.WriteUInt32LittleEndian(item.AsSpan(off), off == 0 ? 768u : 0);
            BinaryPrimitives.WriteUInt16LittleEndian(item.AsSpan(off + 6), 0x6000);
            BinaryPrimitives.WriteUInt32LittleEndian(item.AsSpan(off + 8), 384);
            BinaryPrimitives.WriteUInt16LittleEndian(item.AsSpan(off + 12), (ushort)(off == 0 ? 998 : 995));
            BinaryPrimitives.WriteSingleLittleEndian(item.AsSpan(off + 24), -1);
            BinaryPrimitives.WriteSingleLittleEndian(item.AsSpan(off + 28), -1);
        }
        var b = new FlpBuilder()
            .Ascii(199, "9.0.0").Byte(11, 36).Dword(156, 140_000)
            .Word(64, 0).Byte(21, 0).Ascii(192, "Kick").Ascii(196, @"C:\Samples\Kick.wav")
            .Word(65, 1).Word(72, 5 | 52 << 8).Word(91, 0x100).Word(91, 0x105).Word(92, 7 | 24 << 8).Word(91, 0x107)
            .Data(233, item);
        var path = Path.Combine(dir, "funk step_8.flp");
        File.WriteAllBytes(path, b.Build());
        return path;
    }

    /// <summary>
    /// Like Solomon's funk step_9 (funk step_8 saved again in FL 25.2.5): KORG LegacyCell on "Cell" gave FL an empty
    /// chunk, so its state is FL's 21-byte header with chunk length 0, then 8 zero bytes. A pattern plays it in bar 1.
    /// </summary>
    public static string FlStudio25NothingSaved(string dir)
    {
        var b = new FlpBuilder()
            .Ascii(199, "25.2.5.5319").Dword(159, 5319).Byte(28, 1).Raw(172, 1, 1, 0)
            .Text(192, "FL Studio 25.2.5.5319").Dword(156, 98_000)
            .Word(65, 1).Data(224, Note(0, 0, 96, 60, 100))
            .Word(64, 0).Byte(21, 2)
            .Text(201, "Fruity Wrapper").Text(203, "Cell")
            .Data(213, WrapperBlob("LegacyCell", @"C:\Program Files\VstPlugins\KORG\LegacyCell.dll", "KORG",
                                   [.. FlVst2State([]), .. new byte[8]], version: 12, kind: 4, fourCc: "VCLK"))
            .Data(233, PlaylistItem25(0, 20481, 384));
        var path = Path.Combine(dir, "funk step_9.flp");
        File.WriteAllBytes(path, b.Build());
        return path;
    }

    /// <summary>FL's VST3 state: version 1, then records 1 (settings), 3 (component), 2 (controller).</summary>
    public static byte[] FlVst3State(byte[] component, byte[] controller, uint[]? parameterIds = null)
    {
        using var ms = new MemoryStream();
        ms.Write(BitConverter.GetBytes(1u));
        void Rec(uint id, byte[] data)
        {
            ms.Write(BitConverter.GetBytes(id));
            ms.Write(BitConverter.GetBytes((ulong)data.Length));
            ms.Write(data);
        }
        Rec(1, new byte[64]);
        Rec(3, component);
        Rec(2, controller);
        if (parameterIds is not null) Rec(4, [.. BitConverter.GetBytes((uint)parameterIds.Length), .. parameterIds.SelectMany(BitConverter.GetBytes)]);
        return ms.ToArray();
    }

    /// <summary>
    /// An FL Studio 25 project inside "FL Studio/Projects": wrapper state version 12 with a
    /// VST2 (Serum) and a VST3 (Arcade), and a sample under %FLStudioUserData%.
    /// </summary>
    public static string FlStudio25Project(string dir, bool withNotes = false)
    {
        var projects = Path.Combine(dir, "FL Studio", "Projects");
        Directory.CreateDirectory(projects);
        File.WriteAllBytes(Path.Combine(projects, "Snare 01.wav"), [0]);
        byte[] arcadeId = [0x41, 0x54, 0x53, 0x56, 0x43, 0x52, 0x61, 0x44, 0x72, 0x63, 0x61, 0x64, 0x65, 0, 0, 0];
        var b = new FlpBuilder()
            .Ascii(199, "25.1.0.4868")
            .Dword(156, 126_000)
            .Word(64, 0).Byte(21, 0)
            .Text(203, "Snare").Text(196, @"%FLStudioUserData%\Projects\Snare 01.wav")
            .Word(64, 1).Byte(21, 2)
            .Text(201, "Fruity Wrapper").Text(203, "Lead")
            .Data(213, WrapperBlob("Serum", @"C:\Program Files\VstPlugins\Xfer\Serum_x64.dll", "Xfer Records",
                                   FlVst2State([1, 2, 3]), version: 12, kind: 4, fourCc: "XsfX"))
            .Word(64, 2).Byte(21, 2)
            .Text(201, "Fruity Wrapper").Text(203, "Keys")
            .Data(213, WrapperBlob("Arcade", @"C:\Program Files\Common Files\VST3\Arcade.vst3", "Output",
                                   FlVst3State([7, 7, 7], [8], parameterIds: [10, 20, 2000003]), version: 12, kind: 8, classId: arcadeId))
            .Word(64, 3).Byte(21, 2)
            .Text(201, "Fruity Wrapper").Text(203, "Arp")
            .Data(213, WrapperBlob("Predator 2", @"C:\VstPlugins\Predator2.dll", "Rob Papen",
                                   FlVst2ParamState([0.25f, 1f, 0.5f], "arp- sine beat"), version: 12, kind: 0, fourCc: "2drP"));
        if (withNotes) b.Word(65, 1).Data(224, [.. Note(0, 1, 96, 60, 100), .. Note(0, 2, 96, 64, 100)]);
        var path = Path.Combine(projects, "Shuffle.flp");
        File.WriteAllBytes(path, b.Build());
        return path;
    }

    /// <summary>
    /// Like Solomon's FL 25 test of mutes and steps: pattern "Beat" has a sampler "Snare" playing two half-beat
    /// notes, the second muted in the piano roll, and a sampler "Kick" with four steps from the step sequencer
    /// (no length) on beats 1, 2, 3 and 4½. It's in the playlist in bar 1, then again in bar 2, muted. The kick's
    /// volume envelope is on (attack 2000, sustain 25%, release 40000); only the snare's pan envelope is.
    /// </summary>
    public static string FlStudio25MutesAndSteps(string dir)
    {
        var b = new FlpBuilder()
            .Ascii(199, "25.2.5.5319").Dword(159, 5319).Byte(28, 1).Raw(172, 1, 1, 0)
            .Text(192, "FL Studio 25.2.5.5319").Dword(156, 140_000)
            .Word(65, 1).Text(193, "Beat")
            .Data(224, [.. Note(0, 0, 48, 72, 100), .. Note(96, 0, 48, 72, 100, muted: true),
                        .. Note(0, 1, 0, 60, 100), .. Note(96, 1, 0, 60, 100), .. Note(192, 1, 0, 60, 100), .. Note(336, 1, 0, 60, 100)])
            .Word(64, 0).Byte(21, 0).Text(201, "").Text(203, "Snare").Text(196, @"C:\Samples\Snare 01.wav")
            .Data(218, Envelope(on: true)).Data(218, Envelope()).Data(218, Envelope()).Data(218, Envelope()).Data(218, Envelope())
            .Word(64, 1).Byte(21, 0).Text(201, "").Text(203, "Kick").Text(196, @"C:\Samples\Kick 01.wav")
            .Data(218, Envelope()).Data(218, Envelope(on: true, attack: 2000, sustain: 32, release: 40000))
            .Data(218, Envelope()).Data(218, Envelope()).Data(218, Envelope())
            .Data(233, [.. PlaylistItem25(0, 20481, 384), .. PlaylistItem25(384, 20481, 384, muted: true)]);
        var path = Path.Combine(dir, "Mutes and steps.flp");
        File.WriteAllBytes(path, b.Build());
        return path;
    }

    /// <summary>
    /// An FL 25 song laid out like FL writes it, at 132 bpm: a sampler "Snare" (root key A4, pitch -2 semitones,
    /// pan 25%, volume 100%) with an automation clip on its volume in bar 2, and a Serum VST (root
    /// key E5) with an automation clip on parameter 205 (three points) in bar 1. The pattern sits in bar 2. The volume's
    /// automation clip can be switched off in the Channel Rack.
    /// </summary>
    public static string FlStudio25Automation(string dir, bool volumePlacedTwice = false, bool volumeOff = false)
    {
        var b = new FlpBuilder()
            .Ascii(199, "25.2.5.5319").Dword(159, 5319).Byte(28, 1)
            .Raw(172, 1, 1, 0)   // as FL 25 writes it: three bytes, not four
            .Text(192, "FL Studio 25.2.5.5319").Dword(156, 132_000)
            .Word(65, 1).Data(224, [.. Note(0, 0, 48, 72, 100), .. Note(0, 2, 96, 60, 100)])
            .Data(227, AutomationLink(1, 0))
            .Data(227, AutomationLink(3, 2u << 16 | 0x80cd))
            .Word(64, 0).Byte(21, 0).Text(201, "").Text(203, "Snare")
            .Dword(135, 57).Data(219, Levels(3200, 12800, -200))
            .Text(196, @"C:\Samples\Snare 01.wav")
            .Word(64, 1).Byte(21, 5).Text(201, "").Text(203, "Snare - Channel volume").Byte(0, (byte)(volumeOff ? 0 : 1))
            .Data(219, Levels(0, 12800, 0)).Data(234, AutomationPoints((0, 0), (4, 1)))
            .Word(64, 2).Byte(21, 2)
            .Text(201, "Fruity Wrapper").Text(203, "Serum")
            .Data(213, WrapperBlob("Serum", @"C:\VstPlugins\Serum_x64.dll", "Xfer Records",
                                   FlVst2State([1, 2, 3]), version: 12, kind: 4, fourCc: "XfsX"))
            .Dword(135, 64)
            .Word(64, 3).Byte(21, 5).Text(201, "").Text(203, "Serum - Cutoff")
            .Data(234, AutomationPoints((0, 0.2), (2, 1), (2, 0.5)))
            .Data(233, [.. PlaylistItem25(384, 20481, 384), .. PlaylistItem25(384, 1, 384, 498), .. PlaylistItem25(0, 3, 384, 497),
                        .. volumePlacedTwice ? PlaylistItem25(1152, 1, 384, 498) : []]);
        var path = Path.Combine(dir, "Automated.flp");
        File.WriteAllBytes(path, b.Build());
        return path;
    }

    /// <summary>
    /// Like Solomon's FL 25 test of audio clips, at 120 bpm with a 32 second sample, "Drop.wav", in the playlist on
    /// four audio clip channels: as it is (bar 1, 16 bars), stretched to twice as long (Mul, bar 17, 32 bars), stretched
    /// and pitched up 3 semitones (bar 49, 32 bars), and pitched up 3 semitones and trimmed to its second half (bar 81,
    /// 8 bars). A fifth channel resamples (pitch and length together) at twice as long, plays backwards and its clip
    /// in bar 89 is muted. An automation clip moves the first channel's volume, and a pattern of notes plays a Sampler.
    /// </summary>
    public static string FlStudio25AudioClips(string dir)
    {
        const string drop = @"C:\Samples\Drop.wav";
        var b = new FlpBuilder()
            .Ascii(199, "25.2.5.5319").Dword(159, 5319).Byte(28, 1).Raw(172, 1, 1, 0)
            .Text(192, "FL Studio 25.2.5.5319").Dword(156, 120_000)
            .Word(65, 1).Data(224, [.. Note(0, 0, 96, 60, 100)])
            .Data(227, AutomationLink(6, 1u << 16))
            .Word(64, 0).Byte(21, 0).Text(201, "").Text(203, "Kick").Text(196, @"C:\Samples\Kick 01.wav");
        void Audio(int channel, string name, byte[] parameters, ushort fx = 0) =>
            b.Word(64, (ushort)channel).Byte(21, 4).Byte(0, 1).Word(70, fx).Data(219, Levels(6400, 10000, 0)).Data(215, parameters)
             .Text(203, name).Text(196, drop);
        Audio(1, "Drop", ChannelParams());
        Audio(2, "Drop stretched", ChannelParams(multiplier: 10000));
        Audio(3, "Drop stretched up", ChannelParams(cents: 300, multiplier: 10000));
        Audio(4, "Drop trimmed up", ChannelParams(cents: 300));
        Audio(5, "Drop resampled", ChannelParams(multiplier: 10000, mode: 0), fx: 0x2);
        b.Word(64, 6).Byte(21, 5).Text(201, "").Text(203, "Drop - Volume")
         .Data(219, Levels(0, 12800, 0)).Data(234, AutomationPoints((0, 0), (4, 1)))
         .Data(233, [.. PlaylistItem25(0, 1, 6144), .. PlaylistItem25(0, 6, 384, 498), .. PlaylistItem25(0, 20481, 384, 497),
                     .. PlaylistItem25(6144, 2, 12288), .. PlaylistItem25(18432, 3, 12288),
                     .. PlaylistItem25(30720, 4, 3072, from: 16000, to: 32000), .. PlaylistItem25(33792, 5, 3072, muted: true)]);
        var path = Path.Combine(dir, "Audio clips.flp");
        File.WriteAllBytes(path, b.Build());
        return path;
    }

    /// <summary>
    /// Like Solomon's civil_18 and its Serum: an FL 25 song at 120 bpm whose "Bass" (Serum, routed to insert 1) is switched
    /// off in the Channel Rack, but an automation clip switches it on and off: off for beats 0-4, on 4-8, off 8-12, then on,
    /// its last points a few millionths of a beat past the clip's end as FL saves them. Pattern "Bass" (notes on beats 1
    /// and 3) plays at beats 0, 4 and 10. The "Vox" audio clip channel is switched off from beat 2 by another, and plays
    /// clips at beats 0-4 and 4-6. Insert 1 has Pro-Q 3 in slot 0 and WOW2, switched off, in slot 1; its fader is moved.
    /// Automation clips also move Pro-Q 3's fourth setting, the tempo and insert 1's fader. FL 25 numbers its mixer
    /// settings from 0x7000. With <paramref name="more"/>: a second clip switches the bass off at beat 16 and on at 20
    /// (the pattern plays at 16 and 20 too), another switches WOW2 on and off, and the bass volume rises 0 to 1 over
    /// beats 0-4 and jumps back to 0 at the clip's end, placed twice in a row; and playlist track 3, the vox's, is muted.
    /// </summary>
    public static string FlStudio25SwitchedAndMixer(string dir, bool more = false)
    {
        const uint insert1 = 0x7040u << 16;
        var b = new FlpBuilder()
            .Ascii(199, "25.2.5.5319").Dword(159, 5319).Byte(28, 1).Raw(172, 1, 1, 0)
            .Text(192, "FL Studio 25.2.5.5319").Dword(156, 120_000)
            .Word(65, 1).Text(193, "Bass").Data(224, [.. Note(0, 0, 48, 36, 100), .. Note(192, 0, 48, 38, 100)])
            .Data(227, AutomationLink(1, 0u << 16 | 7))
            .Data(227, AutomationLink(3, 2u << 16 | 7))
            .Data(227, AutomationLink(4, insert1 | 0x8003))
            .Data(227, AutomationLink(5, 0x40000005))
            .Data(227, AutomationLink(6, insert1 | 0x1fc0));
        if (more)
            b.Data(227, AutomationLink(7, 0u << 16 | 7)).Data(227, AutomationLink(8, 0x7041u << 16 | 0x1f00)).Data(227, AutomationLink(9, 0u << 16 | 0));
        b
            .Word(64, 0).Byte(21, 2).Byte(0, 0).Word(104, 1)
            .Text(201, "Fruity Wrapper").Text(203, "Bass")
            .Data(213, WrapperBlob("Serum", @"C:\VstPlugins\Serum_x64.dll", "Xfer Records", FlVst2State([1, 2, 3]), version: 12, kind: 4, fourCc: "XfsX"))
            .Word(64, 1).Byte(21, 5).Byte(0, 1).Text(201, "").Text(203, "Bass - Mute / solo")
            .Data(234, AutomationPoints((0, 0), (4, 1), (4, 0), (4.000008, 0), (0, 0.97)))
            .Word(64, 2).Byte(21, 4).Byte(0, 1).Data(215, ChannelParams()).Text(203, "Vox").Text(196, @"C:\Samples\Vox.wav")
            .Word(64, 3).Byte(21, 5).Byte(0, 1).Text(201, "").Text(203, "Vox - Mute / solo")
            .Data(234, AutomationPoints((0, 1), (2, 0)))
            .Word(64, 4).Byte(21, 5).Byte(0, 1).Text(201, "").Text(203, "Pro-Q 3 - Gain").Data(234, AutomationPoints((0, 0.25), (4, 0.75)))
            .Word(64, 5).Byte(21, 5).Byte(0, 1).Text(201, "").Text(203, "Tempo").Data(234, AutomationPoints((0, 0.5), (4, 1)))
            .Word(64, 6).Byte(21, 5).Byte(0, 1).Text(201, "").Text(203, "Insert 1 - Volume").Data(234, AutomationPoints((0, 0.8), (4, 0.2)));
        if (more)
            b.Word(64, 7).Byte(21, 5).Byte(0, 1).Text(201, "").Text(203, "Bass - Mute / solo #2").Data(234, AutomationPoints((0, 0), (4, 1)))
             .Word(64, 8).Byte(21, 5).Byte(0, 1).Text(201, "").Text(203, "WOW2 - On").Data(234, AutomationPoints((0, 1), (8, 0)))
             .Word(64, 9).Byte(21, 5).Byte(0, 1).Text(201, "").Text(203, "Bass - Volume").Data(234, AutomationPoints((0, 0), (4, 1), (0, 0)));
        b
            .Data(236, [0, 0, 0, 0])
            .Data(236, [0, 0, 0, 0])
            .Text(201, "Fruity Wrapper")
            .Data(213, WrapperBlob("Pro-Q 3", @"C:\VstPlugins\FabFilter Pro-Q 3.dll", "FabFilter", FlVst2State([4]), version: 12, kind: 4, fourCc: "FQ3p"))
            .Word(98, 0)
            .Text(201, "Fruity Wrapper")
            .Data(213, WrapperBlob("WOW2", @"C:\VstPlugins\WOW2.dll", "Sugar Bytes", FlVst2State([5]), version: 12, kind: 4, fourCc: "SBw2"))
            .Word(98, 1)
            .Data(236, [0, 0, 0, 0])
            .Data(225, MixerSettings((0x40000000, 12800),
                                     (0x7000u << 16 | 0x1fc0, 12800), (0x7000u << 16 | 0x1fc1, 0),
                                     (insert1 | 0x1fc0, 9600), (insert1 | 0x1fc1, 0), (insert1 | 0x1f00, 1), (0x7041u << 16 | 0x1f00, 0),
                                     (0x7080u << 16 | 0x1fc0, 12800), (0x7080u << 16 | 0x1fc1, 0)))
            .Data(233, [.. PlaylistItem25(0, 20481, 384), .. PlaylistItem25(384, 20481, 384), .. PlaylistItem25(960, 20481, 384),
                        .. PlaylistItem25(0, 1, 1152, track: 498), .. PlaylistItem25(0, 2, 384, track: 497), .. PlaylistItem25(384, 2, 192, track: 497),
                        .. PlaylistItem25(0, 3, 768, track: 496), .. PlaylistItem25(0, 4, 384, track: 495), .. PlaylistItem25(0, 5, 384, track: 494),
                        .. PlaylistItem25(0, 6, 384, track: 493),
                        .. more ? [.. PlaylistItem25(1536, 20481, 384), .. PlaylistItem25(1920, 20481, 384), .. PlaylistItem25(1536, 7, 768, track: 492),
                                   .. PlaylistItem25(0, 8, 1536, track: 491), .. PlaylistItem25(0, 9, 384, track: 490), .. PlaylistItem25(384, 9, 384, track: 490)]
                                : Array.Empty<byte>()]);
        if (more)
            for (var n = 1; n <= 500; n++) b.Data(238, PlaylistTrack(n, muted: n == 3));
        var path = Path.Combine(dir, "Switched.flp");
        File.WriteAllBytes(path, b.Build());
        return path;
    }

    /// <summary>
    /// An FL 25 song whose "Kick" pattern plays in bars 1, 2 and 3 while two clips switch the kick, back to back: "Kick off"
    /// (off throughout) in bars 1 and 3, "Kick on" (on throughout) in bar 2. <paramref name="onLinkedFirst"/> swaps the order
    /// of their links in the file. "Kick - Volume" (a rise from 0 to 1 over 8 beats) is placed at beat 0 and again at beat 4,
    /// over the first one's second half. "Kick - Pan" (the same rise) is placed at beats 0-8, then again at 2-4 inside it;
    /// "Kick - Pitch" (the same rise) at 0-8, then again at 0-4.
    /// </summary>
    public static string FlStudio25BackToBack(string dir, bool onLinkedFirst)
    {
        var b = new FlpBuilder()
            .Ascii(199, "25.2.5.5319").Dword(159, 5319).Byte(28, 1).Raw(172, 1, 1, 0)
            .Text(192, "FL Studio 25.2.5.5319").Dword(156, 120_000)
            .Word(65, 1).Text(193, "Kick").Data(224, Note(0, 0, 48, 36, 100));
        var (off, on) = (AutomationLink(1, 0u << 16 | 7), AutomationLink(2, 0u << 16 | 7));
        b.Data(227, onLinkedFirst ? on : off).Data(227, onLinkedFirst ? off : on).Data(227, AutomationLink(3, 0u << 16 | 0))
            .Data(227, AutomationLink(4, 0u << 16 | 1)).Data(227, AutomationLink(5, 0u << 16 | 4))
            .Word(64, 0).Byte(21, 0).Text(201, "").Text(203, "Kick").Byte(0, 1).Text(196, @"C:\Samples\Kick.wav")
            .Word(64, 1).Byte(21, 5).Byte(0, 1).Text(201, "").Text(203, "Kick off").Data(234, AutomationPoints((0, 0), (4, 0)))
            .Word(64, 2).Byte(21, 5).Byte(0, 1).Text(201, "").Text(203, "Kick on").Data(234, AutomationPoints((0, 1), (4, 1)))
            .Word(64, 3).Byte(21, 5).Byte(0, 1).Text(201, "").Text(203, "Kick - Volume").Data(234, AutomationPoints((0, 0), (8, 1)))
            .Word(64, 4).Byte(21, 5).Byte(0, 1).Text(201, "").Text(203, "Kick - Pan").Data(234, AutomationPoints((0, 0), (8, 1)))
            .Word(64, 5).Byte(21, 5).Byte(0, 1).Text(201, "").Text(203, "Kick - Pitch").Data(234, AutomationPoints((0, 0), (8, 1)))
            .Data(233, [.. PlaylistItem25(0, 20481, 384), .. PlaylistItem25(384, 20481, 384), .. PlaylistItem25(768, 20481, 384),
                        .. PlaylistItem25(0, 1, 384, track: 498), .. PlaylistItem25(384, 2, 384, track: 498), .. PlaylistItem25(768, 1, 384, track: 498),
                        .. PlaylistItem25(0, 3, 768, track: 497), .. PlaylistItem25(384, 3, 768, track: 496),
                        .. PlaylistItem25(0, 4, 768, track: 495), .. PlaylistItem25(192, 4, 192, track: 494),
                        .. PlaylistItem25(0, 5, 768, track: 493), .. PlaylistItem25(0, 5, 384, track: 492)]);
        var path = Path.Combine(dir, "Back to back.flp");
        File.WriteAllBytes(path, b.Build());
        return path;
    }

    /// <summary>
    /// Like Solomon's civil_18: an FL 25 song whose playlist tracks 2 ("Acapellas") and 4 are muted, and whose "Hat" and
    /// "Vox" channels are switched off in the Channel Rack. Pattern "Verse" (a snare note and a hat note) is on track 1
    /// in bar 1 and on track 2 in bar 2. "Acapella" audio clips are on track 2 (bars 1 and 2) and track 4 (bar 3); "Vox"
    /// clips on tracks 1 and 3 overlap. Pattern "Pad" is only on track 4 (bar 3). A second arrangement mutes its own
    /// track 1. As FL 25 does, the project has an event 0 of its own (0) before the first channel.
    /// </summary>
    public static string FlStudio25MutedTracks(string dir)
    {
        var b = new FlpBuilder()
            .Ascii(199, "25.2.5.5319").Dword(159, 5319).Byte(28, 1).Raw(172, 1, 1, 0)
            .Text(192, "FL Studio 25.2.5.5319").Dword(156, 120_000).Byte(0, 0)
            .Word(65, 1).Text(193, "Verse").Data(224, [.. Note(0, 0, 96, 60, 100), .. Note(0, 1, 48, 66, 100)])
            .Word(65, 2).Text(193, "Pad").Data(224, Note(0, 4, 192, 64, 100))
            .Word(64, 0).Byte(21, 0).Text(201, "").Text(203, "Snare").Byte(0, 1).Text(196, @"C:\Samples\Snare 01.wav")
            .Word(64, 1).Byte(21, 0).Text(201, "").Text(203, "Hat").Byte(0, 0).Text(196, @"C:\Samples\Hat 01.wav")
            .Word(64, 2).Byte(21, 4).Byte(0, 1).Data(215, ChannelParams()).Text(203, "Acapella").Text(196, @"C:\Samples\Acapella.wav")
            .Word(64, 3).Byte(21, 4).Byte(0, 0).Data(215, ChannelParams()).Text(203, "Vox").Text(196, @"C:\Samples\Vox.wav")
            .Word(64, 4).Byte(21, 0).Text(201, "").Text(203, "Pad").Byte(0, 1).Text(196, @"C:\Samples\Pad.wav")
            .Word(99, 0).Text(241, "Arrangement")
            .Data(233, [.. PlaylistItem25(0, 20481, 384), .. PlaylistItem25(384, 20481, 384, track: 498),
                        .. PlaylistItem25(0, 2, 768, track: 498), .. PlaylistItem25(768, 2, 384, track: 496),
                        .. PlaylistItem25(0, 3, 384), .. PlaylistItem25(192, 3, 384, track: 497), .. PlaylistItem25(768, 20482, 384, track: 496)]);
        for (var n = 1; n <= 500; n++)
        {
            b.Data(238, PlaylistTrack(n, muted: n is 2 or 4));
            if (n == 2) b.Text(239, "Acapellas");
        }
        b.Word(99, 1).Text(241, "Arrangement 2").Data(233, PlaylistItem25(0, 20481, 384));
        for (var n = 1; n <= 500; n++) b.Data(238, PlaylistTrack(n, muted: n == 1));
        var path = Path.Combine(dir, "civil_18.flp");
        File.WriteAllBytes(path, b.Build());
        return path;
    }

    // ---- Studio One ------------------------------------------------------------

    public static string StudioOneSong(string dir, string name = "Sunday Song")
    {
        var path = Path.Combine(dir, name + ".song");
        using var zip = ZipFile.Open(path, ZipArchiveMode.Create);
        void Add(string entry, string text)
        {
            using var w = new StreamWriter(zip.CreateEntry(entry).Open());
            w.Write(text);
        }
        Add("metainfo.xml", """
            <?xml version="1.0" encoding="UTF-8"?>
            <MetaInformation>
              <Attribute id="Document:Generator" value="Studio One/6.5.2"/>
              <Attribute id="Media:Title" value="Sunday Song"/>
              <Attribute id="Media:Tempo" value="92"/>
            </MetaInformation>
            """);
        Add("Song/mediapool.xml", """
            <?xml version="1.0" encoding="UTF-8"?>
            <MediaPool>
              <Attributes x:id="rootFolder">
                <AudioClip mediaType="Audio"><Url x:id="path" url="file:///C:/Users/Sol/Samples/Vox%20Chop.wav"/></AudioClip>
                <AudioClip mediaType="Audio"><Url x:id="path" url="file:///C:/Users/Sol/Samples/Rim.aif"/></AudioClip>
              </Attributes>
            </MediaPool>
            """);
        Add("Devices/audiosynthfolder.xml", """
            <?xml version="1.0" encoding="UTF-8"?>
            <SynthFolder>
              <Attributes x:id="ghostData" name="Inst 1">
                <Attributes x:id="deviceData" name="Diva" classID="{ABC}" vendor="u-he" deviceClass="AudioSynth VST3"/>
              </Attributes>
              <Attributes x:id="ghostData" name="Inst 2">
                <Attributes x:id="deviceData" name="Mai Tai" classID="{DEF}" vendor="PreSonus"/>
              </Attributes>
            </SynthFolder>
            """);
        Add("Devices/audiomixer.xml", """
            <?xml version="1.0" encoding="UTF-8"?>
            <AudioMixer>
              <Attributes x:id="deviceData" name="Pro-C 2" classID="{123}" vendor="FabFilter" deviceClass="AudioEffect VST3"/>
            </AudioMixer>
            """);
        Add("Song/song.xml", """
            <?xml version="1.0" encoding="UTF-8"?>
            <Song>
              <Tracks>
                <MusicTrack name="Keys">
                  <Events>
                    <MusicPart name="Verse Chords">
                      <NoteEvent start="0" length="1" pitch="60" velocity="0.8"/>
                      <NoteEvent start="1" length="0.5" pitch="64" velocity="1"/>
                    </MusicPart>
                  </Events>
                </MusicTrack>
                <AudioTrack name="Vocals"/>
              </Tracks>
            </Song>
            """);
        using (var s = zip.CreateEntry("Presets/Synths/Diva/Inst 1.vstpreset").Open())
            s.Write("VST3 fake preset"u8);
        return path;
    }

    // ---- Bitwig ------------------------------------------------------------------

    /// <summary>A file that only looks like Bitwig's format, for the text-scan fallback.</summary>
    public static string BitwigUndecodable(string dir, string name = "Loop Idea")
    {
        var projDir = Path.Combine(dir, name);
        Directory.CreateDirectory(Path.Combine(projDir, "samples"));
        File.WriteAllBytes(Path.Combine(projDir, "samples", "Recorded Bass.wav"), [0]);

        using var ms = new MemoryStream();
        ms.Write("BtWg00010001"u8);
        ms.Write([0, 1, 2, 0xFF, 0x10]);
        ms.Write(Encoding.ASCII.GetBytes("application_version_name\0\0\x05" + "5.2.4"));
        ms.Write([0, 0, 0x07]);
        ms.Write(Encoding.ASCII.GetBytes("Polymer"));
        ms.Write([0, 0, 0x03]);
        ms.Write(Encoding.ASCII.GetBytes("Delay+"));
        ms.Write([0, 0x10]);
        ms.Write(Encoding.Unicode.GetBytes("Analog Rytm MKII"));
        ms.Write([0, 0, 0x22]);
        ms.Write(Encoding.ASCII.GetBytes(@"C:\Program Files\Common Files\VST3\Valhalla Room.vst3"));
        ms.Write([0, 0, 0x05]);
        ms.Write(Encoding.ASCII.GetBytes("Serum"));
        ms.Write([0, 0]);
        // Bitwig writes a string's length just before it, and a length that is a readable character
        // joins the text: ">" (62) before this drive path, "-" (45) before the path in the project folder.
        // A bare name is a sound inside a multisample or a Grid's sample list.
        foreach (var text in new[] { $@"F:\Bitwig Studio\Projects\{name}\recordings\Syntakt 2-1.wav", "recordings/Untitled Project [0m27s-1m22s].wav",
                                     "BASSDRUM_0002 from the Kick Selector.wav" })
        {
            ms.Write([0, 0, 0, (byte)text.Length]);
            ms.Write(Encoding.ASCII.GetBytes(text));
        }
        ms.Write([0, 0]);
        // A compressed section, as some Bitwig versions store parts of the project that way.
        using (var z = new MemoryStream())
        {
            using (var zl = new ZLibStream(z, CompressionLevel.Optimal, leaveOpen: true))
                zl.Write(Encoding.ASCII.GetBytes("\0\0\x15" + @"D:\Splice\Snare 808.wav" + "\0\0"));
            ms.Write(z.ToArray());
        }
        ms.Write([0, 0, 0, 0]);
        var path = Path.Combine(projDir, name + ".bwproject");
        File.WriteAllBytes(path, ms.ToArray());
        return path;
    }

    /// <summary>Writes Bitwig's BtWg object stream (big-endian, typed fields).</summary>
    public sealed class BwWriter
    {
        public readonly MemoryStream S = new();
        int _objects;

        public void U32(long v) { Span<byte> b = stackalloc byte[4]; BinaryPrimitives.WriteUInt32BigEndian(b, (uint)v); S.Write(b); }
        void Type(int fid, byte type) { U32(fid); S.WriteByte(type); }

        public void StrVal(string s, bool utf16 = false)
        {
            if (utf16)
            {
                U32(0x8000_0000 | (uint)s.Length);
                S.Write(Encoding.BigEndianUnicode.GetBytes(s));
            }
            else
            {
                var b = Encoding.UTF8.GetBytes(s);
                U32(b.Length);
                S.Write(b);
            }
        }

        /// <summary>Starts an object; returns its 1-based index for references.</summary>
        public int Begin(int cls) { U32(cls); return ++_objects; }
        public void End() => U32(0);
        public void Str(int fid, string s, bool utf16 = false) { Type(fid, 0x08); StrVal(s, utf16); }
        public void U8(int fid, byte v) { Type(fid, 0x01); S.WriteByte(v); }
        public void Bool(int fid, bool v) { Type(fid, 0x05); S.WriteByte(v ? (byte)1 : (byte)0); }
        public void F64(int fid, double v) { Type(fid, 0x07); Span<byte> b = stackalloc byte[8]; BinaryPrimitives.WriteDoubleBigEndian(b, v); S.Write(b); }
        public void Id(int fid) { Type(fid, 0x15); S.Write(Guid.NewGuid().ToByteArray()); }
        public void Null(int fid) => Type(fid, 0x0a);
        public void Ints(int fid, params int[] v) { Type(fid, 0x0f); U32(v.Length); foreach (var i in v) U32(i); }
        public void Obj(int fid) => Type(fid, 0x09);
        public void Ref(int fid, int index) { Type(fid, 0x09); U32(1); U32(index); }
        public void List(int fid) => Type(fid, 0x12);
        public void ListRef(int index) { U32(1); U32(index); }
        public void EndList() => U32(3);
        public void Map(int fid) => Type(fid, 0x14);
        public void MapKey(string key) { S.WriteByte(1); StrVal(key); }
        public void EndMap() => S.WriteByte(0);
        public void Named(int fid) => Type(fid, 0x1a);
        /// <summary>A reference written as a bare object number (type 0x0b), as Bitwig does for objects it already wrote.</summary>
        public void BareRef(int fid, int index) { Type(fid, 0x0b); U32(index); }
        public void Uuid(int fid, string id) { Type(fid, 0x15); S.Write(Guid.Parse(id).ToByteArray(bigEndian: true)); }
        public void I16(int fid, short v) { Type(fid, 0x02); Span<byte> b = stackalloc byte[2]; BinaryPrimitives.WriteInt16BigEndian(b, v); S.Write(b); }
        public void I32(int fid, int v) { Type(fid, 0x03); U32((uint)v); }
        public void Blob(int fid, byte[] v) { Type(fid, 0x0d); U32(v.Length); S.Write(v); }
        /// <summary>A list of floats (type 0x17), as FM-4 saves its modulation matrix.</summary>
        public void Floats(int fid, params float[] v)
        {
            Type(fid, 0x17);
            U32(v.Length);
            Span<byte> b = stackalloc byte[4];
            foreach (var f in v) { BinaryPrimitives.WriteSingleBigEndian(b, f); S.Write(b); }
        }
        /// <summary>A list of doubles (type 0x18), as Bitwig 6.1 saves a Sampler sample's analysis.</summary>
        public void Doubles(int fid, params double[] v)
        {
            Type(fid, 0x18);
            U32(v.Length);
            Span<byte> b = stackalloc byte[8];
            foreach (var f in v) { BinaryPrimitives.WriteDoubleBigEndian(b, f); S.Write(b); }
        }

        public static byte[] File(Action<BwWriter> meta, Action<BwWriter> body, byte[]? zip)
        {
            var m = new BwWriter();
            m.U32(4);
            m.StrVal("meta");
            meta(m);
            m.U32(0);
            var b = new BwWriter();
            body(b);

            var bodyOffset = 42 + (int)m.S.Length + 8;
            var zipOffset = zip is null ? 0 : bodyOffset + (int)b.S.Length;
            using var ms = new MemoryStream();
            ms.Write(Encoding.ASCII.GetBytes($"BtWg000300020000{bodyOffset:x8}{zipOffset:x16}00"));
            ms.Write(m.S.ToArray());
            ms.Write(Encoding.ASCII.GetBytes("       \n"));
            ms.Write(b.S.ToArray());
            if (zip is not null) ms.Write(zip);
            return ms.ToArray();
        }

        public void MetaEntry(string key, byte type) { U32(1); StrVal(key); S.WriteByte(type); }
    }

    /// <summary>
    /// A Bitwig 5-style project: one unnamed instrument track with Keyscape (VST3) and
    /// Delay+, a note clip, a master track holding Pro-L 2 that is defined inside the
    /// first track and referenced from the project (as Bitwig does), an effect track,
    /// plugin states in the embedded zip and a "structure" summary with display names.
    /// </summary>
    public static string BitwigProject(string dir, string name = "Loop Idea")
    {
        var projDir = Path.Combine(dir, name);
        Directory.CreateDirectory(Path.Combine(projDir, "samples"));
        File.WriteAllBytes(Path.Combine(projDir, "samples", "Recorded Bass.wav"), [0]);

        byte[] zip;
        using (var zs = new MemoryStream())
        {
            using (var z = new ZipArchive(zs, ZipArchiveMode.Create, leaveOpen: true))
            {
                using (var s = z.CreateEntry("plugin-states/aaaa-keys.vstpreset").Open()) s.Write("VST3 keys state"u8);
                using (var s = z.CreateEntry("plugin-states/bbbb-limiter.vstpreset").Open()) s.Write("VST3 limiter state"u8);
            }
            zip = zs.ToArray();
        }

        // Bitwig's "structure" summary is itself a small BtWg document.
        var st = new BwWriter();
        st.Begin(0x2a0);
        st.Str(0xad3, "");
        st.Obj(0xad2); st.Begin(0x283);
        st.Str(0xad1, "Project");
        st.List(0xace);
        st.Begin(0x288); st.Str(0xad1, "Keys Display"); st.End();
        st.EndList();
        st.List(0xacf);
        st.Begin(0x28a); st.Str(0xad1, "FX 1"); st.End();
        st.EndList();
        st.Obj(0xad0); st.Begin(0x28b); st.Str(0xad1, "Master"); st.End();
        st.End();
        st.End();
        var structure = new MemoryStream();
        structure.Write(Encoding.ASCII.GetBytes("BtWg0003000200000000002b000000000000000001\n"));
        structure.Write(st.S.ToArray());

        var master = 0;
        var bytes = BwWriter.File(meta: m =>
        {
            m.MetaEntry("application_version_name", 0x08); m.StrVal("5.2.4");
            m.MetaEntry("structure", 0x0d); m.U32(structure.Length); m.S.Write(structure.ToArray());
            m.MetaEntry("title", 0x08); m.StrVal("");
            m.MetaEntry("used_files", 0x19); m.U32(2); m.StrVal("samples/Kick 01.wav"); m.StrVal("multi-samples/Pad.multisample");
        }, body: b =>
        {
            b.Begin(0x2e);
            b.Id(0xf);
            b.Str(0x44, "Loop Idea");
            b.Obj(0x29a6); b.Begin(0x4c1); b.F64(0x2c8, 128.5); b.End();
            b.Obj(0x4dd); b.Begin(0x1dd);
            b.Id(0xf);
            b.Str(0x15b, "Project");
            b.List(0x4de);
            {
                b.Begin(0x90);
                b.Id(0xf);
                b.Str(0x15b, "");
                b.Map(0x168); b.MapKey("user"); b.Begin(0x88); b.U8(0x2603, 64); b.End(); b.EndMap();
                b.List(0x4ba);
                {
                    b.Begin(ClsVst3);
                    b.Str(0x12de, ""); b.Str(0x9a, "Keyscape"); b.Str(0x9b, "Spectrasonics"); b.Str(0x9c, "Synth");
                    b.Str(0xbe5, "aaaa-keys.vstpreset");
                    b.End();
                    b.Begin(0x40);
                    b.Str(0x9a, "Delay+"); b.Str(0x9b, "Bitwig"); b.Str(0x9c, "Delay");
                    b.Str(0x133b, "Delay » Feedback", utf16: true);
                    b.Ints(0x14e1, 0, 1);
                    b.End();
                }
                b.EndList();
                b.Obj(0x2bad); b.Begin(0x2a); b.Str(0x3b, @"C:\Program Files\Common Files\VST3\Keyscape.vst3"); b.End();
                b.Obj(0x2bae); b.Begin(0x2a); b.Str(0x3b, @"D:\Splice\Snare 808.wav"); b.End();
                b.Obj(0x2baf); b.Begin(0x461); b.Str(0x11a7, "Elektron Analog Rytm MKII"); b.End();
                // The master track, defined here and referenced from the project group below.
                b.Obj(0x2bb0); master = b.Begin(0x90);
                {
                    b.Id(0xf);
                    b.Str(0x15b, "");
                    b.List(0x4ba);
                    b.Begin(ClsVst3);
                    b.Str(0x9a, "FabFilter Pro-L 2"); b.Str(0x9b, "FabFilter"); b.Str(0x9c, "Dynamics");
                    b.Str(0xbe5, "bbbb-limiter.vstpreset");
                    b.End();
                    b.EndList();
                    b.End();
                }
                b.Obj(0x15e); b.Begin(0x1e);
                b.List(0x231);
                b.Begin(0xe3);
                b.Obj(0x240); b.Begin(0x47);
                b.F64(0x2af, 0); b.F64(0x26, 4);
                b.Obj(0x288); b.Begin(0xbf);
                b.Obj(0x49c); b.Begin(0x6cc);
                b.List(0x18cb);
                void Lane(byte key, params (double Start, double Len, double Vel)[] notes)
                {
                    b.Begin(0x42);
                    b.U8(0xee, key);
                    b.List(0x21f);
                    foreach (var (s, l, v) in notes)
                    {
                        b.Begin(0x66); b.F64(0x2af, s); b.F64(0x26, l); b.F64(0xef, v); b.End();
                    }
                    b.EndList();
                    b.End();
                }
                Lane(60, (0, 1, 0.5), (1.5, 0.5, 1.0));
                Lane(64, (2, 1, 0.8));
                b.EndList();
                b.End(); // 0x6cc
                b.End(); // 0xbf
                b.End(); // clip
                b.Named(0x89b); b.ListRef(master); b.StrVal("named ref");
                b.End(); // 0xe3
                b.EndList();
                b.End(); // 0x1e
                b.End(); // track
            }
            b.EndList();
            b.List(0x4df);
            b.Begin(0x90); b.Id(0xf); b.Str(0x15b, ""); b.End();
            b.EndList();
            b.Ref(0x4e0, master);
            b.End(); // project group
            b.End(); // root
        }, zip: zip);

        var path = Path.Combine(projDir, name + ".bwproject");
        File.WriteAllBytes(path, bytes);
        return path;
    }

    const int ClsVst3 = 0x728;

    /// <summary>
    /// A Bitwig 6-style template for converting FL projects, laid out like one saved in Bitwig 6.1: a
    /// Sampler track (Sampler and Amp, root key A2, sends to an effect track that's defined inside it,
    /// and the master defined in its output routing), a VST3 track with a Note Transpose before
    /// Serum 2 and a clip of notes, and a VST2 track with Drumazon, whose output chain sends to the
    /// effect track. The Sampler track's volume is automated, with an automation clip and a point after it.
    /// Between the Sampler and VST3 tracks, an audio track holds an audio clip playing a 32 second sample, as
    /// Bitwig 6.1 saves one: its content is laid out like a clip of notes, with the audio as its one "note".
    /// As in Bitwig's files, the mixer volume is first written in the track's per-user
    /// view state and referenced from the mixer, and the project's view state and clip selection
    /// point into the tracks.
    /// </summary>
    public static byte[] BitwigTemplate(bool automationLane = true, bool audioTrack = true, bool devicePower = true)
    {
        const string sampler = "468bc14b-b2e7-45a1-9666-e83117fe404e", noteTranspose = "0815cd9e-3a31-4429-a268-dabd952a3b68",
                     amp = "41be8f3a-6d24-4442-9508-8548dbe62d47";
        byte[] zip;
        using (var zs = new MemoryStream())
        {
            using (var z = new ZipArchive(zs, ZipArchiveMode.Create, leaveOpen: true))
            {
                using (var s = z.CreateEntry("plugin-states/serum.vstpreset").Open()) s.Write("template serum"u8);
                using (var s = z.CreateEntry("plugin-states/drumazon.fxb").Open()) s.Write("template drumazon"u8);
            }
            zip = zs.ToArray();
        }

        var st = new BwWriter();
        st.Begin(0x285);
        st.Str(0xad3, "");
        st.Obj(0xad2); st.Begin(0x283);
        st.Str(0xad1, "Project");
        st.List(0xace);
        foreach (var n in audioTrack ? new[] { "Kick", "Drops", "Serum 2", "Drumazon" } : ["Kick", "Serum 2", "Drumazon"])
        {
            st.Begin(n == "Drops" ? 0x287 : 0x288); st.Str(0xad1, n); st.End();
        }
        st.EndList();
        st.List(0xacf); st.Begin(0x28a); st.Str(0xad1, "FX 1"); st.End(); st.EndList();
        st.Obj(0xad0); st.Begin(0x28b); st.Str(0xad1, "Master"); st.End();
        st.End();
        st.End();
        var structure = new MemoryStream();
        structure.Write(Encoding.ASCII.GetBytes("BtWg0003000200000000002b000000000000000001\n"));
        structure.Write(st.S.ToArray());

        int master = 0, fx = 0, root = 0, firstTrack = 0, clip = 0, content = 0;
        return BwWriter.File(meta: m =>
        {
            m.MetaEntry("application_version_name", 0x08); m.StrVal("6.1.3");
            m.MetaEntry("structure", 0x0d); m.U32(structure.Length); m.S.Write(structure.ToArray());
            m.MetaEntry("used_files", 0x19); m.U32(0);
        }, body: b =>
        {
            void Param(int cls, string name, double value) { b.Begin(cls); b.Str(0x2bd, name); b.F64(0x2c8, value); b.End(); }
            void Named(int cls, string name, Action fields) { b.Begin(cls); b.Str(0x2b9, name); fields(); b.End(); }
            void FileRef(string path) { b.Begin(0x2a); b.Str(0x38, "PC"); b.Str(0x3a, "user"); b.Str(0x3b, path); b.End(); }
            // A track's start: ids, per-user view state that first writes its volume automation lane and
            // mixer volume, its automation lanes. An automated lane holds an automation clip and a point after it.
            int TrackStart(string name, bool automated = false, Action? lanes = null)
            {
                var t = b.Begin(0x90);
                b.Id(0xf);
                b.Str(0x15b, name);
                b.Map(0x168); b.MapKey("user"); b.Begin(0x88);
                b.Obj(0x4e6); b.Begin(0x23d); b.Obj(0x2a3); b.Begin(0xf1);
                b.Obj(0x41); Param(0x4c4, "VOLUME", 1.0);
                b.Str(0x133b, "Mixer"); b.Str(0x133a, "Volume");
                b.End();
                b.Obj(0x2d8c); b.Begin(0xd91);
                b.List(0x21f);
                if (automated) { b.Begin(0xd90); b.F64(0x2af, 0); b.F64(0x26, 4); b.End(); }
                b.EndList();
                b.Obj(0x35ac); b.Begin(0x6e); b.List(0x21f);
                if (automated) { b.Begin(0x108); b.F64(0x2af, 4); b.F64(0x28f, 1.25992105); b.U8(0x359e, 0); b.F64(0xba6, 0.5); b.F64(0x2ebe, 0); b.End(); }
                b.EndList(); b.End();
                b.End();
                b.Obj(0x2aec); b.Begin(0xe6c); b.List(0x10e6); b.EndList(); b.End();
                b.End();
                b.End(); b.EndMap();
                b.Obj(0x2dc9); b.Begin(0xfa2); b.List(0x4ba); if (automated) b.ListRef(t + 2); lanes?.Invoke(); b.EndList(); b.List(0x3f83); b.EndList(); b.End();
                b.Id(0xa51);
                return t;
            }
            // The rest: arrangement clips, clip contents, devices, mixer (volume by reference), output to the master.
            void TrackEnd(int volume, Action? clips, Action devices, Action sends, Action output)
            {
                b.Obj(0x2bf8); b.Begin(0xef7);
                b.Obj(0x2d8e); b.Begin(0x52); b.List(0x21f); clips?.Invoke(); b.EndList(); b.End();
                b.End();
                b.Obj(0x2aed); b.Begin(0xe6d); b.List(0x10e6); if (clips is not null) b.ListRef(content); b.EndList(); b.End();
                b.Obj(0x164); b.Begin(0x8a); b.List(0x144); devices(); b.EndList(); b.End();
                b.Obj(0x165); b.Begin(0xab);
                b.BareRef(0x1a4, volume);
                b.Obj(0x1a5); Param(0x4c3, "PANORAMA", 0.0);
                b.Obj(0x1a6); b.Begin(0x4ce); b.Str(0x2bd, "MUTE"); b.Bool(0xd2, false); b.End();
                b.List(0xa5); sends(); b.EndList();
                b.End();
                b.Obj(0x897); b.Begin(0x241); b.Named(0x89b); output(); b.StrVal("361"); b.End();
                b.End();
            }
            void ToMaster() => b.ListRef(master);
            void SendToFx() { b.Begin(0x27c); b.Obj(0xab3); Param(0x4c2, "AMOUNT", 0.0); b.Ref(0xbd0, fx); b.End(); }
            void Contents(params (string Name, Action Value)[] items)
            {
                b.Obj(0xa4); b.Begin(0xd3); b.Str(0x2b9, "CONTENTS");
                b.List(0x20c);
                foreach (var (_, value) in items) value();
                b.EndList();
                b.End();
            }

            root = b.Begin(0x2e);
            b.Id(0xf);
            b.Obj(0x29a6); Param(0x4c1, "TEMPO", 120.0);
            b.Obj(0x4dd); b.Begin(0x1dd);
            b.Id(0xf);
            b.Str(0x15b, "Project");
            b.List(0x4de);
            {
                // Sampler track
                firstTrack = TrackStart("", automated: automationLane);
                var volume = firstTrack + 4;   // the track, its view state, lane, target, then the volume
                TrackEnd(volume, null, devices: () =>
                {
                    b.Begin(0xa3); b.Id(0xf);
                    b.Obj(0x197); b.Begin(0x40);
                    b.Str(0x12de, "Kick"); b.Str(0x9a, "Sampler"); b.Str(0x9b, "Bitwig"); b.Str(0x9c, "Synth"); b.U8(0x9d, 2);
                    b.Uuid(0x99, sampler);
                    Contents(("TRANSPOSE", () => Named(0x85, "TRANSPOSE", () => b.F64(0x136, 0))),
                             ("SAMPLE", () => Named(0x212, "SAMPLE", () =>
                             {
                                 b.Obj(0x74c); b.Begin(0x210); b.Str(0x2b9, "SAMPLE");
                                 b.Obj(0x4112); b.Begin(0x15eb);
                                 b.Obj(0x748); b.Begin(0x4a9);
                                 b.Str(0x129f, "Kick.wav");
                                 b.Obj(0x129e); b.Begin(0x4a6);
                                 b.Str(0x512, "Kick.wav");
                                 b.Obj(0xd3b); FileRef(@"J:\samples\Kick.wav");
                                 b.Obj(0x513); b.Begin(0x1e2); b.Blob(0x16, new byte[16]); b.End();
                                 b.I16(0x17, 3856);
                                 b.End();
                                 b.I32(0x94, 44100); b.F64(0x95, 0.25); b.U8(0x96, 1);
                                 b.End();
                                 b.End();
                                 b.U8(0x75c, 57); b.F64(0x75f, 0.25); b.F64(0x761, 0.25);
                                 b.End();
                             })));
                    b.End();
                    b.Str(0x199, "Kick");
                    b.End();
                    b.Begin(0xa3); b.Id(0xf);
                    b.Obj(0x197); b.Begin(0x40); b.Str(0x9a, "Amp"); b.U8(0x9d, 0); b.Uuid(0x99, amp); b.End();
                    b.End();
                }, sends: () =>
                {
                    // The effect track is first written inside this send.
                    b.Begin(0x27c); b.Obj(0xab3); Param(0x4c2, "AMOUNT", 0.0);
                    b.Obj(0xbd0); fx = b.Begin(0x90); b.Id(0xf); b.Str(0x15b, "FX 1"); b.End();
                    b.End();
                }, output: () =>
                {
                    master = b.Begin(0x90);
                    b.Id(0xf);
                    b.Str(0x15b, "Master");
                    b.Obj(0x164); b.Begin(0x8a); b.List(0x144); b.EndList(); b.End();
                    b.Obj(0x165); b.Begin(0xab); b.Obj(0x1a4); Param(0x4c4, "VOLUME", 1.0); b.Obj(0x1a5); Param(0x4c3, "PANORAMA", 0.0); b.End();
                    b.End();
                });

                if (audioTrack)
                {
                    var audio = TrackStart("Drops");
                    TrackEnd(audio + 4, clips: () =>
                    {
                        b.Begin(0xee);
                        b.F64(0x2af, 0); b.F64(0x26, 64); b.Bool(0x10f8, false);
                        b.Obj(0x2ae9); b.Begin(0xec2);
                        b.Obj(0x10e9); content = b.Begin(0x105);
                        b.Str(0x2d24, "Drops");
                        b.Obj(0x49c); b.Begin(0x6ce);
                        b.List(0x18cb);
                        b.Begin(0xce);
                        b.List(0x21f);
                        b.Begin(0xd4);
                        b.F64(0x2af, 0); b.F64(0x26, 64); b.Bool(0x10f8, false);
                        b.Obj(0x288); b.Begin(0x9f);
                        b.Null(0xc33);
                        b.Obj(0x20d); b.Begin(0x1db);
                        b.Obj(0x4b4); var drops = b.Begin(0x4a9);
                        b.Str(0x129f, "Drops.wav");
                        b.Obj(0x129e); b.Begin(0x4a6);
                        b.Str(0x512, "Drops.wav");
                        b.Obj(0xd3b); FileRef(@"J:\samples\Drops.wav");
                        b.Obj(0x513); b.Begin(0x1e2); b.Blob(0x16, new byte[16]); b.End();
                        b.I32(0x17, 8467244);
                        b.List(0x129c); b.ListRef(drops); b.EndList();
                        b.End();
                        b.I32(0x94, 44100); b.F64(0x95, 32); b.U8(0x96, 2);
                        b.End();
                        b.End();
                        b.U8(0x4b0, 3);
                        b.Obj(0x192); b.Begin(0x8b); b.List(0x21f);
                        b.Begin(0x43); b.F64(0x2af, 0); b.F64(0xa6, 0); b.End();
                        b.Begin(0x43); b.F64(0x2af, 64); b.F64(0xa6, 32); b.End();
                        b.EndList(); b.End();
                        b.Obj(0x10ea); b.Begin(0x36b);
                        b.Obj(0x10e9); b.Begin(0xb6); b.List(0x21f);
                        foreach (var at in new[] { 0.0, 8.5 }) { b.Begin(0x203); b.F64(0x2af, at); b.F64(0x703, 1.99); b.End(); }
                        b.EndList(); b.U8(0x4406, 1); b.End();
                        b.End();
                        b.End(); // play
                        b.F64(0xc04, 0); b.F64(0xc05, 32);
                        b.End(); // audio
                        b.EndList();
                        b.End(); // lane
                        b.EndList();
                        b.End(); b.End(); b.End();
                        b.Obj(0x2adb); b.Begin(0x267);
                        b.U8(0x991, 0);
                        b.Obj(0x992); b.Begin(0x58); b.F64(0x2af, 0); b.F64(0x26, 69.33); b.End();
                        b.Obj(0x98c); b.Begin(0x269); b.F64(0x2af, 0); b.End();
                        b.Obj(0x98d); b.Begin(0x268); b.F64(0x2af, 64); b.End();
                        b.End();
                        b.End();
                    }, devices: () => { }, sends: SendToFx, output: ToMaster);
                }

                // VST3 track: Note Transpose, Serum 2, a clip with two notes on key 63 and one on 60
                var vst3 = TrackStart("");
                TrackEnd(vst3 + 4, clips: () =>
                {
                    clip = b.Begin(0x47);
                    b.F64(0x2af, 0); b.F64(0x26, 4);
                    b.Obj(0x2ae9); b.Begin(0xec0);
                    b.Obj(0x10e9); content = b.Begin(0xbf);
                    b.Obj(0x49c); b.Begin(0x6cc);
                    b.List(0x18cb);
                    foreach (var (key, starts) in new[] { (63, new[] { 0.0, 2.0 }), (60, new[] { 1.0 }) })
                    {
                        b.Begin(0x42);
                        b.List(0x21f);
                        foreach (var at in starts)
                        {
                            b.Begin(0x66); b.F64(0x2af, at); b.F64(0x26, 0.25);
                            b.Obj(0x288); b.Begin(0x29); b.Null(0xc33); b.End();
                            b.F64(0xef, 0.79); b.F64(0xf0, 0.79);
                            b.End();
                        }
                        b.EndList();
                        b.U8(0xee, (byte)key);
                        b.End();
                    }
                    b.EndList();
                    b.End(); b.End(); b.End();
                    b.Obj(0x2adb); b.Begin(0x267);
                    b.Obj(0x992); b.Begin(0x58); b.F64(0x2af, 0); b.F64(0x26, 4); b.End();
                    b.Obj(0x98c); b.Begin(0x269); b.F64(0x2af, 0); b.End();
                    b.Obj(0x98d); b.Begin(0x268); b.F64(0x2af, 4); b.End();
                    b.End();
                    b.End();
                }, devices: () =>
                {
                    b.Begin(0xa3); b.Id(0xf);
                    b.Obj(0x197); b.Begin(0x40);
                    b.Str(0x9a, "Note Transpose"); b.Str(0x9c, "Note FX"); b.U8(0x9d, 1); b.Uuid(0x99, noteTranspose);
                    Contents(("COARSE", () => Named(0x189, "COARSE", () => b.U8(0x330, 3))),
                             ("OCTAVES", () => Named(0xf7, "OCTAVES", () => b.U8(0x273, 3))));
                    b.End();
                    b.End();
                    b.Begin(0x729); b.Id(0xf);
                    b.Obj(0x197); b.Begin(ClsVst3);
                    b.Str(0x12de, ""); b.Str(0x9a, "Serum 2"); b.Str(0x9b, "Xfer Records"); b.Str(0x9c, "Synth"); b.U8(0x9d, 2);
                    if (devicePower) b.Bool(0xa3, true);   // the device's power switch
                    b.Obj(0xc5a); b.Begin(0x2b4); b.List(0xc5b); FileRef(@"C:\Program Files\Common Files\VST3\Serum2.vst3"); b.EndList(); b.End();
                    b.Str(0xbe5, "serum.vstpreset");
                    b.List(0x1318); b.Begin(0x4e0); b.I32(0x1319, 2000003); b.F64(0x131a, 0.42); b.End(); b.EndList();
                    b.Obj(0x137c); b.Begin(0x4f3); b.List(0x8e0); b.EndList(); b.End();
                    b.U8(0x1809, 1);
                    b.Str(0xce4, "56534558667350736572756D20320000");
                    b.Str(0xc5e, "2.0.16");
                    b.End();
                    b.End();
                }, sends: SendToFx, output: ToMaster);

                // VST2 track: Drumazon, with an output chain that sends to the effect track, and its first
                // setting automated. The plugin is first written in the automation lane, which comes first.
                var drumazon = 0;
                var vst2 = TrackStart("", lanes: () =>
                {
                    b.Begin(0x23d);
                    b.Obj(0x2d8c); b.Begin(0xd91);
                    b.List(0x21f); b.Begin(0xd90); b.F64(0x2af, 0); b.F64(0x26, 4); b.End(); b.EndList();
                    b.Obj(0x35ac); b.Begin(0x6e); b.List(0x21f);
                    b.Begin(0x108); b.F64(0x2af, 4); b.F64(0x28f, 0.92); b.U8(0x359e, 1); b.F64(0xba6, 0); b.End();
                    b.EndList(); b.End();
                    b.End();
                    b.Obj(0x2a3); b.Begin(0x65);
                    b.Str(0x133b, "Drumazon"); b.Str(0x133a, "Bass Drum Level");
                    b.Obj(0xec); drumazon = Drumazon();
                    b.Str(0xed, "CONTENTS/ROOT_GENERIC_MODULE/PARAM0");
                    b.Obj(0xe92); b.Begin(0x7b); b.F64(0x124, 0); b.F64(0x125, 1); b.End();
                    b.End();
                    b.Obj(0x2aec); b.Begin(0xe6c); b.List(0x10e6); b.EndList(); b.End();
                    b.End();
                });
                TrackEnd(vst2 + 4, null, devices: () => b.ListRef(drumazon), sends: SendToFx, output: ToMaster);
                int Drumazon()
                {
                    var d = b.Begin(0x1a6); b.Id(0xf);
                    b.Obj(0x197); b.Begin(0x1a5);
                    b.Str(0x12de, ""); b.Str(0x9a, "Drumazon"); b.Str(0x9b, "D16 Group Audio Software"); b.Str(0x9c, "Synth"); b.U8(0x9d, 2);
                    if (devicePower) b.Bool(0xa3, true);
                    b.Obj(0x1b75); b.Begin(0x7b1); b.Str(0x1b72, "Bass Drum"); b.End();
                    b.Obj(0xc5a); b.Begin(0x2b4); b.List(0xc5b); FileRef(@"C:\Program Files\Vstplugins\Drumazon.dll"); b.EndList(); b.End();
                    b.Str(0xbe5, "drumazon.fxb");
                    b.List(0x1318); b.EndList();
                    b.Obj(0x137c); b.Begin(0x4f3); b.List(0x8e0);
                    b.Begin(0x5a0); b.Str(0x2b9, "CHAIN0"); b.Id(0x2ab8);
                    b.Obj(0x825); b.Begin(0x23a); b.Str(0x2b9, "MIXER");
                    b.List(0xbd2); b.Begin(0x732); b.Str(0x2b9, "TRACK_SEND(96d49fae-f171-4587-b7dd-1202ce2a9915)"); b.End(); b.EndList();
                    b.End();
                    b.End();
                    b.EndList(); b.End();
                    b.U8(0x1809, 0);
                    b.I32(0xce4, 1144278322);
                    b.I32(0xc5e, 67074);
                    b.End();
                    b.End();
                    return d;
                }
            }
            b.EndList();
            b.List(0x4df); b.ListRef(fx); b.EndList();
            b.BareRef(0x4e0, master);
            // The arrangement's clip selection points at the template's clip.
            b.Obj(0x2bfb); b.Begin(0xef9);
            b.Obj(0x2d8b); b.Begin(0x60a); b.List(0x21f);
            b.Begin(0x60b); b.F64(0x2af, 0); b.F64(0x26, 4); b.List(0x3f8b); b.ListRef(clip); b.EndList(); b.End();
            b.EndList(); b.End();
            b.End();
            b.End(); // project group
            // The project's per-user view state: the selected track.
            b.Map(0x4d); b.MapKey("user"); b.Begin(0xe4);
            b.Obj(0xee6); b.Begin(0x325); b.Obj(0xb5e); b.Begin(0x29e); b.BareRef(0xb5d, firstTrack); b.End(); b.End();
            b.End(); b.EndMap();
            b.End(); // root
        }, zip: zip);
    }
}
