using CommunityToolkit.Mvvm.ComponentModel;
using MusicOrganiser.Core.Library;
using MusicOrganiser.Core.Midi;

namespace MusicOrganiser.ViewModels;

/// <summary>
/// The MIDI page's numbers that were fixed in code, as options on Settings' MIDI tab (Solomon, 2026-10-18: "things that
/// are hardcoded … should be available to change as an option").
/// </summary>
public sealed partial class MidiBrowserViewModel
{
    public const string ListLimitKey = "midi_list_limit";
    public const string SongTabLimitKey = "midi_song_tab_limit";
    public const string GoodFitKey = "midi_good_fit_percent";
    public const string QuantiseKey = "midi_quantise";
    public const string LongestLoopKey = "midi_longest_loop_bars";

    public static IReadOnlyList<int> ListLimitChoices { get; } = [100, 250, 500, 1000, 2000];
    public static IReadOnlyList<int> SongTabLimitChoices { get; } = [10, 20, 40, 80];
    public static IReadOnlyList<int> GoodFitChoices { get; } = [50, 60, 70, 80, 90];
    public static IReadOnlyList<int> QuantiseChoices { get; } = [4, 8, 16, 32];
    public static IReadOnlyList<int> LongestLoopChoices { get; } = [8, 16, 32, 64];

    /// <summary>How many parts the list shows. Fitting gives each one shown the closer look, so more is slower.</summary>
    [ObservableProperty] int _listLimit = 500;
    /// <summary>How many parts of each kind a song's "MIDI that fits" tab shows.</summary>
    [ObservableProperty] int _songTabLimit = SongMidiViewModel.Limit;
    /// <summary>A fit this good (percent) or better counts towards a song's "N good fits".</summary>
    [ObservableProperty] int _goodFitPercent = (int)Math.Round(SongMidiViewModel.GoodFit * 100);
    /// <summary>The piano roll's Quantise snaps to this note: 16 for 16ths.</summary>
    [ObservableProperty] int _quantiseTo = 16;
    /// <summary>Two parts of different lengths go round together after both have finished, up to this many bars.</summary>
    [ObservableProperty] int _longestLoopBars = 32;

    void LoadOptions(LibraryDb db)
    {
        int Read(string key, int fallback, IReadOnlyList<int> choices) =>
            int.TryParse(db.GetSetting(key), out var v) && choices.Contains(v) ? v : fallback;
        _listLimit = Read(ListLimitKey, _listLimit, ListLimitChoices);
        _songTabLimit = Read(SongTabLimitKey, _songTabLimit, SongTabLimitChoices);
        _goodFitPercent = Read(GoodFitKey, _goodFitPercent, GoodFitChoices);
        _quantiseTo = Read(QuantiseKey, _quantiseTo, QuantiseChoices);
        _longestLoopBars = Read(LongestLoopKey, _longestLoopBars, LongestLoopChoices);
    }

    partial void OnListLimitChanged(int value)
    {
        _db.SetSetting(ListLimitKey, value.ToString());
        _ = RefreshAsync();
    }

    partial void OnSongTabLimitChanged(int value) => _db.SetSetting(SongTabLimitKey, value.ToString());
    partial void OnGoodFitPercentChanged(int value) => _db.SetSetting(GoodFitKey, value.ToString());
    partial void OnQuantiseToChanged(int value) => _db.SetSetting(QuantiseKey, value.ToString());

    partial void OnLongestLoopBarsChanged(int value)
    {
        _db.SetSetting(LongestLoopKey, value.ToString());
        // How the parts shown pair up changes with it.
        foreach (var row in Results) row.EditsChanged();
        _ = RefreshPlayingAsync();
    }

    /// <summary>The grid the piano roll's Quantise snaps to, in ticks.</summary>
    internal int QuantiseGrid => 4 * PhraseAnalyser.Ppq / Math.Max(1, QuantiseTo);
}
