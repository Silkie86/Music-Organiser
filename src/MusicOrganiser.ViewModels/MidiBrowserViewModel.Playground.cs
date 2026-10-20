using CommunityToolkit.Mvvm.ComponentModel;
using MusicOrganiser.Core.Midi;
using MusicOrganiser.Core.Parsers;

namespace MusicOrganiser.ViewModels;

/// <summary>
/// "What goes with this?" works in a cut-down Playground: the part picked and what it's fitted to as lanes, with the
/// Playground's piano roll, bars, loop points and harmony style (Solomon, 2026-10-19: "i want this and the playground to
/// be linked so when features are added to the playground the midi version gets those too").
/// </summary>
public sealed partial class MidiBrowserViewModel
{
    [ObservableProperty] PlaygroundViewModel? _quick;
    int _quickRequest;
    /// <summary>What the small playground was last built from, so a list refresh that changes none of it keeps your work there.</summary>
    object? _quickFrom;

    // The part picked, what it's fitted to, the bars being fitted, the part's notes or either octave changed: the small
    // playground starts again with them, once the list rests (arrowing down the list doesn't build one per row).
    void QuickFollow() => _ = QuickFollowAsync();

    async Task QuickFollowAsync()
    {
        var request = ++_quickRequest;
        if (Fit is not { Notes.Count: > 0 } fit || Selected is not { BarNotes.Count: > 0 } row)
        {
            Quick?.StopPlaying();
            return;
        }
        var section = _sectionTarget is not null && _frame is { } frame && Section is { } s ? s.Within(frame.Length) : (RollRange?)null;
        var from = (fit, row.Entry.Phrase.Hash, row.BarNotes, WithOctaves, section);
        if (Quick is not null && Equals(from, _quickFrom)) return;   // the list was only rebuilt
        Quick?.StopPlaying();
        if (UiWork.InBackground) await Task.Delay(150);
        if (request != _quickRequest) return;
        _quickFrom = from;
        Quick ??= _main.Playground.Quick();
        IReadOnlyList<MidiChannel> partners;
        long period, at;
        if (section is { } bars && _frame is { } f)
        {
            // Finding parts for some bars: the part plays over just those bars of what it's fitted to.
            partners = [new MidiChannel(fit.Name, "", PhraseAnalyser.Ppq, Cut(f.GuideAt(WithOctaves), bars.Start, bars.End)
                .Select(n => n with { Start = n.Start - bars.Start }).ToList())];
            (period, at) = (0, 0);
        }
        else
        {
            // A song's parts where they play, or the one part fitted to (from its first bar, round and round), in the
            // octave it's been moved to.
            var shift = 12 * WithOctaves;
            IEnumerable<MidiChannel> parts = fit.Parts.Count > 0 ? fit.Parts : [new MidiChannel(fit.Name, "", PhraseAnalyser.Ppq, fit.Notes)];
            partners = shift == 0 ? parts.ToList()
                : parts.Select(c => c.Kind == "Drums" ? c : c with { Notes = c.Notes.Select(n => n with { Key = Math.Clamp(n.Key + shift, 0, 127) }).ToList() }).ToList();
            (period, at) = (fit.Loops ? fit.Length : 0, row.Fit?.At ?? 0);
        }
        await Quick.LoadPairAsync(fit.Name, partners, period, _fitPath, at, row.Entry, row.BarNotes, row.Title, TempoValue);
    }
}
