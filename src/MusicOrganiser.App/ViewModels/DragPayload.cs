namespace MusicOrganiser.App.ViewModels;

/// <summary>One thing shown in the drag preview: a track, a sound, a preset…</summary>
/// <param name="Glyph">A Segoe Fluent Icons character.</param>
public sealed record DragCard(string Title, string Detail, string Glyph);

/// <summary>
/// What a drag carries out of the app: the files (made when the drag starts) and how the
/// preview looks. <paramref name="Numbered"/> shows the cards as numbered tracks, in the
/// order they'll land.
/// </summary>
public sealed record DragPayload(IReadOnlyList<string> Files, IReadOnlyList<DragCard> Cards, string Caption, bool Numbered = false);

/// <summary>Something whose rows can be dragged out of the app as files.</summary>
public interface IFileDragSource
{
    /// <summary>The files for these rows (in display order), or null when there's nothing to drag.</summary>
    DragPayload? MakeDrag(IReadOnlyList<object> items);
}

public static class Glyphs
{
    public const string Midi = "\uE8D6", Audio = "\uE767", Preset = "\uE713", Project = "\uE8B7", Kit = "\uE8F1";
}
