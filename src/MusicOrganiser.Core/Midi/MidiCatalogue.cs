using MusicOrganiser.Core.Library;
using MusicOrganiser.Core.Sounds;

namespace MusicOrganiser.Core.Midi;

/// <summary>
/// One MIDI part as the browser shows it: the newest copy of some notes, with how many other places have the same
/// notes (a pattern kept through 20 saves of a song is one entry, "in 20 files").
/// </summary>
public sealed class MidiEntry
{
    public required StoredPhrase Phrase { get; init; }
    public int Copies => 1 + (OtherSources?.Count ?? 0);
    /// <summary>The other files with the same notes (older saves of the song, or the same pattern in another project).</summary>
    public List<string>? OtherSources { get; set; }
    public string SearchText { get; set; } = "";
    /// <summary>What you said it is, which wins over the app's guess; null for the guess.</summary>
    public string? YourRole { get; set; }
    public IReadOnlyList<PhraseChord> Chords { get; init; } = [];
    public float[] Chroma { get; init; } = new float[12];
    /// <summary>The instrument it's for, when its names, plugin, channel or General MIDI sound say; else null.</summary>
    public InstrumentGuess? Instrument { get; init; }
    /// <summary>What CLaMP 3 heard in it (see <see cref="Clamp3Model"/>), or null when the model hasn't heard it yet.</summary>
    public sbyte[]? Meaning { get; set; }

    public long Id => Phrase.Id;

    IReadOnlyList<PhraseMember>? _members;
    /// <summary>For a phrase joined from clips that play back to back: those clips, and where each sits in it.</summary>
    public IReadOnlyList<PhraseMember> Members => _members ??= PhraseMember.Decode(Phrase.Members);
    public bool IsJoined => Phrase.Members.Length > 0;
    /// <summary>How many different clips the phrase was joined from.</summary>
    public int ClipCount => Members.Select(m => m.Hash).Distinct().Count();
    /// <summary>The longer phrases these notes are part of, longest first; null when none.</summary>
    public List<MidiEntry>? Within { get; set; }

    /// <summary>
    /// Whether this song has these notes, in this save or any other save of it (Song_2 … Song_36 in the same folder),
    /// as the newest copy or one of the others.
    /// </summary>
    public bool IsIn(string path) => IsInSong(SongOf(path));

    string[]? _songs;
    /// <summary><see cref="IsIn"/> for a song already worked out with <see cref="SongOf"/>.</summary>
    public bool IsInSong(string song) =>
        Array.IndexOf(_songs ??= [SongOf(Phrase.Source.Path), .. OtherSources?.Select(SongOf) ?? []], song) >= 0;

    /// <summary>The song a file is a save of: its folder and its name without the save number.</summary>
    public static string SongOf(string path) =>
        Path.Combine(Path.GetDirectoryName(path) ?? "", SongVersions.Split(Path.GetFileNameWithoutExtension(path)).Song).ToLowerInvariant();
    public string Role => YourRole ?? Phrase.Role;
    public bool IsProject => Phrase.Source.Kind == MidiSource.Project;
    public string SourceName => Path.GetFileNameWithoutExtension(Phrase.Source.Path);

    /// <summary>"Bass · Pattern 3" for a project's part; "Dark keys 01" (or "Dark keys 01 · Piano") for a file.</summary>
    public string Title => IsProject
        ? string.Join(" · ", new[] { Phrase.Part, Phrase.Clip }.Where(s => s.Length > 0))
        : string.Join(" · ", new[] { Phrase.Clip, Phrase.Part }.Where(s => s.Length > 0));

    public string KeyText => Phrase.Key is { Confidence: >= 0.05 } k ? k.Short : "";
}

