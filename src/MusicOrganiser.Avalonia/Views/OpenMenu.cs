using System.Windows.Input;
using Avalonia.Controls;
using MusicOrganiser.ViewModels;

namespace MusicOrganiser.Avalonia.Views;

/// <summary>
/// The menu of ways to open a song: the song panel's "Older saves" drop-down, and the same (with the Open buttons'
/// actions and the file's added) on a right-click in the project list. Built when it opens, since it changes with the song.
/// </summary>
public static class OpenMenu
{
    /// <param name="row">The list's right-click menu, which also has the Open buttons' own actions and the file's;
    /// otherwise just the versions, for the song panel's "Older saves" button.</param>
    public static void Fill(ItemCollection items, ProjectDetailViewModel d, bool row)
    {
        items.Clear();
        // (FL Studio and Bitwig songs open in Bitwig with this line, so it has Open in Bitwig's job too.)
        if (row) items.Add(Item(d.OpenMainText, d.OpenMainCommand, d.OpenMainTip, key: "projects.open"));
        if (row && d.IsFlStudio) items.Add(Item("Open in FL Studio", d.OpenInDawCommand, "Open the newest save in FL Studio itself"));

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
            if (d.CanFix) items.Add(Item("Fix…", d.PlanFixCommand, d.FixTip, key: "projects.fix"));
            if (d.CanExtractMidi) items.Add(Item("MIDI…", d.ShowMidiCommand, d.MidiNote, key: "projects.midi"));
            items.Add(Item("Show in folder", d.RevealCommand, null, key: "projects.reveal"));
            items.Add(Item("Copy path", d.CopyPathCommand, null, key: "projects.copyPath"));
        }
    }

    /// <summary>
    /// The "Older saves" button's menu: the song's versions when it has some (saves months apart), otherwise its newest
    /// older saves, then a link to every save on the Saves tab.
    /// </summary>
    public static void FillOlderSaves(ItemCollection items, ProjectDetailViewModel d, Action seeAll)
    {
        items.Clear();
        if (d.HasRunChoices) Fill(items, d, row: false);
        else
        {
            items.Add(new MenuItem { Header = "RECENT SAVES", IsEnabled = false, Classes = { "menuHeading" } });
            foreach (var save in d.OlderSaves.OrderByDescending(s => s.Modified).Take(OlderSavesShown))
            {
                var item = new MenuItem { Header = Text($"{save.Name} · {save.Modified:d MMM yyyy}") };
                if (save.Daw == "FL Studio") item.Items.Add(Item("Open in Bitwig", d.OpenSaveInBitwigCommand, "Convert this save into a Bitwig project and open it", save));
                item.Items.Add(Item($"Open in {save.Daw}", d.OpenSaveCommand, null, save));
                item.Items.Add(Item("Show in folder", d.RevealSaveCommand, null, save));
                items.Add(item);
            }
        }
        items.Add(new Separator());
        var all = new MenuItem { Header = $"See all {d.Item.SaveCount} saves" };
        all.Click += (_, _) => seeAll();
        items.Add(all);
    }

    const int OlderSavesShown = 8;

    static MenuItem Item(string header, ICommand command, string? tip, object parameter)
    {
        var item = Item(header, command, tip);
        item.CommandParameter = parameter;
        return item;
    }

    /// <param name="key">The action in Settings › Shortcuts the line does, whose key it shows (see <see cref="ShortcutKeys.ShowKeys"/>).</param>
    static MenuItem Item(string header, ICommand command, string? tip, string? key = null)
    {
        var item = new MenuItem { Header = Text(header), Command = command, Tag = key };
        if (tip is not null) ToolTip.SetTip(item, tip);
        return item;
    }

    // A menu takes "_" to mark the key that picks an item, so file names like Didums_77 need it doubled.
    static string Text(string header) => header.Replace("_", "__");
}
