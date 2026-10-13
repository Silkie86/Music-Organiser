using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Effects;
using MusicOrganiser.App.ViewModels;

namespace MusicOrganiser.App.Views;

/// <summary>
/// The card that follows the cursor during a drag: what's being carried, how many, and for
/// MIDI the tracks in the order they'll land. It floats above every app (so it shows over the
/// DAW too) and lets the mouse pass straight through to whatever is underneath.
/// </summary>
sealed class DragPreview : Window
{
    const int MaxCards = 5;
    readonly Border _panel;
    readonly ScaleTransform _scale = new(0.88, 0.88);
    readonly TextBlock _caption;
    readonly string _text;

    public DragPreview(DragPayload payload)
    {
        WindowStyle = WindowStyle.None;
        AllowsTransparency = true;
        Background = Brushes.Transparent;
        ShowInTaskbar = false;
        ShowActivated = false;
        Topmost = true;
        IsHitTestVisible = false;
        SizeToContent = SizeToContent.WidthAndHeight;
        ResizeMode = ResizeMode.NoResize;
        Opacity = 0;
        _text = payload.Caption;

        var stack = new StackPanel();
        var shown = payload.Cards.Take(MaxCards).ToList();
        for (var i = 0; i < shown.Count; i++)
            stack.Children.Add(Card(shown[i], payload.Numbered ? i + 1 : null, i));
        if (payload.Cards.Count > MaxCards)
            stack.Children.Add(new TextBlock
            {
                Text = $"+ {payload.Cards.Count - MaxCards} more", Margin = new Thickness(6, 2, 0, 0), FontSize = 12,
                Foreground = Brush("MutedBrush"),
            });
        _caption = new TextBlock
        {
            Text = _text, Margin = new Thickness(6, 8, 0, 0), FontSize = 12, FontWeight = FontWeights.SemiBold,
            Foreground = Brush("TextBrush"),
        };
        stack.Children.Add(_caption);

        _panel = new Border
        {
            Padding = new Thickness(10), CornerRadius = new CornerRadius(12), Width = 280,
            Background = Brush("SidebarBrush"), BorderBrush = Brush("AccentBrush"), BorderThickness = new Thickness(1),
            Effect = new DropShadowEffect { BlurRadius = 24, ShadowDepth = 6, Opacity = 0.45, Color = Colors.Black },
            Child = stack,
            RenderTransform = _scale, RenderTransformOrigin = new Point(0, 0),
        };

        var root = new Grid { Margin = new Thickness(14) };   // room for the shadow and the badge
        root.Children.Add(_panel);
        if (payload.Cards.Count > 1)
        {
            var badge = new Border
            {
                Width = 26, Height = 26, CornerRadius = new CornerRadius(13), Background = Brush("AccentBrush"),
                HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Top,
                Margin = new Thickness(0, -10, -10, 0),
                Child = new TextBlock
                {
                    Text = payload.Cards.Count.ToString(), Foreground = Brushes.White, FontWeight = FontWeights.Bold, FontSize = 12,
                    HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center,
                },
                RenderTransform = new ScaleTransform(0, 0), RenderTransformOrigin = new Point(0.5, 0.5),
            };
            root.Children.Add(badge);
            Pop((ScaleTransform)badge.RenderTransform, 120);
        }
        Content = root;
    }

    UIElement Card(DragCard card, int? number, int index)
    {
        var row = new DockPanel();
        if (number is { } n)
            row.Children.Add(Docked(new TextBlock
            {
                Text = n.ToString(), Width = 18, FontSize = 12, FontWeight = FontWeights.SemiBold,
                Foreground = Brush("FaintBrush"), VerticalAlignment = VerticalAlignment.Center,
            }));
        row.Children.Add(Docked(new Border
        {
            Width = 28, Height = 28, CornerRadius = new CornerRadius(6), Background = Brush("AccentSoftBrush"),
            Margin = new Thickness(0, 0, 10, 0),
            Child = new TextBlock
            {
                Text = card.Glyph, FontFamily = new FontFamily("Segoe Fluent Icons, Segoe MDL2 Assets"), FontSize = 14,
                Foreground = Brush("TextBrush"), HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center,
            },
        }));
        var text = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        text.Children.Add(new TextBlock { Text = card.Title, FontWeight = FontWeights.SemiBold, Foreground = Brush("TextBrush"), TextTrimming = TextTrimming.CharacterEllipsis });
        if (card.Detail.Length > 0)
            text.Children.Add(new TextBlock { Text = card.Detail, FontSize = 11, Foreground = Brush("MutedBrush"), TextTrimming = TextTrimming.CharacterEllipsis });
        row.Children.Add(text);

        // Each card slides in a moment after the one above, like tracks being laid down.
        var shift = new TranslateTransform(0, 10);
        var border = new Border
        {
            Margin = new Thickness(0, 0, 0, 4), Padding = new Thickness(8, 6, 8, 6), CornerRadius = new CornerRadius(8),
            Background = Brush("SurfaceRaisedBrush"), BorderBrush = Brush("StrokeBrush"), BorderThickness = new Thickness(1),
            Child = row, Opacity = 0, RenderTransform = shift,
        };
        var delay = TimeSpan.FromMilliseconds(40 * index);
        var ease = new CubicEase { EasingMode = EasingMode.EaseOut };
        border.BeginAnimation(OpacityProperty, new DoubleAnimation(1, TimeSpan.FromMilliseconds(180)) { BeginTime = delay, EasingFunction = ease });
        shift.BeginAnimation(TranslateTransform.YProperty, new DoubleAnimation(0, TimeSpan.FromMilliseconds(220)) { BeginTime = delay, EasingFunction = ease });
        return border;
    }

