using CommunityToolkit.Mvvm.ComponentModel;
using MusicOrganiser.Core.Preview;

namespace MusicOrganiser.ViewModels;

/// <summary>The Arrangement tab: the project drawn as its DAW's arrangement, read when the tab is first shown.</summary>
public sealed partial class ProjectDetailViewModel
{
    /// <summary>The ways the project can be laid out (FL Studio: its playlist, and how it opens in Bitwig).</summary>
    [ObservableProperty] IReadOnlyList<Arrangement> _arrangements = [];
    [ObservableProperty] Arrangement? _arrangement;
    [ObservableProperty] bool _arrangementLoading;
    [ObservableProperty] string _arrangementMessage = "";
    bool _arrangementRead;

    /// <summary>Whether this DAW's projects can be drawn.</summary>
    public bool CanPreview => Item.Daw is "FL Studio" or "Bitwig Studio" && !Item.FileMissing;
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

    /// <summary>Set by the view while the Arrangement tab is showing; the first time, the project is read.</summary>
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
        if (!CanPreview)
        {
            ArrangementMessage = Item.FileMissing
                ? "The project file isn't where it was, so it can't be drawn."
                : $"Drawing {Item.Daw} projects isn't possible yet. FL Studio and Bitwig projects can be drawn.";
            return;
        }
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
