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
    /// <summary>Who made the plugin: 0 any, 1 third-party, 2 FL Studio.</summary>
    [ObservableProperty] int _maker;
    /// <summary>What it is: 0 any, 1 synths, 2 effects.</summary>
    [ObservableProperty] int _type;
    [ObservableProperty] string _allMakersText = "All";
    [ObservableProperty] string _thirdPartyText = "Third-party";
    [ObservableProperty] string _nativeText = "FL Studio";
    [ObservableProperty] string _allTypesText = "All";
    [ObservableProperty] string _synthsText = "Synths";
    [ObservableProperty] string _effectsText = "Effects";
    [ObservableProperty] IReadOnlyList<PresetRow> _presets = [];
    [ObservableProperty] bool _hasPresets;
    [ObservableProperty] string _summary = "Reading plugin states…";
    [ObservableProperty] string _message = "";
    /// <summary>One line on where each kind of file in the list can be loaded.</summary>
    [ObservableProperty] string _formatNote = "";

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
            FormatNote = string.Join("  ·  ", presets.Select(p => p.Extension.ToLowerInvariant()).Distinct()
                .Select(ext => (ext, hint: LoadHint(ext))).Where(x => x.hint.Length > 0)
                .Select(x => $"{x.ext}: {x.hint}"));
            Apply();
        }
        catch (Exception e) { Summary = $"Couldn't read the plugin states: {e.Message}"; }
    }

    partial void OnMakerChanged(int value) => Apply();
    partial void OnTypeChanged(int value) => Apply();

    static PresetFilter Filter(int maker, int type) =>
        new(ThirdParty: maker != 2, Native: maker != 1, Synths: type != 2, Effects: type != 1);

    int Count(int maker, int type)
    {
        var f = Filter(maker, type);
        return _all.Count(r => f.Matches(r.Blob));
    }

    void Apply()
    {
        // Each choice shows how many presets it would give with the other choice as it is.
        AllMakersText = $"All ({Count(0, Type)})";
        ThirdPartyText = $"Third-party ({Count(1, Type)})";
        NativeText = $"FL Studio ({Count(2, Type)})";
        AllTypesText = $"All ({Count(Maker, 0)})";
        SynthsText = $"Synths ({Count(Maker, 1)})";
        EffectsText = $"Effects ({Count(Maker, 2)})";

        var filter = Filter(Maker, Type);
        Presets = _all.Where(r => filter.Matches(r.Blob)).ToList();
        HasPresets = Presets.Count > 0;
        Summary = _all.Count == 0
            ? "No plugin states saved in this project."
            : Presets.Count == 0
                ? "None of this project's plugin states match those choices."
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
        ".fxp" or ".fxb" => "load into the VST2 version of the plugin",
        ".vstpreset" => "load into the VST3 version of the plugin",
        ".flstate" => "only FL Studio can load these",
        ".vst3state" => "raw VST3 state, most DAWs can't load it directly",
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
        blob.Where,
        blob.Kind switch { PluginKind.Instrument => "Synth", PluginKind.Effect or PluginKind.NoteEffect => "Effect", _ => "" },
        blob.Native ? "FL Studio plugin" : "",
        blob.Extension.TrimStart('.'),
    }.Where(s => s.Length > 0));
    public string LoadHint => PresetsViewModel.LoadHint(blob.Extension);

    [RelayCommand]
    void Export() => owner.Export(this);
}
