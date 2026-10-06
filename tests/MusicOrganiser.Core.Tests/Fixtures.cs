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

    public static byte[] WrapperBlob(string name, string path, string vendor, byte[] state)
    {
        using var ms = new MemoryStream();
        ms.Write(BitConverter.GetBytes(10u));
        void Field(uint id, byte[] data)
        {
            ms.Write(BitConverter.GetBytes(id));
            ms.Write(BitConverter.GetBytes((ulong)data.Length));
            ms.Write(data);
        }
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
            .Data(213, WrapperBlob("Serum", @"C:\Program Files\Common Files\VST3\Serum.vst3", "Xfer Records", [1, 2, 3, 4]))
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

    public static string BitwigProject(string dir, string name = "Loop Idea")
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
}
