using MusicOrganiser.ViewModels;

namespace MusicOrganiser.Avalonia.Audio;

/// <summary>Settings › Sound output's way into the player's outputs, which every player shares.</summary>
sealed class OpenAlOutputs : ISoundOutputs
{
    public IReadOnlyList<string> List() => OpenAlPlayer.OutputNames();

    public void Prefer(string? name) => OpenAlPlayer.ChooseOutput(name);

    public string Choose(string? name)
    {
        OpenAlPlayer.ChooseOutput(name);
        return OpenAlPlayer.CheckOutput();
    }

    public string Check() => OpenAlPlayer.CheckOutput();
}
