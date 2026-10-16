using System.Text.Json;
using MusicOrganiser.Core.Export;
using MusicOrganiser.Core.Library;
using MusicOrganiser.Core.Midi;
using MusicOrganiser.Core.Parsers;

namespace MusicOrganiser.Core.Tests;

/// <summary>
/// The CLaMP 3 pieces checked against CLaMP 3's own Python: Data/clamp3-tiny holds a small random model exported by
/// tools/clamp3/export_onnx.py --tiny, its tokenizer, and what the Python side gave for each input (MTF written by mido,
/// patches from M3Patchilizer, token ids from Hugging Face tokenizers, and both encoders' outputs).
/// </summary>
public class Clamp3Tests
{
    static readonly string Folder = Path.Combine(AppContext.BaseDirectory, "Data", "clamp3-tiny");
    static readonly JsonElement Expected = JsonDocument.Parse(File.ReadAllText(Path.Combine(Folder, "expected.json"))).RootElement;

    static float[] Unit(JsonElement array)
    {
        var v = array.EnumerateArray().Select(x => x.GetSingle()).ToArray();
        var n = MathF.Sqrt(v.Sum(x => x * x));
        return v.Select(x => x / n).ToArray();
    }

    static double Cosine(float[] a, float[] b) => a.Zip(b, (x, y) => (double)x * y).Sum();

    [Fact]
    public void Notes_become_the_same_mtf_text_as_mido_writes()
    {
        foreach (var c in Expected.GetProperty("notes").EnumerateArray())
        {
            var notes = c.GetProperty("notes").EnumerateArray()
                .Select(n => new NoteEvent(n[0].GetInt64(), n[1].GetInt64(), n[2].GetInt32(), n[3].GetInt32())).ToList();
            Assert.Equal(c.GetProperty("mtf").GetString(),
                         MtfText.FromNotes(notes, 96, c.GetProperty("tempo").GetDouble(), c.GetProperty("drums").GetBoolean()));
        }
    }

    [Fact]
    public void Mtf_is_cut_into_the_same_patches_as_clamp3_cuts_it()
    {
        foreach (var c in Expected.GetProperty("midi").EnumerateArray())
        {
            var want = c.GetProperty("patches").EnumerateArray().Select(p => p.EnumerateArray().Select(x => x.GetInt32()).ToArray()).ToList();
            var got = M3Patches.Encode(c.GetProperty("mtf").GetString()!);
            Assert.Equal(want.Count, got.Count);
            for (var i = 0; i < want.Count; i++) Assert.Equal(want[i], got[i]);
        }
    }

    [Fact]
    public void Long_pieces_are_read_in_windows_weighted_as_clamp3_weights_them()
    {
        Assert.Equal([(0, 100, 100)], M3Patches.Windows(100));
        Assert.Equal([(0, 512, 512)], M3Patches.Windows(512));
        Assert.Equal([(0, 512, 512), (488, 512, 488)], M3Patches.Windows(1000));
        Assert.Equal([(0, 512, 512), (512, 512, 512)], M3Patches.Windows(1024));
    }

    [Fact]
    public void Descriptions_are_tokenised_as_hugging_face_does()
    {
        var tokenizer = UnigramTokenizer.Load(Path.Combine(Folder, "tokenizer.json"));
        foreach (var c in Expected.GetProperty("tokens").EnumerateArray())
            Assert.Equal(c.GetProperty("ids").EnumerateArray().Select(x => x.GetInt32()), tokenizer.Encode(c.GetProperty("text").GetString()!));
    }

    [Fact]
    public void Both_encoders_give_what_onnx_runtime_gives_in_python()
    {
        using var model = new Clamp3Model(Folder);
        foreach (var c in Expected.GetProperty("midi").EnumerateArray())
            Assert.True(Cosine(Unit(c.GetProperty("embedding")), model.EmbedMtf(c.GetProperty("mtf").GetString()!)) > 0.9999);
        foreach (var c in Expected.GetProperty("text").EnumerateArray())
            Assert.True(Cosine(Unit(c.GetProperty("embedding")), model.EmbedText(c.GetProperty("text").GetString()!)) > 0.9999);
    }

    [Fact]
    public void Packed_vectors_keep_their_likeness()
    {
        var rng = new Random(3);
        float[] RandomUnit()
        {
            var v = Enumerable.Range(0, 768).Select(_ => (float)(rng.NextDouble() - 0.5)).ToArray();
            var n = MathF.Sqrt(v.Sum(x => x * x));
            return v.Select(x => x / n).ToArray();
        }
        var a = RandomUnit();
        var b = a.Select((x, i) => x + (i % 3 == 0 ? 0.02f : 0)).ToArray();
        var packedA = Clamp3Model.Unpack(Clamp3Model.Pack(a));
        var packedB = Clamp3Model.Unpack(Clamp3Model.Pack(b));
        var exact = Cosine(a, b) / Math.Sqrt(Cosine(b, b));
        Assert.Equal(exact, Clamp3Model.Similarity(packedA, packedB), 2);
        Assert.Equal(1, Clamp3Model.Similarity(a, packedA), 2);
    }

    // ---- searching by description ----

    static StoredPhrase Phrase(long id, string part, string role, string hash) =>
        new(id, new MidiSource($@"k:\{id}.flp", MidiSource.Project, "FL Studio", 100 - id, 1, 120), part, "", role, "", null,
            4, 12, 48, 72, null, new byte[12], "", new byte[16], hash);

    static sbyte[] Towards(int axis, float amount)
    {
        var v = new float[768];
        v[axis] = amount;
        v[767] = MathF.Sqrt(1 - amount * amount);
        return Clamp3Model.Unpack(Clamp3Model.Pack(v));
    }

