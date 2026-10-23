using MusicOrganiser.Core.Midi;
using MusicOrganiser.Core.Midi.Jazz;
using MusicOrganiser.Core.Midi.Playground;

namespace MusicOrganiser.ViewModels;

/// <summary>One line of a menu: what it says and the action it runs (<see cref="PlaygroundViewModel.RunShortcut"/>), or a
/// separator when <see cref="Id"/> is "".</summary>
public sealed record PlaygroundMenuItem(string Header, string Id)
{
    public static readonly PlaygroundMenuItem Separator = new("", "");
    public bool IsSeparator => Id.Length == 0;
}

public sealed record PlaygroundMenu(string Header, IReadOnlyList<PlaygroundMenuItem> Items);

/// <summary>
/// The Playground's menus, like a DAW's (Solomon, 2026-10-21: "no right click menus or file edit view menus… it should
/// look like a modern daw"). Every line runs an action from the shortcut list, so its key shows beside it and can be
/// changed in Settings › Shortcuts; the right-click menus are the same lines picked for what was clicked.
/// </summary>
public sealed partial class PlaygroundViewModel
{
    static PlaygroundMenuItem M(string header, string id) => new(header, id);
    static readonly PlaygroundMenuItem Sep = PlaygroundMenuItem.Separator;
    /// <summary>The last line of each right-click menu: the keys sheet, where any line's key can be set (Solomon, 2026-10-21:
    /// "they don't all have to have one but they should all have the possibility to").</summary>
    static readonly PlaygroundMenuItem Keys = M("Change keys…", "playground.help");

    public static IReadOnlyList<PlaygroundMenu> MenuBar { get; } =
    [
        new("File", [M("New idea", "playground.new"), M("Open an idea…", "playground.ideas"), M("Save", "playground.save"), Sep,
                     M("Send to Bitwig", "playground.sendToBitwig"), Sep,
                     M("Follow Bitwig", "playground.followBitwig"), M("Play in Bitwig", "playground.playInBitwig")]),
        new("Edit", [M("Undo", "playground.undo"), M("Redo", "playground.redo"), Sep,
                     M("Cut", "playground.cut"), M("Copy", "playground.copy"), M("Paste", "playground.paste"),
                     M("Duplicate", "playground.duplicate"), M("Delete", "playground.delete"), Sep,
                     M("Split", "playground.split"), M("Rename", "playground.rename")]),
        new("Create", [M("Label over the highlight", "playground.addLabel"), Sep,
                       M("Write chords", "playground.writeChords"), M("Write a bassline", "playground.writeBass"),
                       M("Write guide tones", "playground.writeGuideTones")]),
        new("Clip", [M("Up a semitone", "playground.up"), M("Down a semitone", "playground.down"),
                     M("Up an octave", "playground.octaveUp"), M("Down an octave", "playground.octaveDown"), Sep,
                     M("Follow the chords again", "playground.fitAgain"), M("Smooth inversions", "playground.smooth"), Sep,
                     M("Follow: nudge clashes", "playground.followNudge"), M("Follow: keep its place in the chord", "playground.followDegree"),
                     M("Follow: move with the root", "playground.followRoot"), M("Don't follow the chords", "playground.followNone"), Sep,
                     M("Find alternatives", "playground.alternatives")]),
        new("Track", [M("Mute", "playground.mute"), M("Solo", "playground.solo"), Sep,
                      M("Duplicate track", "playground.duplicateTrack"), M("Delete track", "playground.deleteTrack")]),
        new("Harmony", [M("Jazz and gospel", "playground.jazz"), M("Classical", "playground.classical"), Sep,
                        M("Reharmonise…", "playground.reharm"), M("Harmonise the melody…", "playground.harmonise"), Sep,
                        M("Chord progressions…", "playground.tabChords"),
                        M("Get chords from Bitwig", "playground.chordsFromBitwig"),
                        M("Put the chords heard on the chord track", "playground.keepHeardChords"), M("Clear the chord track", "playground.clearChordTrack"), Sep,
                        M("Hear the chord track", "playground.hearChordTrack")]),
        new("View", [M("Zoom in", "playground.zoomIn"), M("Zoom out", "playground.zoomOut"), M("Zoom to fit", "playground.zoomFit"), Sep,
                     M("Loop", "playground.loop"), M("Piano roll", "playground.foldRoll"), Sep,
                     M("Your MIDI", "playground.tabMidi"), M("Chords", "playground.tabChords"), M("Write", "playground.tabWrite"),
                     M("Reharm", "playground.reharm"), M("Song", "playground.tabSong")]),
        new("Help", [M("Keyboard shortcuts: see and change them…", "playground.help")]),
    ];