/// <summary>What to look for.</summary>
/// <param name="Role">Only this kind of part (<see cref="MidiRoles"/>), or null for every kind.</param>
/// <param name="From">"projects", "files" or null for both.</param>
/// <param name="Fit">Rank by how well each part fits this harmony, moved to fit it.</param>
/// <param name="Like">Rank by how alike each part is to this one.</param>
/// <param name="Leave">Leave out the parts in this file: the project being fitted to fits itself, which says nothing.</param>
/// <param name="Words">What CLaMP 3 makes of <paramref name="Text"/>: when given, the words are read as a description and
/// parts are ranked by how close they sound to it, instead of only matching names.</param>
/// <param name="Within">Only the longer phrases this part is in.</param>
/// <param name="NameWeight">How much words found in a part's names, folders and kind count against the model when
/// searching by description: 1 as usual, more to let names lead, 0 for the model's ears only.</param>
public sealed record MidiQuery(string Text = "", string? Role = null, string? From = null, HarmonyTarget? Fit = null,
                               MidiEntry? Like = null, int Limit = 500, string? Leave = null, float[]? Words = null,
                               MidiEntry? Within = null, float NameWeight = 1);

/// <summary>A part found, with how it fits (when matching a harmony) or how alike it is (for "more like this").</summary>
public sealed record MidiHit(MidiEntry Entry, HarmonyFit? Fit = null, double? Likeness = null);

/// <summary>
/// Every MIDI part in the index, held in memory for instant searching. Search words must all appear in a part's names,
/// file, folders, kind, key or chords. A harmony to fit ranks every part by how well it fits once transposed; "more like
/// this" ranks by shape. Each is a pass over the list, quick even for hundreds of thousands of parts.
/// </summary>
public sealed class MidiCatalogue
{
    public IReadOnlyList<MidiEntry> Entries { get; }
    public int PhraseCount { get; }

    public MidiCatalogue(IReadOnlyList<StoredPhrase> phrases, IReadOnlyDictionary<string, sbyte[]>? meanings = null)
    {
        PhraseCount = phrases.Count;
        // Phrases come newest file first, so the first of each set of notes is the newest copy.
        var byHash = new Dictionary<string, MidiEntry>(phrases.Count);
        var entries = new List<MidiEntry>();
        foreach (var p in phrases)
        {
            if (byHash.TryGetValue(p.Hash, out var seen))
            {
                (seen.OtherSources ??= []).Add(p.Source.Path);
                continue;
            }
            var chords = PhraseChord.Decode(p.Chords);
            var instrument = InstrumentOf(p);
            var entry = new MidiEntry
            {
                Phrase = p, Chords = chords, Chroma = MidiIndex.Floats(p.Chroma), YourRole = p.YourRole, Instrument = instrument,
                SearchText = SearchTextOf(p, p.YourRole ?? p.Role, chords, instrument),
                Meaning = meanings?.GetValueOrDefault(p.Hash),
            };
            byHash[p.Hash] = entry;
            entries.Add(entry);
        }
        Entries = entries;

        // Each clip learns which longer phrases it's part of.
        foreach (var phrase in entries.Where(e => e.IsJoined))
            foreach (var hash in phrase.Members.Select(m => m.Hash).Distinct())
                if (byHash.TryGetValue(hash, out var member) && member != phrase)
                    (member.Within ??= []).Add(phrase);
        foreach (var e in entries)
            e.Within?.Sort((a, b) => b.Phrase.Bars.CompareTo(a.Phrase.Bars));
    }

    /// <summary>How many parts CLaMP 3 has heard.</summary>
    public int HeardCount => Entries.Count(e => e.Meaning is not null);

    /// <summary>Adds what CLaMP 3 heard to the parts it has heard since the catalogue was made.</summary>
    public void AddMeanings(IReadOnlyDictionary<string, sbyte[]> meanings)
    {
        foreach (var e in Entries)
            if (e.Meaning is null && meanings.TryGetValue(e.Phrase.Hash, out var m)) e.Meaning = m;
    }

