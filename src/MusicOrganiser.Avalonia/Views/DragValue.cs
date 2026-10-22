using System.Globalization;
using System.Windows.Input;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using MusicOrganiser.ViewModels;

namespace MusicOrganiser.Avalonia.Views;

/// <summary>
/// A value in a sunk box that changes like a DAW's: drag up or down (a step every 8 pixels), scroll over it, press the
/// arrow keys, or double-click and type the number you want. Each step runs <see cref="UpCommand"/> or
/// <see cref="DownCommand"/>, so it works with values that only move a step at a time (Solomon, 2026-10-22: the
/// inspector's folding sections, with no − and + buttons).
/// </summary>
public sealed class DragValue : Border
{
    public static readonly StyledProperty<string?> TextProperty = AvaloniaProperty.Register<DragValue, string?>(nameof(Text));
    /// <summary>The value as a whole number, to work out how many steps a typed number is away.</summary>
    public static readonly StyledProperty<int> ValueProperty = AvaloniaProperty.Register<DragValue, int>(nameof(Value));
    public static readonly StyledProperty<ICommand?> UpCommandProperty = AvaloniaProperty.Register<DragValue, ICommand?>(nameof(UpCommand));
    public static readonly StyledProperty<ICommand?> DownCommandProperty = AvaloniaProperty.Register<DragValue, ICommand?>(nameof(DownCommand));
    /// <summary>Takes several steps at once (a <see cref="ValueStep"/>), so a typed 11 or a long drag is one edit and one undo step.</summary>
    public static readonly StyledProperty<ICommand?> ByCommandProperty = AvaloniaProperty.Register<DragValue, ICommand?>(nameof(ByCommand));

    public string? Text { get => GetValue(TextProperty); set => SetValue(TextProperty, value); }
    public int Value { get => GetValue(ValueProperty); set => SetValue(ValueProperty, value); }
    public ICommand? UpCommand { get => GetValue(UpCommandProperty); set => SetValue(UpCommandProperty, value); }
    public ICommand? DownCommand { get => GetValue(DownCommandProperty); set => SetValue(DownCommandProperty, value); }
    public ICommand? ByCommand { get => GetValue(ByCommandProperty); set => SetValue(ByCommandProperty, value); }

    // Styled as a Border, so the sunk "lcd" box applies.
    protected override Type StyleKeyOverride => typeof(Border);

    const double Step = 8;
    readonly TextBlock _shown = new() { HorizontalAlignment = HorizontalAlignment.Center };
    readonly TextBox _typed = new() { IsVisible = false, MinHeight = 0, Padding = new Thickness(0), BorderThickness = new Thickness(0),
                                      HorizontalContentAlignment = HorizontalAlignment.Center, VerticalContentAlignment = VerticalAlignment.Center };
    double? _from;
    int _steps;
    static int _gestures;
    int _gesture;

    public DragValue()
    {
        Classes.Add("lcd");
        Focusable = true;
        Cursor = new Cursor(StandardCursorType.SizeNorthSouth);
        _shown.Bind(TextBlock.TextProperty, this.GetObservable(TextProperty));
        _typed.KeyDown += (_, e) =>
        {
            if (e.Key == Key.Enter) { Typed(); e.Handled = true; }
            else if (e.Key == Key.Escape) { StopTyping(); e.Handled = true; }
        };
        _typed.LostFocus += (_, _) => Typed();
        Child = new Panel { Children = { _shown, _typed } };
        ToolTip.SetTip(this, "Drag up or down, scroll, or double-click to type");
    }

    void Nudge(int by)
    {
        if (by == 0) return;
        if (ByCommand is { } many)
        {
            var step = new ValueStep(by, _gesture);
            if (many.CanExecute(step)) many.Execute(step);
            return;
        }
        for (var i = 0; i < Math.Abs(by); i++)
            if ((by > 0 ? UpCommand : DownCommand) is { } c && c.CanExecute(null)) c.Execute(null);
    }

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);
        if (!e.GetCurrentPoint(this).Properties.IsLeftButtonPressed) return;
        Focus();
        if (e.ClickCount == 2) { StartTyping(); e.Handled = true; return; }
        _from = e.GetPosition(this).Y;
        _steps = 0;
        _gesture = ++_gestures;
        e.Pointer.Capture(this);
        e.Handled = true;
    }

    protected override void OnPointerMoved(PointerEventArgs e)
    {
        base.OnPointerMoved(e);
        if (_from is not { } from) return;
        var want = (int)((from - e.GetPosition(this).Y) / Step);   // up is more
        if (want == _steps) return;
        Nudge(want - _steps);
        _steps = want;
    }

    protected override void OnPointerReleased(PointerReleasedEventArgs e)
    {
        base.OnPointerReleased(e);
        _from = null;
        e.Pointer.Capture(null);
    }

    protected override void OnPointerCaptureLost(PointerCaptureLostEventArgs e)
    {
        base.OnPointerCaptureLost(e);
        _from = null;
    }

    protected override void OnPointerWheelChanged(PointerWheelEventArgs e)
    {
        base.OnPointerWheelChanged(e);
        if (e.Delta.Y == 0) return;
        _gesture = ++_gestures;
        Nudge(e.Delta.Y > 0 ? 1 : -1);
        e.Handled = true;
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        if (_typed.IsVisible) return;
        if (e.Key is Key.Up or Key.Down) { _gesture = ++_gestures; Nudge(e.Key == Key.Up ? 1 : -1); e.Handled = true; }
        else if (e.Key == Key.Enter) { StartTyping(); e.Handled = true; }
    }

    void StartTyping()
    {
        _typed.Text = Value.ToString(CultureInfo.CurrentCulture);
        _typed.IsVisible = true;
        _shown.IsVisible = false;
        _typed.Focus();
        _typed.SelectAll();
    }

    void Typed()
    {
        if (!_typed.IsVisible) return;
        var text = _typed.Text?.Trim().TrimStart('+') ?? "";
        StopTyping();
        _gesture = ++_gestures;
        if (int.TryParse(text, NumberStyles.Integer, CultureInfo.CurrentCulture, out var want)) Nudge(want - Value);
    }

    void StopTyping()
    {
        _typed.IsVisible = false;
        _shown.IsVisible = true;
    }
}
