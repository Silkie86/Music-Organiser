using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using MusicOrganiser.Core.Library;
using MusicOrganiser.Core.Sounds;

namespace MusicOrganiser.ViewModels;

/// <summary>Settings › Most liked: one slider per sign that you like a sound, saved as you move it.</summary>
public sealed partial class LikedSettingsViewModel : ObservableObject
{
    readonly LibraryDb _db;
    bool _loading;

    /// <summary>The Sounds page re-sorts when these change.</summary>
    public event Action<LikedWeights>? Changed;

    public LikedSettingsViewModel(LibraryDb db)
    {
        _db = db;
        Load(LikedWeights.FromJson(db.GetSetting(SoundsViewModel.LikedWeightsKey)));
    }

    [ObservableProperty] double _project;
    [ObservableProperty] double _drag;
    [ObservableProperty] double _send;
    [ObservableProperty] double _play;
    [ObservableProperty] double _favourite;
    /// <summary>How much anything older than a year counts, in percent.</summary>
    [ObservableProperty] double _olderPercent;

    public LikedWeights Weights => new(Project, Drag, Send, Play, Favourite, OlderPercent / 100);

    void Load(LikedWeights w)
    {
        _loading = true;
        (Project, Drag, Send, Play, Favourite, OlderPercent) = (w.Project, w.Drag, w.Send, w.Play, w.Favourite, w.OlderShare * 100);
        _loading = false;
    }

    protected override void OnPropertyChanged(System.ComponentModel.PropertyChangedEventArgs e)
    {
        base.OnPropertyChanged(e);
        if (_loading) return;
        var w = Weights;
        _db.SetSetting(SoundsViewModel.LikedWeightsKey, w.ToJson());
        Changed?.Invoke(w);
    }

    [RelayCommand]
    void Reset()
    {
        Load(LikedWeights.Default);
        _db.SetSetting(SoundsViewModel.LikedWeightsKey, LikedWeights.Default.ToJson());
        Changed?.Invoke(LikedWeights.Default);
    }
}