    /// <summary>Changes what kind of part this is (null: back to the app's guess), for searching and the chips too.</summary>
    public static void Relabel(MidiEntry e, string? role)
    {
        e.YourRole = role;
        e.SearchText = SearchTextOf(e.Phrase, e.Role, e.Chords, e.Instrument);
    }

    /// <summary>
    /// The instrument a part is for, from what's attached to it: its track (or channel) name and its clip; for a .mid
    /// file also the file's name and its folders from the nearest up (packs sort into folders like "Piano"), though in
    /// a project those name the song, not the part; channel 10 or a drum plugin; the General MIDI sound a .mid file sets.
    /// </summary>
    public static InstrumentGuess? InstrumentOf(StoredPhrase p)
    {
        var isFile = p.Source.Kind == MidiSource.File;
        string[] names = isFile
            ? [p.Part, p.Clip, Path.GetFileNameWithoutExtension(p.Source.Path),
               .. (Path.GetDirectoryName(p.Source.Path) ?? "").Split(['\\', '/'], StringSplitOptions.RemoveEmptyEntries).TakeLast(3).Reverse()]
            : [p.Part, p.Clip];
        var guess = Instruments.Guess(names, channel10: p.Drums && isFile, program: p.Program);
        // A project track flagged as drums plays an unpitched plugin (a drum machine or sampler of hits).
        return guess ?? (p.Drums ? new InstrumentGuess(Instruments.Drums, $"“{p.Part}” is a drum plugin") : null);
    }

    static string SearchTextOf(StoredPhrase p, string role, IReadOnlyList<PhraseChord> chords, InstrumentGuess? instrument)
    {
        var parts = new List<string> { p.Part, p.Clip, Path.GetFileNameWithoutExtension(p.Source.Path), role, MidiRoles.Plural(role), p.Source.Daw };
        if (instrument is not null) parts.Add(Instruments.SearchWords(instrument.Name));
        if (p.Program is { } program and >= 0 and < 128) parts.Add(Instruments.GeneralMidi[program]);
        // The folders it's in (the last few): MIDI packs are sorted into folders like "Chords" and "Trap Melodies".
        parts.AddRange((Path.GetDirectoryName(p.Source.Path) ?? "").Split(['\\', '/'], StringSplitOptions.RemoveEmptyEntries).TakeLast(4));
        if (p.Key is { Confidence: >= 0.05 } k) parts.AddRange([k.Name, k.Short]);
        parts.AddRange(chords.Select(c => c.Name).Distinct());
        if (role == MidiRoles.Bass) parts.Add("bassline");
        if (role == MidiRoles.Chords) parts.Add("progression");
        return string.Join(' ', parts).ToLowerInvariant();
    }

    /// <summary>
    /// How much each word found in a part's names, kind, key or chords lifts it when searching by description. The model
    /// hears notes, not sounds, so it knows a bassline from chords but not a pad from a piano; checked on 6,000 real parts,
    /// a lift per word (rather than 0.08 shared between the words) put the right kind in the top 50 far more often
    /// ("trance arp" 46% → 72%, "sad piano melody" 60% → 80%) while keeping the model's order among them.
    /// </summary>
    const float WordBonus = 0.06f;