    /// <summary>Right-click on a clip.</summary>
    public static IReadOnlyList<PlaygroundMenuItem> ClipMenu { get; } =
    [
        M("Cut", "playground.cut"), M("Copy", "playground.copy"), M("Paste", "playground.paste"), M("Duplicate", "playground.duplicate"),
        M("Delete", "playground.delete"), Sep, M("Split", "playground.split"), M("Rename", "playground.rename"), Sep,
        M("Find alternatives", "playground.alternatives"), M("Follow the chords again", "playground.fitAgain"),
        M("Smooth inversions", "playground.smooth"), Sep,
        M("Follow: nudge clashes", "playground.followNudge"), M("Follow: keep its place in the chord", "playground.followDegree"),
        M("Follow: move with the root", "playground.followRoot"), M("Don't follow the chords", "playground.followNone"), Sep,
        M("Up an octave", "playground.octaveUp"), M("Down an octave", "playground.octaveDown"), Sep, Keys,
    ];

    /// <summary>Right-click on a track's name.</summary>
    public static IReadOnlyList<PlaygroundMenuItem> TrackMenu { get; } =
    [
        M("Mute", "playground.mute"), M("Solo", "playground.solo"), M("Rename", "playground.rename"), Sep,
        M("Paste", "playground.paste"), M("Duplicate track", "playground.duplicateTrack"), M("Delete track", "playground.deleteTrack"), Sep, Keys,
    ];

    /// <summary>Right-click on empty timeline or the bar numbers.</summary>
    public static IReadOnlyList<PlaygroundMenuItem> TimelineMenu { get; } =
    [
        M("Paste", "playground.paste"), Sep, M("Label over the highlight", "playground.addLabel"), M("Loop", "playground.loop"),
        M("Zoom to fit", "playground.zoomFit"), Sep, M("Write chords", "playground.writeChords"), M("Write a bassline", "playground.writeBass"), Sep, Keys,
    ];

    /// <summary>Right-click on the chord lane.</summary>
    public static IReadOnlyList<PlaygroundMenuItem> ChordMenu { get; } =
    [
        M("Reharmonise…", "playground.reharm"), M("Remove this chord", "playground.removeChord"), Sep,
        M("Chord progressions…", "playground.tabChords"), M("Get chords from Bitwig", "playground.chordsFromBitwig"),
        M("Put the chords heard on the chord track", "playground.keepHeardChords"), M("Clear the chord track", "playground.clearChordTrack"), M("Hear the chord track", "playground.hearChordTrack"), Sep,
        M("Jazz and gospel", "playground.jazz"), M("Classical", "playground.classical"), Sep, Keys,
    ];

    public const int SuggestTabIndex = 0, ChordsTab = 1, WriteTab = 2, ReharmTab = 3, SongTab = 4;

