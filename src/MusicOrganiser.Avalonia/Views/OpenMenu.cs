using System.Windows.Input;
using Avalonia.Controls;
using MusicOrganiser.ViewModels;

namespace MusicOrganiser.Avalonia.Views;

/// <summary>
/// The menu of ways to open a song: the Open button's drop-down, and the same (with the main action and file actions
/// added) on a right-click in the project list. Built when it opens, since it changes with the song.
/// </summary>
public static class OpenMenu
{
    /// <param name="row">The list's right-click menu, which also has the big button's own action and the file's.</param>
    public static void Fill(ItemCollection items, ProjectDetailViewModel d, bool row)
    {
        items.Clear();
        if (row) items.Add(Item(d.OpenMainText, d.OpenMainCommand, d.OpenMainTip));
        if (d.IsFlStudio) items.Add(Item("Open in FL Studio", d.OpenInDawCommand, "Open the newest save in FL Studio itself"));

        if (d.HasRunChoices)
        {
            if (items.Count > 0) items.Add(new Separator());
            items.Add(new MenuItem { Header = "VERSIONS", IsEnabled = false, Classes = { "menuHeading" } });
            foreach (var run in d.RunChoices)
            {
                var version = new MenuItem { Header = Text(run.Header) };
                ToolTip.SetTip(version, run.Run.Reworked || run.Run.First
                    ? $"Saved from {run.Run.Start:d MMM yyyy} to {run.Run.End:d MMM yyyy}"
                    : $"Saved from {run.Run.Start:d MMM yyyy} to {run.Run.End:d MMM yyyy}. Not much changed from the original: probably saved again to open it.");
                if (run.IsFlStudio) version.Items.Add(Item("Open in Bitwig", run.OpenInBitwigCommand, "Convert this save into a Bitwig project and open it"));
                version.Items.Add(Item(run.OpenInDawText, run.OpenInDawCommand, null));
                version.Items.Add(Item("Show in folder", run.RevealCommand, null));
                items.Add(version);
            }
        }

        if (row)
        {
            items.Add(new Separator());
            items.Add(Item("Show in folder", d.RevealCommand, null));
            items.Add(Item("Copy path", d.CopyPathCommand, null));
        }
    }

    static MenuItem Item(string header, ICommand command, string? tip)
    {
        var item = new MenuItem { Header = Text(header), Command = command };
        if (tip is not null) ToolTip.SetTip(item, tip);
        return item;
    }

    // A menu takes "_" to mark the key that picks an item, so file names like Didums_77 need it doubled.
    static string Text(string header) => header.Replace("_", "__");
}
