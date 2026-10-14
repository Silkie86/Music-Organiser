using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Avalonia;
using Avalonia.Animation;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using MusicOrganiser.ViewModels;

namespace MusicOrganiser.Avalonia.Views;

/// <summary>
/// The card that follows the cursor during a drag: what's being carried, how many, and for
/// MIDI the tracks in the order they'll land. It floats above every app (so it shows over the
/// DAW too) and lets the mouse pass straight through to whatever is underneath. Windows only:
/// it needs the cursor's position outside the app, which Avalonia doesn't give.
/// </summary>
[SupportedOSPlatform("windows")]
sealed partial class DragPreview : Window
{
    const int MaxCards = 5;
    readonly DispatcherTimer _follow;

    public DragPreview(DragPayload payload)
    {
        WindowDecorations = WindowDecorations.None;
        TransparencyLevelHint = [WindowTransparencyLevel.Transparent];
        Background = Brushes.Transparent;
        ShowInTaskbar = false;
        ShowActivated = false;
        Topmost = true;
        CanResize = false;
        IsHitTestVisible = false;
        SizeToContent = SizeToContent.WidthAndHeight;
        Opacity = 0;
        Transitions = [new DoubleTransition { Property = OpacityProperty, Duration = TimeSpan.FromMilliseconds(150) }];

        var stack = new StackPanel();
        var shown = payload.Cards.Take(MaxCards).ToList();
        for (var i = 0; i < shown.Count; i++)
            stack.Children.Add(Card(shown[i], payload.Numbered ? i + 1 : null));
        if (payload.Cards.Count > MaxCards)
            stack.Children.Add(new TextBlock
            {
                Text = $"+ {payload.Cards.Count - MaxCards} more", Margin = new Thickness(6, 2, 0, 0), FontSize = 12,
                Foreground = Brush("MutedBrush"),
            });
        stack.Children.Add(new TextBlock
        {
            Text = payload.Caption, Margin = new Thickness(6, 8, 0, 0), FontSize = 12, FontWeight = FontWeight.SemiBold,
            Foreground = Brush("TextBrush"),
        });

        var root = new Grid { Margin = new Thickness(14) };   // room for the shadow and the badge
        root.Children.Add(new Border
        {
            Padding = new Thickness(10), CornerRadius = new CornerRadius(12), Width = 280,
            Background = Brush("SidebarBrush"), BorderBrush = Brush("AccentBrush"), BorderThickness = new Thickness(1),
            BoxShadow = BoxShadows.Parse("0 6 24 0 #73000000"),
            Child = stack,
        });
        if (payload.Cards.Count > 1)
            root.Children.Add(new Border
            {
                Width = 26, Height = 26, CornerRadius = new CornerRadius(13), Background = Brush("AccentBrush"),
                HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Top,
                Margin = new Thickness(0, -10, -10, 0),
                Child = new TextBlock
                {
                    Text = payload.Cards.Count.ToString(), Foreground = Brushes.White, FontWeight = FontWeight.Bold, FontSize = 12,
                    HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center,
                },
            });
        Content = root;

        _follow = new DispatcherTimer(TimeSpan.FromMilliseconds(15), DispatcherPriority.Render, (_, _) => FollowCursor());
    }

    Control Card(DragCard card, int? number)
    {
        var row = new DockPanel();
        if (number is { } n)
            row.Children.Add(Docked(new TextBlock
            {
                Text = n.ToString(), Width = 18, FontSize = 12, FontWeight = FontWeight.SemiBold,
                Foreground = Brush("FaintBrush"), VerticalAlignment = VerticalAlignment.Center,
            }));
        row.Children.Add(Docked(new Border
        {
            Width = 28, Height = 28, CornerRadius = new CornerRadius(6), Background = Brush("AccentFaintBrush"),
            Margin = new Thickness(0, 0, 10, 0),
            Child = new TextBlock
            {
                Text = card.Glyph, FontFamily = new FontFamily("Segoe Fluent Icons, Segoe MDL2 Assets"), FontSize = 14,
                Foreground = Brush("TextBrush"), HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center,
            },
        }));
        var text = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        text.Children.Add(new TextBlock { Text = card.Title, FontWeight = FontWeight.SemiBold, Foreground = Brush("TextBrush"), TextTrimming = TextTrimming.CharacterEllipsis });
        if (card.Detail.Length > 0)
            text.Children.Add(new TextBlock { Text = card.Detail, FontSize = 11, Foreground = Brush("MutedBrush"), TextTrimming = TextTrimming.CharacterEllipsis });
        row.Children.Add(text);

        return new Border
        {
            Margin = new Thickness(0, 0, 0, 4), Padding = new Thickness(8, 6, 8, 6), CornerRadius = new CornerRadius(8),
            Background = Brush("SurfaceRaisedBrush"), BorderBrush = Brush("StrokeBrush"), BorderThickness = new Thickness(1),
            Child = row,
        };
    }

    static Control Docked(Control c)
    {
        DockPanel.SetDock(c, Dock.Left);
        return c;
    }

    static IBrush? Brush(string key) =>
        Application.Current?.TryGetResource(key, null, out var value) == true ? value as IBrush : null;

    public void ShowNearCursor()
    {
        FollowCursor();
        Show();
        Opacity = 0.96;
        _follow.Start();
    }

    /// <summary>Sits just below and to the right of the cursor, so it never covers the drop point.</summary>
    void FollowCursor()
    {
        if (!GetCursorPos(out var p)) return;
        var scale = DesktopScaling;
        Position = new PixelPoint(p.X + (int)(8 * scale), p.Y + (int)(10 * scale));
    }

    public void FadeAway()
    {
        _follow.Stop();
        Opacity = 0;
        DispatcherTimer.RunOnce(Close, TimeSpan.FromMilliseconds(170));
    }

    protected override void OnOpened(EventArgs e)
    {
        base.OnOpened(e);
        // Click-through and out of Alt+Tab, so the drop goes to the window underneath.
        if (TryGetPlatformHandle()?.Handle is not { } hwnd || hwnd == 0) return;
        var ex = GetWindowLong(hwnd, GwlExStyle);
        SetWindowLong(hwnd, GwlExStyle, ex | WsExTransparent | WsExToolWindow | WsExNoActivate | WsExLayered);
    }

    const int GwlExStyle = -20, WsExTransparent = 0x20, WsExToolWindow = 0x80, WsExNoActivate = 0x08000000, WsExLayered = 0x80000;

    [StructLayout(LayoutKind.Sequential)]
    struct CursorPoint { public int X, Y; }

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GetCursorPos(out CursorPoint p);

    [LibraryImport("user32.dll", EntryPoint = "GetWindowLongW")]
    private static partial int GetWindowLong(nint hwnd, int index);

    [LibraryImport("user32.dll", EntryPoint = "SetWindowLongW")]
    private static partial int SetWindowLong(nint hwnd, int index, int value);
}