    /// <summary>Whether <paramref name="id"/> can be done now: menu lines that can't are greyed.</summary>
    public bool CanRun(string id)
    {
        var clip = SelectedClip;
        var track = clip?.Track ?? SelectedLayer;
        return id switch
        {
            "playground.undo" => CanUndo,
            "playground.redo" => CanRedo,
            "playground.paste" => _clipboard is not null,
            "playground.removeChord" => PickedChord is { IsSet: true },
            "playground.clearChordTrack" => HasChordTrack,
            "playground.keepHeardChords" => Harmony.Lane.Count > 0,
            "playground.chordsFromBitwig" => ChordsFromBitwigCommand.CanExecute(null),
            "playground.harmonise" => CanHarmonise,
            "playground.sendToBitwig" => SendToBitwigCommand.CanExecute(null),
            "playground.fitAgain" => clip is { CanFitAgain: true },
            "playground.smooth" => track is not null,
            "playground.mute" or "playground.solo" or "playground.duplicateTrack" or "playground.deleteTrack" => track is not null,
            "playground.rename" => clip is not null || track is not null,
            "playground.cut" or "playground.copy" or "playground.duplicate" or "playground.delete" or "playground.split" or
            "playground.up" or "playground.down" or "playground.octaveUp" or "playground.octaveDown" or "playground.alternatives" => clip is not null,
            "playground.followNudge" or "playground.followDegree" or "playground.followRoot" or "playground.followNone" => clip is { IsDrums: false },
            _ => true,
        };
    }

    /// <summary>Whether a menu line is ticked: the harmony style, loop, mute and solo.</summary>
    public bool IsTicked(string id) => id switch
    {
        "playground.jazz" => !IsClassical,
        "playground.classical" => IsClassical,
        "playground.loop" => LoopOn,
        "playground.followBitwig" => FollowBitwig,
        "playground.playInBitwig" => PlayInBitwig,
        "playground.hearChordTrack" => HearChordTrack,
        "playground.foldRoll" => !PianoFolded,
        "playground.mute" => (SelectedClip?.Track ?? SelectedLayer)?.Layer.Muted == true,
        "playground.solo" => (SelectedClip?.Track ?? SelectedLayer)?.Layer.Solo == true,
        _ when FollowOf(id) is { } way => SelectedClip?.Clip.Way == way,
        _ => false,
    };

    /// <summary>The way of following the chords a menu line picks, or null when it's another line.</summary>
    static FollowWay? FollowOf(string id) => id switch
    {
        "playground.followNudge" => FollowWay.Nudge,
        "playground.followDegree" => FollowWay.Degree,
        "playground.followRoot" => FollowWay.Root,
        "playground.followNone" => FollowWay.None,
        _ => null,
    };

    /// <summary>Whether a menu line has a tick box at all.</summary>
    public static bool IsToggle(string id) =>
        id is "playground.jazz" or "playground.classical" or "playground.loop" or "playground.hearChordTrack" or "playground.foldRoll" or "playground.mute" or "playground.solo" or
            "playground.followBitwig" or "playground.playInBitwig" ||
        FollowOf(id) is not null;

    // ---- copy and paste ----

    PlaygroundClip? _clipboard;
    string _clipboardTrack = "";

    void CopyClip(ClipRow row, bool cut)
    {
        _clipboard = row.Clip.Copy();
        _clipboardTrack = row.Track.Layer.Name;
        if (cut) RemoveClip(row);
        Message = $"{(cut ? "Cut" : "Copied")} {row.Clip.Name}. Paste puts it at the play marker.";
    }

    /// <summary>
    /// The copied clip at the play marker, on the picked track (or the track it came from, or a new one), over whatever
    /// was there, fitted to the chords where it lands.
    /// </summary>
    internal void PasteClip()
    {
        if (_clipboard is null) return;
        var track = SelectedClip?.Track ?? SelectedLayer ?? Layers.FirstOrDefault(l => l.Layer.Name == _clipboardTrack);
        var copy = _clipboard.Copy();
        copy.Start = Cursor / Grid * Grid;
        Remember();
        if (track is null)
        {
            var layer = AddLayer(_clipboardTrack.Length > 0 ? _clipboardTrack : copy.Name, copy.Role, copy.Notes, copy.SourcePath, copy.SourceHash,
                                 start: copy.Start, length: copy.Length, loop: copy.Loop);
            layer.Clips[0] = copy;   // with its moves, inversion and pin, not rebuilt from its notes
            if (!copy.Pinned) Refit(layer, copy);   // on the chords where it's pasted, not where it was copied
            layer.Render();
            var row = Layers.First(l => l.Layer == layer);
            row.Changed();
            SelectedClip = new ClipRow(this, row, copy);
            Changed();
            return;
        }
        CutOut(track.Layer, copy.Start, copy.End);
        track.Layer.Clips.Add(copy);
        Reach(copy.End);
        if (!copy.Pinned) Refit(track.Layer, copy);
        track.Layer.Render();
        track.Changed();
        SelectedClip = new ClipRow(this, track, copy);
        Changed();
    }

