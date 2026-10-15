using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using MusicOrganiser.Core.Library;

namespace MusicOrganiser.ViewModels;

/// <summary>
/// Settings › Sound output: where the app's sounds play. The computer's default comes first, then each output by
/// name (an audio interface, say). The choice is saved, used from the first sound on, and takes effect at once.
/// </summary>
public sealed partial class SoundOutputSetting : ObservableObject
{
    public const string Key = "sound_output";

    readonly LibraryDb _db;
    readonly ISoundOutputs? _outputs;
    /// <summary>Set while the list is refilled: a selection then isn't a new choice.</summary>
    bool _filling;
    /// <summary>Counts choices, so only the latest one's answer shows.</summary>
    int _choosing;

    public SoundOutputSetting(LibraryDb db, ISoundOutputs? outputs)
    {
        _db = db;
        _outputs = outputs;
        var saved = Saved;
        outputs?.Prefer(saved);
        Choices.Add(DefaultChoice(null));
        if (saved is not null) Choices.Add(new OutputChoice(saved, saved));
        _selected = Choices[^1];
    }

    /// <summary>False where the app can't choose (the WPF app), and Settings leaves the card out.</summary>
    public bool IsAvailable => _outputs is not null;

    public ObservableCollection<OutputChoice> Choices { get; } = [];

    [ObservableProperty] OutputChoice? _selected;

    /// <summary>Where sounds go now, or why they can't go where chosen.</summary>
    [ObservableProperty] string _note = "";

    [ObservableProperty] bool _busy;

    string? Saved => _db.GetSetting(Key) is { Length: > 0 } name ? name : null;

    /// <summary>The first line. On Windows it also says which output that is now: Windows lists it first.</summary>
    static OutputChoice DefaultChoice(string? now) =>
        OperatingSystem.IsWindows()
            ? new(null, now is null ? "Windows' default output" : $"Windows' default output (now {now})")
            : new(null, "The computer's default output");

    /// <summary>Lists the outputs there are now and says where sounds go. Settings calls it each time it opens.</summary>
    [RelayCommand]
    public async Task RefreshAsync()
    {
        if (_outputs is null) return;
        Busy = true;
        try
        {
            var (names, note) = await Task.Run(() => (_outputs.List(), _outputs.Check()));
            var saved = Saved;
            _filling = true;
            try
            {
                Choices.Clear();
                Choices.Add(DefaultChoice(names.FirstOrDefault()));
                foreach (var name in names) Choices.Add(new OutputChoice(name, name));
                if (saved is not null && !names.Contains(saved)) Choices.Add(new OutputChoice(saved, $"{saved} (not there now)"));
                Selected = Choices.First(c => c.Name == saved);
            }
            finally { _filling = false; }
            Note = note;
        }
        catch (Exception e) { Note = $"Couldn't look at the sound outputs: {e.Message}"; }
        finally { Busy = false; }
    }

    partial void OnSelectedChanged(OutputChoice? value)
    {
        if (_filling || value is null || _outputs is null) return;
        _db.SetSetting(Key, value.Name ?? "");
        _ = ChooseAsync(value.Name);
    }

    async Task ChooseAsync(string? name)
    {
        var mine = ++_choosing;
        Busy = true;
        Note = "Moving sound there…";
        try
        {
            var note = await Task.Run(() => _outputs!.Choose(name));
            if (mine == _choosing) Note = note;
        }
        catch (Exception e) { if (mine == _choosing) Note = $"Couldn't change the output: {e.Message}"; }
        finally { if (mine == _choosing) Busy = false; }
    }
}

/// <summary>A line in the Sound output list: the output's name (null for the computer's default) and how it shows.</summary>
public sealed record OutputChoice(string? Name, string Label)
{
    public override string ToString() => Label;
}
