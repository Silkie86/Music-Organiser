using System.Windows;
using System.Windows.Media;
using MusicOrganiser.Core.Parsers;

namespace MusicOrganiser.App.Views;

/// <summary>A tiny piano roll of one channel's notes, on a time scale shared by the whole song.</summary>
public sealed class NoteRoll : FrameworkElement
{
    public static readonly DependencyProperty ChannelProperty = DependencyProperty.Register(
        nameof(Channel), typeof(MidiChannel), typeof(NoteRoll), new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty SongEndProperty = DependencyProperty.Register(
        nameof(SongEnd), typeof(long), typeof(NoteRoll), new FrameworkPropertyMetadata(0L, FrameworkPropertyMetadataOptions.AffectsRender));

    public MidiChannel? Channel { get => (MidiChannel?)GetValue(ChannelProperty); set => SetValue(ChannelProperty, value); }
    public long SongEnd { get => (long)GetValue(SongEndProperty); set => SetValue(SongEndProperty, value); }

    protected override void OnRender(DrawingContext dc)
    {
        var w = ActualWidth;
        var h = ActualHeight;
        var back = (TryFindResource("ControlFillColorDefaultBrush") as Brush) ?? Brushes.Transparent;
        dc.DrawRoundedRectangle(back, null, new Rect(0, 0, w, h), 4, 4);
        if (Channel is not { Notes.Count: > 0 } c || w < 4 || h < 4) return;

        var fill = (TryFindResource("SystemAccentColorPrimaryBrush") as Brush) ?? Brushes.DodgerBlue;
        var end = Math.Max(1, SongEnd > 0 ? SongEnd : c.End);
        var lo = c.Lowest;
        var rows = Math.Max(1, c.Highest - lo + 1);
        var rowH = Math.Max(1.5, (h - 4) / rows);
        foreach (var n in c.Notes)
        {
            var x = 2 + (w - 4) * n.Start / end;
            var nw = Math.Max(1.0, (w - 4) * n.Length / end);
            var y = 2 + (h - 4 - rowH) * (rows == 1 ? 0.5 : 1 - (double)(n.Key - lo) / (rows - 1));
            dc.DrawRectangle(fill, null, new Rect(x, y, nw, rowH));
        }
    }
}
