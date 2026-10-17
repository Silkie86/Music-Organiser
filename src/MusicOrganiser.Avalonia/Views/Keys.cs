namespace MusicOrganiser.Avalonia.Views;

/// <summary>Key names for hints, as this computer has them: a Mac picks several with ⌘, not Ctrl.</summary>
public static class Keys
{
    public static string Command { get; } = OperatingSystem.IsMacOS() ? "⌘" : "Ctrl";

    public static string PickSeveralChannels { get; } = $"Or drag any channel. {Command} or Shift picks several.";
}
