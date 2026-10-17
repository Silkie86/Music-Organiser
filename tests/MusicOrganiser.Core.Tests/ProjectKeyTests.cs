using MusicOrganiser.Core.Library;
using MusicOrganiser.Core.Parsers;
using Xunit;

namespace MusicOrganiser.Core.Tests;

public class ProjectKeyTests
{
    const int Ppq = 96;

    /// <summary>Chords one bar each, every note held for the bar.</summary>
    static MidiChannel Chords(string name, params int[][] chords) => new(name, "", Ppq,
        chords.SelectMany((c, bar) => c.Select(k => new NoteEvent(bar * 4L * Ppq, 4L * Ppq, k, 100))).ToList(), Plugin: "Serum");

    // A minor: Am, Dm, E (its G# is what makes it minor, not C major), Am.
    static readonly int[][] AMinor = [[57, 60, 64], [62, 65, 69], [64, 68, 71], [57, 60, 64]];

    [Fact]
    public void Chords_point_to_their_key()
    {
        Assert.Equal("A minor", ProjectKey.Estimate([Chords("Keys", AMinor)])?.Name);
    }

    [Fact]
    public void Drums_and_sample_channels_are_left_out()
    {
        // A hat on F# all the way through, and a sampled vocal chop on B flat: neither says anything about the key.
        var hat = new MidiChannel("Hi hat", "", Ppq, Enumerable.Range(0, 64).Select(i => new NoteEvent(i * Ppq / 4L, Ppq / 4L, 66, 90)).ToList());
        var chop = new MidiChannel("Chop", "vox.wav", Ppq, Enumerable.Range(0, 16).Select(i => new NoteEvent(i * Ppq, 4L * Ppq, 70 + i % 3, 90)).ToList(),
                                   SamplePath: @"C:\Samples\vox.wav");
        Assert.Equal("A minor", ProjectKey.Estimate([hat, chop, Chords("Keys", AMinor)])?.Name);
        Assert.Null(ProjectKey.Estimate([hat, chop]));
    }

    [Fact]
    public void A_few_notes_are_not_enough()
    {
        Assert.Null(ProjectKey.Estimate([Chords("Keys", [57, 60, 64])]));
    }

    [Fact]
    public void The_library_keeps_the_key_and_reads_projects_from_before_it_again()
    {
        var dir = Fixtures.TempDir();
        using var db = new LibraryDb(Path.Combine(dir, "library.db"));
        var file = Path.Combine(dir, "Song.flp");
        File.WriteAllBytes(file, [0]);
        db.Upsert(new LibraryDb.IndexedFile(file, 1, 100, null, null, [], []) { DetectedKey = "A minor" });
        Assert.Equal("A minor", db.Projects().Single().DetectedKey);
        Assert.Equal(100, db.KnownFiles()[file].Mtime);

        // A library from before keys were worked out has no key yet: the next scan reads the project again.
        using (var sql = new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={db.FilePath}"))
        {
            sql.Open();
            using var cmd = sql.CreateCommand();
            cmd.CommandText = "UPDATE projects SET detected_key = NULL";
            cmd.ExecuteNonQuery();
        }
        Assert.Equal(-1, db.KnownFiles()[file].Mtime);
    }
}
