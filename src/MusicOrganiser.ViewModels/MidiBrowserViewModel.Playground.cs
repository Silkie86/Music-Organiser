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

    // The part picked, or what it's fitted to, changed: the small playground starts again with them, once the list rests
    // (arrowing down the list doesn't build one per row).
    void QuickFollow() => _ = QuickFollowAsync();

    async Task QuickFollowAsync()
    {
        var request = ++_quickRequest;
        Quick?.StopPlaying();
        if (Fit is not { Notes.Count: > 0 } fit || Selected is not { BarNotes.Count: > 0 } row) return;
        if (UiWork.InBackground) await Task.Delay(150);
        if (request != _quickRequest) return;
        Quick ??= _main.Playground.Quick();
        // A song's parts where they play, or the one part fitted to (from its first bar, round and round).
        IReadOnlyList<MidiChannel> partners = fit.Parts.Count > 0 ? fit.Parts : [new MidiChannel(fit.Name, "", PhraseAnalyser.Ppq, fit.Notes)];
        await Quick.LoadPairAsync(fit.Name, partners, fit.Loops ? fit.Length : 0, _fitPath, row.Fit?.At ?? 0, row.Entry, row.BarNotes, row.Title);
    }
}