    static UIElement Docked(UIElement e)
    {
        DockPanel.SetDock(e, Dock.Left);
        return e;
    }

    static Brush Brush(string key) => (Brush)Application.Current.Resources[key];

    static void Pop(ScaleTransform t, int delayMs)
    {
        var a = new DoubleAnimation(1, TimeSpan.FromMilliseconds(260))
        {
            BeginTime = TimeSpan.FromMilliseconds(delayMs), EasingFunction = new BackEase { Amplitude = 0.5, EasingMode = EasingMode.EaseOut },
        };
        t.BeginAnimation(ScaleTransform.ScaleXProperty, a);
        t.BeginAnimation(ScaleTransform.ScaleYProperty, a);
    }

    public void ShowNearCursor()
    {
        Show();
        FollowCursor();
        BeginAnimation(OpacityProperty, new DoubleAnimation(0.96, TimeSpan.FromMilliseconds(140)));
        var grow = new DoubleAnimation(1, TimeSpan.FromMilliseconds(200)) { EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut } };
        _scale.BeginAnimation(ScaleTransform.ScaleXProperty, grow);
        _scale.BeginAnimation(ScaleTransform.ScaleYProperty, grow);
    }

    /// <summary>Sits just below and to the right of the cursor, so it never covers the drop point.</summary>
    public void FollowCursor()
    {
        if (!GetCursorPos(out var p)) return;
        var dpi = VisualTreeHelper.GetDpi(this);
        Left = p.X / dpi.DpiScaleX + 8;
        Top = p.Y / dpi.DpiScaleY + 10;
    }

    bool? _accepted;

    /// <summary>Over somewhere that takes the files, the caption says so; elsewhere it shows the plain caption.</summary>
    public void SetAccepted(bool accepted)
    {
        if (_accepted == accepted) return;
        _accepted = accepted;
        _caption.Text = accepted ? $"Drop to add · {_text}" : _text;
        _panel.BorderBrush = Brush(accepted ? "OkBrush" : "AccentBrush");
    }

    public void FadeAway()
    {
        var fade = new DoubleAnimation(0, TimeSpan.FromMilliseconds(160));
        fade.Completed += (_, _) => Close();
        BeginAnimation(OpacityProperty, fade);
        var shrink = new DoubleAnimation(0.92, TimeSpan.FromMilliseconds(160));
        _scale.BeginAnimation(ScaleTransform.ScaleXProperty, shrink);
        _scale.BeginAnimation(ScaleTransform.ScaleYProperty, shrink);
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        // Click-through and out of Alt+Tab, so the drop goes to the window underneath.
        var hwnd = new WindowInteropHelper(this).Handle;
        var ex = GetWindowLong(hwnd, GwlExStyle);
        SetWindowLong(hwnd, GwlExStyle, ex | WsExTransparent | WsExToolWindow | WsExNoActivate | WsExLayered);
    }

    const int GwlExStyle = -20, WsExTransparent = 0x20, WsExToolWindow = 0x80, WsExNoActivate = 0x08000000, WsExLayered = 0x80000;

    [StructLayout(LayoutKind.Sequential)]
    struct CursorPoint { public int X, Y; }

    [DllImport("user32.dll")] static extern bool GetCursorPos(out CursorPoint p);
    [DllImport("user32.dll")] static extern int GetWindowLong(IntPtr hwnd, int index);
    [DllImport("user32.dll")] static extern int SetWindowLong(IntPtr hwnd, int index, int value);
}
