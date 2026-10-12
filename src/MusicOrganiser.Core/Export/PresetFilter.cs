using MusicOrganiser.Core.Parsers;

namespace MusicOrganiser.Core.Export;

/// <summary>
/// Which presets to export: third-party plugins and/or the DAW's own, synths and/or effects.
/// Presets whose kind isn't known (Bitwig, Studio One) pass the synth/effect choice.
/// </summary>
public sealed record PresetFilter(bool ThirdParty = true, bool Native = true, bool Synths = true, bool Effects = true)
{
    public bool Matches(PresetBlob p) =>
        (p.Native ? Native : ThirdParty) &&
        p.Kind switch
        {
            PluginKind.Instrument => Synths,
            PluginKind.Effect or PluginKind.NoteEffect => Effects,
            _ => true,
        };
}
