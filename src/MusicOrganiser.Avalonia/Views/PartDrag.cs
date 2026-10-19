using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using MusicOrganiser.ViewModels;

namespace MusicOrganiser.Avalonia.Views;

/// <summary>
/// Lets a suggestion in the Playground's side panel be dragged onto the timeline (Solomon, 2026-10-19: "all draggable
/// onto tracks"): set <c>views:PartDrag.Part="{Binding}"</c> on the bit of the card to grab.
/// </summary>
public static class PartDrag
{
    /// <summary>How a suggestion travels inside the app while it's dragged.</summary>
    public static readonly DataFormat<PlaygroundMatchRow> Format = DataFormat.CreateInProcessFormat<PlaygroundMatchRow>("MusicOrganiser.PlaygroundPart");

    public static readonly AttachedProperty<PlaygroundMatchRow?> PartProperty =
        AvaloniaProperty.RegisterAttached<Control, PlaygroundMatchRow?>("Part", typeof(PartDrag));

    public static PlaygroundMatchRow? GetPart(Control c) => c.GetValue(PartProperty);
    public static void SetPart(Control c, PlaygroundMatchRow? value) => c.SetValue(PartProperty, value);

    static readonly AttachedProperty<PointerPressedEventArgs?> PressProperty =
        AvaloniaProperty.RegisterAttached<Control, PointerPressedEventArgs?>("Press", typeof(PartDrag));

    static PartDrag()
    {
        PartProperty.Changed.AddClassHandler<Control>((c, e) =>
        {
            c.PointerPressed -= Pressed;
            c.PointerMoved -= Moved;
            c.PointerReleased -= Released;
            if (e.NewValue is null) return;
            c.PointerPressed += Pressed;
            c.PointerMoved += Moved;
            c.PointerReleased += Released;
            c.Cursor = new Cursor(StandardCursorType.Hand);
        });
    }

    static void Pressed(object? sender, PointerPressedEventArgs e)
    {
        if (sender is Control c && e.GetCurrentPoint(c).Properties.IsLeftButtonPressed) c.SetValue(PressProperty, e);
    }

    static void Released(object? sender, PointerReleasedEventArgs e) => (sender as Control)?.SetValue(PressProperty, null);

    static async void Moved(object? sender, PointerEventArgs e)
    {
        if (sender is not Control c || c.GetValue(PressProperty) is not { } press || GetPart(c) is not { } part) return;
        var d = e.GetPosition(c) - press.GetPosition(c);
        if (Math.Abs(d.X) < 4 && Math.Abs(d.Y) < 4) return;
        c.SetValue(PressProperty, null);
        var data = new DataTransfer();
        data.Add(DataTransferItem.Create(Format, part));
        await DragDrop.DoDragDropAsync(press, data, DragDropEffects.Copy);
    }
}
