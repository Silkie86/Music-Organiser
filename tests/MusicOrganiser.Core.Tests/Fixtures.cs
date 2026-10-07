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

    // ---- FL Studio ---------------------------------------------------------

    public sealed class FlpBuilder
    {
        readonly MemoryStream _events = new();
        public int Ppq = 96;

        public FlpBuilder Byte(int id, byte v) { _events.WriteByte((byte)id); _events.WriteByte(v); return this; }
        public FlpBuilder Word(int id, ushort v) { _events.WriteByte((byte)id); _events.Write(BitConverter.GetBytes(v)); return this; }
        public FlpBuilder Dword(int id, uint v) { _events.WriteByte((byte)id); _events.Write(BitConverter.GetBytes(v)); return this; }

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

    public static byte[] Note(uint pos, ushort channel, uint length, ushort key, byte velocity)
    {
        var b = new byte[24];
        BinaryPrimitives.WriteUInt32LittleEndian(b, pos);
        BinaryPrimitives.WriteUInt16LittleEndian(b.AsSpan(6), channel);
        BinaryPrimitives.WriteUInt32LittleEndian(b.AsSpan(8), length);
        BinaryPrimitives.WriteUInt16LittleEndian(b.AsSpan(12), key);
        b[21] = velocity;
        return b;
    }

    /// <summary>
    /// Three channels (Serum VST on "Lead", a sampler "Kick", native 3x Osc "Bass"),
    /// two patterns with notes, and a mixer with Fruity Limiter and a Pro-Q 3 VST.
    /// </summary>
    public static string FlStudioProject(string dir, string name = "Night Drive")
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
        var path = Path.Combine(dir, name + ".flp");
        File.WriteAllBytes(path, b.Build());
        return path;
    }

    /// <summary>FL's VST2 state: a 21-byte header in front of the plugin's own chunk.</summary>
    public static byte[] FlVst2State(byte[] chunk)
    {
        var b = new byte[21 + chunk.Length];
        BinaryPrimitives.WriteInt32LittleEndian(b, -9);
        b[4] = 0x0d;
        BinaryPrimitives.WriteInt32LittleEndian(b.AsSpan(5), -2);
        BinaryPrimitives.WriteUInt32LittleEndian(b.AsSpan(9), (uint)chunk.Length);
        chunk.CopyTo(b, 21);
        return b;
    }

    /// <summary>FL's VST3 state: version 1, then records 1 (settings), 3 (component), 2 (controller).</summary>
    public static byte[] FlVst3State(byte[] component, byte[] controller)
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
        return ms.ToArray();
    }

    /// <summary>
    /// An FL Studio 25 project inside "FL Studio/Projects": wrapper state version 12 with a
    /// VST2 (Serum) and a VST3 (Arcade), and a sample under %FLStudioUserData%.
    /// </summary>
    public static string FlStudio25Project(string dir)
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
                                   FlVst3State([7, 7, 7], [8]), version: 12, kind: 8, classId: arcadeId));
        var path = Path.Combine(projects, "Shuffle.flp");
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
                b.Map(0x168); b.MapKey("silkie86"); b.Begin(0x88); b.U8(0x2603, 64); b.End(); b.EndMap();
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
}