    /// <summary>The picked clip's bars highlighted, so Suggest lists parts of its kind to swap in for it.</summary>
    void FindAlternatives(ClipRow row)
    {
        RightTab = SuggestTabIndex;
        SetLoop(new RollRange(row.Clip.Start, row.Clip.End));
        Message = $"Parts that could replace {row.Clip.Name}: Swap in puts one there instead.";
    }

    /// <summary>The view asks for a name box to be focused (Rename); the view does that, as it owns the boxes.</summary>
    public event EventHandler? RenameAsked;

    /// <summary>The view opens the Ideas list (File › Open an idea).</summary>
    public event EventHandler? IdeasAsked;

    bool RunMenuAction(string id, ClipRow? clip)
    {
        var track = clip?.Track ?? SelectedLayer;
        switch (id)
        {
            case "playground.new": NewIdeaCommand.Execute(null); return true;
            case "playground.ideas": IdeasAsked?.Invoke(this, EventArgs.Empty); return true;
            case "playground.sendToBitwig": if (SendToBitwigCommand.CanExecute(null)) SendToBitwigCommand.Execute(null); return true;
            case "playground.paste": PasteClip(); return true;
            case "playground.addLabel": AddLabelCommand.Execute(null); return true;
            case "playground.writeChords": RightTab = WriteTab; WriteChordsCommand.Execute(null); return true;
            case "playground.writeBass": RightTab = WriteTab; WriteBassCommand.Execute(null); return true;
            case "playground.writeGuideTones": RightTab = WriteTab; WriteGuideTonesCommand.Execute(null); return true;
            case "playground.jazz": IsClassical = false; return true;
            case "playground.classical": IsClassical = true; return true;
            case "playground.reharm": OpenReharm(); return true;
            case "playground.harmonise": if (CanHarmonise) OpenReharm(); return true;
            case "playground.tabMidi": RightTab = SuggestTabIndex; return true;
            case "playground.tabChords": RightTab = ChordsTab; return true;
            case "playground.removeChord": RemoveChord(); return true;
            case "playground.clearChordTrack": ClearChordTrack(); return true;
            case "playground.keepHeardChords": KeepHeardChords(); return true;
            case "playground.chordsFromBitwig": _ = ChordsFromBitwigCommand.ExecuteAsync(null); return true;
            case "playground.hearChordTrack": HearChordTrack = !HearChordTrack; return true;
            case "playground.followBitwig": FollowBitwig = !FollowBitwig; return true;
            case "playground.playInBitwig": PlayInBitwig = !PlayInBitwig; return true;
            case "playground.tabWrite": RightTab = WriteTab; return true;
            case "playground.tabSong": RightTab = SongTab; return true;
            case "playground.foldRoll": PianoFolded = !PianoFolded; return true;
            case "playground.rename": if (clip is null && track is null) return false; RenameAsked?.Invoke(this, EventArgs.Empty); return true;
        }
        if (track is not null)
        {
            switch (id)
            {
                case "playground.mute": ToggleMute(track); return true;
                case "playground.solo": ToggleSolo(track); return true;
                case "playground.duplicateTrack": DuplicateLayer(track); return true;
                case "playground.deleteTrack": RemoveLayer(track); return true;
                case "playground.smooth": SelectedLayer = track; SmoothInversionsCommand.Execute(null); return true;
            }
        }
        if (clip is null) return false;
        switch (id)
        {
            case "playground.copy": CopyClip(clip, cut: false); return true;
            case "playground.cut": CopyClip(clip, cut: true); return true;
            case "playground.alternatives": FindAlternatives(clip); return true;
        }
        if (FollowOf(id) is { } follow && !clip.IsDrums) { SetFollow(clip, follow); return true; }
        return false;
    }
}
