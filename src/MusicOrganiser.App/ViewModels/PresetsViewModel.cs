using System.IO;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using MusicOrganiser.Core;
using MusicOrganiser.Core.Export;
using MusicOrganiser.Core.Parsers;

namespace MusicOrganiser.App.ViewModels;

/// <summary>The presets popup: every plugin state in the project, filtered, ready to export.</summary>
public sealed partial class PresetsViewModel : ObservableObject
{
    readonly IProjectReader _reader;
    readonly string _path;
    readonly string _exportRoot;
    IReadOnlyList<PresetRow> _all = [];

    public string Title { get; }
    /// <summary>Only FL Studio projects keep the DAW's own plugin states alongside third-party ones.</summary>
    [ObservableProperty] bool _hasNative;
    /// <summary>Only some projects say which plugins are synths and which are effects.</summary>
    [ObservableProperty] bool _hasKinds;
    [ObservableProperty] bool _thirdParty = true;
    [ObservableProperty] bool _native = true;
    [ObservableProperty] bool _synths = true;
    [ObservableProperty] bool _effects = true;
    [ObservableProperty] IReadOnlyList<PresetRow> _presets = [];
    [ObservableProperty] bool _hasPresets;
    [ObservableProperty] string _summary = "Reading plugin states…";
    [ObservableProperty] string _message = "";

    public PresetsViewModel(IProjectReader reader, string path, string title, string exportRoot)
    {
        _reader = reader;
        _path = path;
        _exportRoot = exportRoot;
        Title = title;
    }

    public async Task LoadAsync()
    {
        try
        {
            var presets = await Task.Run(() => _reader.ExtractPresets(_path));
            _all = presets.Select(p => new PresetRow(p, this)).ToList();
            HasNative = presets.Any(p => p.Native);
            HasKinds = presets.Any(p => p.Kind != PluginKind.Unknown);
            Apply();
        }
        catch (Exception e) { Summary = $"Couldn't read the plugin states: {e.Message}"; }
    }

    partial void OnThirdPartyChanged(bool value) => Apply();
    partial void OnNativeChanged(bool value) => Apply();
    partial void OnSynthsChanged(bool value) => Apply();
    partial void OnEffectsChanged(bool value) => Apply();

    void Apply()
    {
        var filter = new PresetFilter(ThirdParty, Native, Synths, Effects);
        Presets = _all.Where(r => filter.Matches(r.Blob)).ToList();
        HasPresets = Presets.Count > 0;
        Summary = _all.Count == 0
            ? "No plugin states saved in this project."
            : Presets.Count == _all.Count
                ? $"{_all.Count} plugin state{(_all.Count == 1 ? "" : "s")}"
                : $"Showing {Presets.Count} of {_all.Count} plugin states";
    }

    [RelayCommand]
    async Task ExportShown()
    {
        Message = "Exporting…";
        try
        {
            var blobs = Presets.Select(r => r.Blob).ToList();
            var result = await Task.Run(() => Exporter.Presets(blobs, _path, _exportRoot));
            Message = $"Exported {result.Files.Count} preset{(result.Files.Count == 1 ? "" : "s")}.";
            Shell.Open(result.Folder);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { Message = $"Export failed: {e.Message}"; }
    }

    public void Export(PresetRow row)
    {
        try
        {
            var result = Exporter.Presets([row.Blob], _path, _exportRoot);
            Message = $"Exported {result.Files[0]}";
            Shell.Reveal(Path.Combine(result.Folder, result.Files[0]));
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { Message = $"Export failed: {e.Message}"; }
    }

    /// <summary>Where each kind of file can be loaded.</summary>
    public static string LoadHint(string extension) => extension.ToLowerInvariant() switch
    {
        ".fxp" or ".fxb" => "Load into the VST2 version of the plugin",
        ".vstpreset" => "Load into the VST3 version of the plugin",
        ".flstate" => "FL Studio's own format: only FL Studio can load it",
        ".vst3state" => "Raw VST3 state: most DAWs can't load it directly",
        _ => "",
    };
}

public sealed partial class PresetRow(PresetBlob blob, PresetsViewModel owner) : ObservableObject
{
    public PresetBlob Blob => blob;
    public string Name => blob.Name;
    public string Plugin => blob.Plugin.Length > 0 && blob.Plugin != blob.Name ? blob.Plugin : "";
    public string Details => string.Join(" · ", new[]
    {
        blob.Kind switch { PluginKind.Instrument => "Synth", PluginKind.Effect or PluginKind.NoteEffect => "Effect", _ => "" },
        blob.Native ? "FL Studio plugin" : "",
        blob.Extension.TrimStart('.'),
    }.Where(s => s.Length > 0));
    public string LoadHint => PresetsViewModel.LoadHint(blob.Extension);

    [RelayCommand]
    void Export() => owner.Export(this);
}
