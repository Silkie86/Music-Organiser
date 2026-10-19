using CommunityToolkit.Mvvm.ComponentModel;
using MusicOrganiser.Core.Preview;

namespace MusicOrganiser.ViewModels;

/// <summary>The arrangement at the top of Overview: the project drawn as its DAW's arrangement, read when Overview is first shown.</summary>
public sealed partial class ProjectDetailViewModel
{
    /// <summary>The ways the project can be laid out (FL Studio: its playlist, and how it opens in Bitwig).</summary>
    [ObservableProperty] IReadOnlyList<Arrangement> _arrangements = [];
    [ObservableProperty] Arrangement? _arrangement;
    [ObservableProperty] bool _arrangementLoading;
    [ObservableProperty] string _arrangementMessage = "";
    bool _arrangementRead;

    /// <summary>Whether this DAW's projects can be drawn.</summary>
    public bool CanPreview => Item.Daw is "FL Studio" or "Bitwig Studio" or "Studio One" && !Item.FileMissing;
    public bool HasArrangementChoice => Arrangements.Count > 1;
    public bool HasArrangement => Arrangement is { Lanes.Count: > 0 };
    public IReadOnlyList<string> ArrangementNotes => Arrangement?.Notes ?? [];
    public bool HasArrangementNotes => ArrangementNotes.Count > 0;

    /// <summary>"140 BPM · 32 bars · 0:55 · 6 tracks · 18 clips · 412 notes".</summary>
    public string ArrangementSummary
    {
        get
        {
            if (Arrangement is not { } a) return "";
            var parts = new List<string> { $"{a.Tempo:0.##} BPM" };
            if (a.Length > 0)
            {
                parts.Add($"{Math.Ceiling(a.Bars - 1e-6):0} bar{(Math.Ceiling(a.Bars - 1e-6) == 1 ? "" : "s")}");
                parts.Add(TimeSpan.FromSeconds(a.Seconds).ToString(a.Seconds >= 3600 ? @"h\:mm\:ss" : @"m\:ss"));
            }
            parts.Add(Count(a.Lanes.Count(l => l.Kind != LaneKind.Group), "track"));
            parts.Add(Count(a.ClipCount, "clip"));
            if (a.NoteCount > 0) parts.Add(Count(a.NoteCount, "note"));
            return string.Join(" · ", parts);
        }
    }

    static string Count(int n, string what) => $"{n:N0} {what}{(n == 1 ? "" : "s")}";

    /// <summary>Set by the view while Overview is showing; the first time, the project is read.</summary>
    public bool PreviewShown
    {
        get => _previewShown;
        set
        {
            _previewShown = value;
            if (value && !_arrangementRead) _ = ReadArrangementAsync();
        }
    }
    bool _previewShown;

    async Task ReadArrangementAsync()
    {
        _arrangementRead = true;
        // Other DAWs, and missing files, hide the arrangement altogether (see the Overview layout), so there's nothing to say.
        if (!CanPreview) return;
        ArrangementLoading = true;
        try
        {
            var path = Item.Path;
            Arrangements = await Task.Run(() => ArrangementReader.Read(path));
            Arrangement = Arrangements.FirstOrDefault();
            if (Arrangement is null) ArrangementMessage = "This project has no arrangement to draw.";
        }
        catch (Exception e)   // a damaged or unusual file: say so here rather than leave the tab blank
        {
            ArrangementMessage = $"Couldn't read the arrangement: {e.Message}";
        }
        finally { ArrangementLoading = false; }
    }

    partial void OnArrangementsChanged(IReadOnlyList<Arrangement> value) => OnPropertyChanged(nameof(HasArrangementChoice));

    partial void OnArrangementChanged(Arrangement? value)
    {
        OnPropertyChanged(nameof(HasArrangement));
        OnPropertyChanged(nameof(ArrangementSummary));
        OnPropertyChanged(nameof(ArrangementNotes));
        OnPropertyChanged(nameof(HasArrangementNotes));
    }
}