    public IReadOnlyList<MidiHit> Search(MidiQuery q, out int matching, CancellationToken ct = default)
    {
        if (q.Within is { } part)
        {
            var phrases = part.Within ?? [];
            matching = phrases.Count;
            // Still moved into the song's key when fitting.
            return phrases.Take(q.Limit).Select(e => q.Fit is { } song ? new MidiHit(e, HarmonyMatcher.Fit(e.Chroma, e.Chords, song)) : new MidiHit(e)).ToList();
        }
        var words = q.Text.ToLowerInvariant().Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (q.Words is { } described && words.Length > 0 && q.Fit is null && q.Like is null)
            return Describe(q, words, described, out matching, ct);
        var leave = q.Leave is null ? null : MidiEntry.SongOf(q.Leave);
        var pool = Entries.Where(e =>
            (q.Role is null ? q.Fit is null || e.Role != MidiRoles.Drums : e.Role == q.Role) &&   // drums have no harmony to fit
            (q.From is null || (q.From == "projects") == e.IsProject) &&
            !ReferenceEquals(e, q.Like) &&
            (leave is null || !e.IsInSong(leave)) &&
            words.All(w => e.SearchText.Contains(w, StringComparison.Ordinal))).ToList();
        matching = pool.Count;
        ct.ThrowIfCancellationRequested();

        if (q.Fit is { } target)
        {
            // A quick look at the notes alone picks the likeliest few thousand; those get the full look, chords and all.
            var shortlist = pool.Select(e => (e, Quick: HarmonyMatcher.QuickScore(e.Chroma, target)))
                                .OrderByDescending(x => x.Quick).Take(Math.Max(q.Limit * 4, 2000)).Select(x => x.e).ToList();
            ct.ThrowIfCancellationRequested();
            return shortlist.Select(e => new MidiHit(e, HarmonyMatcher.Fit(e.Chroma, e.Chords, target)))
                            .OrderByDescending(h => h.Fit!.Score).ThenByDescending(h => h.Entry.Copies)
                            .Take(q.Limit).ToList();
        }
        if (q.Like is { } like)
        {
            var mine = MidiIndex.Floats(like.Phrase.Features);
            // Shape (the app's own measurements) and, once CLaMP 3 has heard both, how alike they sound, half and half.
            return pool.Select(e => new MidiHit(e, Likeness: Likeness(like, e, mine)))
                       .OrderByDescending(h => h.Likeness).Take(q.Limit).ToList();
        }
        return pool.Take(q.Limit).Select(e => new MidiHit(e)).ToList();
    }

    static double Likeness(MidiEntry like, MidiEntry e, float[] likeFeatures)
    {
        var shape = PhraseAnalyser.Likeness(likeFeatures, MidiIndex.Floats(e.Phrase.Features));
        return like.Meaning is { } a && e.Meaning is { } b ? 0.5 * shape + 0.5 * Math.Max(0, Clamp3Model.Similarity(a, b)) : shape;
    }

    /// <summary>
    /// Search by description: every part (of the chosen kind and place) ranked by how close CLaMP 3 thinks it sounds to
    /// the words, lifted a little for each word that also appears in its names, kind, key or chords. Parts the model
    /// hasn't heard yet still show up when all the words match their names, as they do without the model.
    /// </summary>
    IReadOnlyList<MidiHit> Describe(MidiQuery q, string[] words, float[] described, out int matching, CancellationToken ct)
    {
        var leave = q.Leave is null ? null : MidiEntry.SongOf(q.Leave);
        var scored = new List<(MidiEntry Entry, float Score)>();
        foreach (var e in Entries)
        {
            if ((q.Role is not null && e.Role != q.Role) || (q.From is not null && (q.From == "projects") != e.IsProject) ||
                (leave is not null && e.IsInSong(leave))) continue;
            var found = q.NameWeight > 0 ? words.Count(w => e.SearchText.Contains(w, StringComparison.Ordinal)) : 0;
            var lift = WordBonus * q.NameWeight * found;
            if (e.Meaning is { } m) scored.Add((e, Clamp3Model.Similarity(described, m) + lift));
            // Parts not heard yet only show when their names have every word (and names count at all).
            else if (q.NameWeight > 0 && found == words.Length) scored.Add((e, lift));
            if (scored.Count % 8192 == 0) ct.ThrowIfCancellationRequested();
        }
        matching = scored.Count;
        return scored.OrderByDescending(s => s.Score).Take(q.Limit).Select(s => new MidiHit(s.Entry)).ToList();
    }

    /// <summary>How many parts of each kind there are, for the chips.</summary>
    public IReadOnlyDictionary<string, int> RoleCounts() =>
        Entries.GroupBy(e => e.Role).ToDictionary(g => g.Key, g => g.Count());
}
