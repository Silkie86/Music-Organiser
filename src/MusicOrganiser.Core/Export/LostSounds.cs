using MusicOrganiser.Core.Library;
using MusicOrganiser.Core.Parsers;

namespace MusicOrganiser.Core.Export;

/// <summary>
/// Fills in plugin sounds a save lost (<see cref="FlPlugin.NothingSaved"/>) from the song's older numbered saves in the same
/// folder. KORG LegacyCell gives FL nothing when FL 25 saves, so Solomon's funk step_9 lost the sound that funk step_8
/// (saved in FL 9) still has, and converting funk step_9 takes it from there.
/// </summary>
public static class LostSounds
{
    /// <summary>
    /// The song with each lost sound taken from the newest older save that has the same plugin on the same channel (or
    /// mixer slot), and a line for each sound taken, naming the save it came from.
    /// </summary>
    public static (FlSong Song, IReadOnlyList<string> Taken) FromOlderSaves(FlSong song, string path)
    {
        if (!Lost(song)) return (song, []);
        var (name, number) = SongVersions.Split(Path.GetFileNameWithoutExtension(path));
        var older = Directory.EnumerateFiles(Path.GetDirectoryName(Path.GetFullPath(path))!, "*.flp")
            .Select(f => (File: f, Version: SongVersions.Split(Path.GetFileNameWithoutExtension(f))))
            .Where(f => f.Version.Song.Equals(name, StringComparison.OrdinalIgnoreCase) && f.Version.Number < number)
            .OrderByDescending(f => f.Version.Number)
            .Select(f => f.File)
            .ToList();

        var taken = new List<string>();
        foreach (var file in older)
        {
            FlSong old;
            try { old = Readers.FlStudio.ReadSong(file); }
            catch (Exception) { continue; }   // a damaged save, or one this reader can't read
            var from = Path.GetFileNameWithoutExtension(file);

            var channels = song.Channels.Select(c =>
            {
                if (c.Instrument is not { NothingSaved: true } p) return c;
                var same = old.Channels.Where(o => Same(o.Instrument, p)).ToList();
                var match = same.FirstOrDefault(o => o.Index == c.Index && o.Name == c.Name) ??
                            same.FirstOrDefault(o => o.Name == c.Name) ?? same.FirstOrDefault(o => o.Index == c.Index);
                if (match is null) return c;
                taken.Add($"{c.Name}: {p.Name}'s sound, from {from}");
                return c with { Instrument = p with { State = match.Instrument!.State, NothingSaved = false } };
            }).ToList();

            var inserts = song.Inserts.Select(i =>
            {
                if (!i.Effects.Any(e => e.NothingSaved)) return i;
                var oldEffects = old.Inserts.FirstOrDefault(o => o.Index == i.Index)?.Effects ?? [];
                return i with
                {
                    Effects = i.Effects.Select(e =>
                    {
                        if (!e.NothingSaved) return e;
                        var same = oldEffects.Where(o => Same(o, e)).ToList();
                        if ((same.FirstOrDefault(o => o.Slot == e.Slot) ?? same.FirstOrDefault()) is not { } match) return e;
                        taken.Add($"{(i.Name.Length > 0 ? i.Name : $"Insert {i.Index}")}: {e.Name}'s sound, from {from}");
                        return e with { State = match.State, NothingSaved = false };
                    }).ToList(),
                };
            }).ToList();

            song = song with { Channels = channels, Inserts = inserts };
            if (!Lost(song)) break;
        }
        return (song, taken);
    }

    static bool Lost(FlSong song) =>
        song.Channels.Any(c => c.Instrument is { NothingSaved: true }) || song.Inserts.Any(i => i.Effects.Any(e => e.NothingSaved));

    /// <summary>The same plugin, with a sound saved for it.</summary>
    static bool Same(FlPlugin? old, FlPlugin lost) =>
        old is { State: not null } && old.Format == lost.Format &&
        (lost.DeviceId.Length > 0 ? old.DeviceId == lost.DeviceId : old.Name.Equals(lost.Name, StringComparison.OrdinalIgnoreCase));
}