    [Fact]
    public void A_description_ranks_parts_by_how_close_they_sound_and_names_still_count()
    {
        var phrases = new[]
        {
            Phrase(1, "Keys", MidiRoles.Chords, "a"), Phrase(2, "Lead", MidiRoles.Melody, "b"),
            Phrase(3, "Pad", MidiRoles.Chords, "c"), Phrase(4, "Dark pad", MidiRoles.Chords, "d"),
        };
        // "a" sounds most like the description, "c" a little, "b" not at all; "d" hasn't been heard yet.
        var meanings = new Dictionary<string, sbyte[]> { ["a"] = Towards(0, 0.9f), ["b"] = Towards(1, 0.9f), ["c"] = Towards(0, 0.5f) };
        var catalogue = new MidiCatalogue(phrases, meanings);
        var words = new float[768];
        words[0] = 1;

        var hits = catalogue.Search(new MidiQuery("warm", Words: words), out var matching);
        Assert.Equal(["a", "c", "b"], hits.Select(h => h.Entry.Phrase.Hash));
        Assert.Equal(3, matching);   // "d" isn't heard and isn't named "warm"

        // A part not heard yet still shows up when its names match every word, as without the model.
        hits = catalogue.Search(new MidiQuery("dark pad", Words: words), out _);
        Assert.Contains(hits, h => h.Entry.Phrase.Hash == "d");
        // The kind chips still narrow it down.
        Assert.All(catalogue.Search(new MidiQuery("warm", MidiRoles.Chords, Words: words), out _), h => Assert.Equal(MidiRoles.Chords, h.Entry.Role));
        // Without the model's reading of the words, the search is by names only.
        Assert.Empty(catalogue.Search(new MidiQuery("warm"), out _));
        Assert.Equal(3, catalogue.HeardCount);
    }

    [Fact]
    public void Names_can_count_for_more_or_not_at_all()
    {
        var phrases = new[] { Phrase(1, "Keys", MidiRoles.Chords, "a"), Phrase(2, "Pad", MidiRoles.Chords, "b"), Phrase(3, "Dark pad", MidiRoles.Chords, "c") };
        // "a" sounds a little closer to the description than "b", which is named after it; "c" hasn't been heard yet.
        var meanings = new Dictionary<string, sbyte[]> { ["a"] = Towards(0, 0.58f), ["b"] = Towards(0, 0.5f) };
        var catalogue = new MidiCatalogue(phrases, meanings);
        var words = new float[768];
        words[0] = 1;
        List<string> Order(float weight) =>
            catalogue.Search(new MidiQuery("pad", Words: words, NameWeight: weight), out _).Select(h => h.Entry.Phrase.Hash).ToList();

        // As it comes, the name lifts "b" a little, not past the closer sound of "a".
        Assert.Equal(["a", "b", "c"], Order(1));
        // Names counting three times put the part named "Pad" first.
        Assert.Equal(["b", "a", "c"], Order(3));
        // The model only: names are ignored, so the part not heard yet drops out.
        Assert.Equal(["a", "b"], Order(0));
    }

    [Fact]
    public void Every_set_of_notes_is_heard_once_and_kept_when_files_are_read_again()
    {
        var dir = Fixtures.TempDir();
        var lib = Path.Combine(dir, "MIDI");
        Directory.CreateDirectory(lib);
        static MidiClip Clip(string track, params int[] keys)
        {
            var clip = new MidiClip { Name = track, Ppq = 96 };
            clip.Track(track).AddRange(keys.Select((k, i) => new NoteEvent(i * 96L, 96, k, 100)));
            return clip;
        }
        File.WriteAllBytes(Path.Combine(lib, "Bass 1.mid"), MidiWriter.Write(Clip("Bass", 33, 33, 41, 41, 36, 36, 43, 43), 120));
        File.WriteAllBytes(Path.Combine(lib, "Bass copy.mid"), MidiWriter.Write(Clip("Bass", 33, 33, 41, 41, 36, 36, 43, 43), 120));
        File.WriteAllBytes(Path.Combine(lib, "Lead.mid"), MidiWriter.Write(Clip("Lead", 69, 72, 71, 67, 69, 64, 65, 67), 120));

        using var db = new LibraryDb(Path.Combine(dir, "library.db"));
        db.SetList(MidiIndexer.FoldersKey, [lib]);
        using var index = new MidiIndex(MidiIndex.PathBeside(db.FilePath));
        new MidiIndexer(index, db).Run();
        using var model = new Clamp3Model(Folder);

        var first = MidiEmbedder.Run(index, model);
        Assert.Equal(2, first.Heard);   // the copy has the same notes, so it shares the first one's
        Assert.Equal(0, first.Failed);
        var meanings = index.Embeddings(Clamp3Model.Version);
        Assert.Equal(2, meanings.Count);
        Assert.All(index.All(), p => Assert.True(meanings.ContainsKey(p.Hash)));

        // Reading the files again (a new version of the analysis) keeps what was heard.
        index.ReplaceMany(index.All().GroupBy(p => p.Source.Path).Select(g => (g.First().Source, 0,
            (IReadOnlyList<IndexedPhrase>)MidiIndexer.FromFile(g.Key, [lib]), (string?)null)).ToList());
        Assert.Equal(0, MidiEmbedder.Run(index, model).Heard);

        var catalogue = new MidiCatalogue(index.All(), meanings);
        Assert.Equal(catalogue.Entries.Count, catalogue.HeardCount);
        Assert.NotEmpty(catalogue.Search(new MidiQuery("bass", Words: model.EmbedText("bass")), out _));
    }
}
