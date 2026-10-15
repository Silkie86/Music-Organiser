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

    public long Id => Phrase.Id;

    /// <summary>Whether this file has these notes (as the newest copy or one of the others).</summary>
    public bool IsIn(string path) =>
        Phrase.Source.Path.Equals(path, StringComparison.OrdinalIgnoreCase) ||
        OtherSources?.Any(o => o.Equals(path, StringComparison.OrdinalIgnoreCase)) == true;
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
public sealed record MidiQuery(string Text = "", string? Role = null, string? From = null, HarmonyTarget? Fit = null,
                               MidiEntry? Like = null, int Limit = 500, string? Leave = null);

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

    public MidiCatalogue(IReadOnlyList<StoredPhrase> phrases)
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
            var entry = new MidiEntry
            {
                Phrase = p, Chords = chords, Chroma = MidiIndex.Floats(p.Chroma), YourRole = p.YourRole,
                SearchText = SearchTextOf(p, p.YourRole ?? p.Role, chords),
            };
            byHash[p.Hash] = entry;
            entries.Add(entry);
        }
        Entries = entries;
    }

    /// <summary>Changes what kind of part this is (null: back to the app's guess), for searching and the chips too.</summary>
    public static void Relabel(MidiEntry e, string? role)
    {
        e.YourRole = role;
        e.SearchText = SearchTextOf(e.Phrase, e.Role, e.Chords);
    }

    static string SearchTextOf(StoredPhrase p, string role, IReadOnlyList<PhraseChord> chords)
    {
        var parts = new List<string> { p.Part, p.Clip, Path.GetFileNameWithoutExtension(p.Source.Path), role, MidiRoles.Plural(role), p.Source.Daw };
        // The folders it's in (the last few): MIDI packs are sorted into folders like "Chords" and "Trap Melodies".
        parts.AddRange((Path.GetDirectoryName(p.Source.Path) ?? "").Split(['\\', '/'], StringSplitOptions.RemoveEmptyEntries).TakeLast(4));
        if (p.Key is { Confidence: >= 0.05 } k) parts.AddRange([k.Name, k.Short]);
        parts.AddRange(chords.Select(c => c.Name).Distinct());
        if (role == MidiRoles.Bass) parts.Add("bassline");
        if (role == MidiRoles.Chords) parts.Add("progression");
        return string.Join(' ', parts).ToLowerInvariant();
    }

    public IReadOnlyList<MidiHit> Search(MidiQuery q, out int matching, CancellationToken ct = default)
    {
        var words = q.Text.ToLowerInvariant().Split(' ', StringSplitOptions.RemoveEmptyEntries);
        var pool = Entries.Where(e =>
            (q.Role is null || e.Role == q.Role) &&
            (q.From is null || (q.From == "projects") == e.IsProject) &&
            !ReferenceEquals(e, q.Like) &&
            (q.Leave is null || !e.IsIn(q.Leave)) &&
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
            return pool.Select(e => new MidiHit(e, Likeness: PhraseAnalyser.Likeness(mine, MidiIndex.Floats(e.Phrase.Features))))
                       .OrderByDescending(h => h.Likeness).Take(q.Limit).ToList();
        }
        return pool.Take(q.Limit).Select(e => new MidiHit(e)).ToList();
    }

    /// <summary>How many parts of each kind there are, for the chips.</summary>
    public IReadOnlyDictionary<string, int> RoleCounts() =>
        Entries.GroupBy(e => e.Role).ToDictionary(g => g.Key, g => g.Count());
}
