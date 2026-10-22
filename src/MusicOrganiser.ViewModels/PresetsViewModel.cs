using System.IO;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using MusicOrganiser.Core;
using MusicOrganiser.Core.Export;
using MusicOrganiser.Core.Parsers;

namespace MusicOrganiser.ViewModels;

/// <summary>The presets popup: every plugin state in the project, filtered, ready to export.</summary>
public sealed partial class PresetsViewModel : ObservableObject, IFileDragSource
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
            var (result, templates) = await Task.Run(() =>
            {
                var t = BitwigTemplates();
                return (Exporter.Presets(blobs, _path, _exportRoot, t), t);
            });
            Message = ExportSummary(result, blobs, templates);
            Shell.Open(result.Folder);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { Message = $"Export failed: {e.Message}"; }
    }

    public void Export(PresetRow row)
    {
        try
        {
            var templates = BitwigTemplates();
            var result = Exporter.Presets([row.Blob], _path, _exportRoot, templates);
            Message = ExportSummary(result, [row.Blob], templates);
            Shell.Reveal(Path.Combine(result.Folder, result.Files[0]));
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { Message = $"Export failed: {e.Message}"; }
    }

    /// <summary>
    /// Dragged presets, exported as they leave: the Bitwig preset where there is one (drop it on a
    /// track and the plugin loads with its settings), else the plugin's own preset file.
    /// </summary>
    public DragPayload? MakeDrag(IReadOnlyList<object> items)
    {
        var rows = items.OfType<PresetRow>().ToList();
        if (rows.Count == 0) return null;
        var templates = BitwigTemplates();
        var result = Exporter.Presets(rows.Select(r => r.Blob).ToList(), _path, _exportRoot, templates);
        var files = result.Files.Select(f => Path.Combine(result.Folder, f)).ToList();
        var bitwig = files.Where(f => f.EndsWith(".bwpreset", StringComparison.OrdinalIgnoreCase))
                          .Select(Path.GetFileNameWithoutExtension).ToHashSet(StringComparer.OrdinalIgnoreCase);
        // Each preset once: its .bwpreset when it got one, otherwise the plugin preset.
        var chosen = files.Where(f => f.EndsWith(".bwpreset", StringComparison.OrdinalIgnoreCase) ||
                                      !bitwig.Contains(Path.GetFileNameWithoutExtension(f))).ToList();
        var cards = rows.Select(r => new DragCard(r.Name, r.Plugin.Length > 0 ? r.Plugin : r.Details, Glyphs.Preset)).ToList();
        return new DragPayload(chosen, cards, rows.Count == 1 ? "1 preset" : $"{rows.Count} presets");
    }

    /// <summary>
    /// Presets you saved in Bitwig (in Documents\Bitwig Studio), used as templates for .bwpreset
    /// files. Read fresh for each export, so a preset saved a minute ago counts.
    /// </summary>
    public static IReadOnlyDictionary<string, BitwigPreset.Template> BitwigTemplates()
    {
        try { return BitwigPreset.FindTemplates(BitwigPreset.DefaultFolders()); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return new Dictionary<string, BitwigPreset.Template>(); }
    }

    /// <summary>What an export wrote, and which plugins could also get Bitwig presets after one save in Bitwig.</summary>
    public static string ExportSummary(ExportResult result, IReadOnlyCollection<PresetBlob> blobs, IReadOnlyDictionary<string, BitwigPreset.Template> templates)
    {
        var bitwig = result.Files.Count(f => f.EndsWith(".bwpreset", StringComparison.OrdinalIgnoreCase));
        var plain = result.Files.Count - bitwig;
        var text = plain == 1 && bitwig == 0 ? $"Exported {result.Files[0]}." : $"Exported {plain} preset{(plain == 1 ? "" : "s")}";
        if (bitwig > 0) text += $", {(bitwig == plain ? "all" : bitwig.ToString())} also as Bitwig presets (.bwpreset) that load in Bitwig with their settings.";
        else if (!text.EndsWith('.')) text += ".";
        var without = blobs.Where(b => BitwigPreset.StateFormat(b.Extension).Length > 0 && BitwigPreset.For(b, templates) is null)
                           .Select(b => b.Plugin).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        if (without.Count > 0)
            text += $" For a Bitwig preset of {string.Join(", ", without.Take(3))}{(without.Count > 3 ? $" and {without.Count - 3} more" : "")}, " +
                    "save any preset of that plugin once in Bitwig, then export again.";
        return text;
    }

    /// <summary>Where each kind of file can be loaded.</summary>
    public static string LoadHint(string extension) => extension.ToLowerInvariant() switch
    {
        ".bwpreset" => "open it from Bitwig's browser or drag it onto a track",
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
