using System.ComponentModel;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Controls.Shapes;
using Avalonia.Layout;
using CommunityToolkit.Mvvm.Input;

namespace MusicOrganiser.Avalonia.Views;

/// <summary>
/// A button whose job takes a moment shows it: while its command is still running, its content dims and a spinner
/// turns over it (the "busy" class), so a click always visibly did something. App.axaml turns this on for every
/// button; buttons with quick commands never notice.
/// </summary>
public static class BusyButton
{
    public static readonly AttachedProperty<bool> TrackProperty =
        AvaloniaProperty.RegisterAttached<Button, bool>("Track", typeof(BusyButton));

    public static bool GetTrack(Button b) => b.GetValue(TrackProperty);
    public static void SetTrack(Button b, bool value) => b.SetValue(TrackProperty, value);

    static readonly AttachedProperty<Watcher?> WatcherProperty =
        AvaloniaProperty.RegisterAttached<Button, Watcher?>("Watcher", typeof(BusyButton));

    static BusyButton()
    {
        TrackProperty.Changed.AddClassHandler<Button>((b, e) =>
        {
            b.GetValue(WatcherProperty)?.Dispose();
            b.SetValue(WatcherProperty, e.NewValue is true ? new Watcher(b) : null);
        });
    }

    sealed class Watcher : IDisposable
    {
        readonly Button _button;
        IAsyncRelayCommand? _command;

        public Watcher(Button button)
        {
            _button = button;
            _button.PropertyChanged += OnButtonChanged;
            Watch(_button.Command);
        }

        void OnButtonChanged(object? sender, AvaloniaPropertyChangedEventArgs e)
        {
            if (e.Property == Button.CommandProperty) Watch(_button.Command);
        }

        void Watch(object? command)
        {
            if (_command is not null) _command.PropertyChanged -= OnCommandChanged;
            _command = command as IAsyncRelayCommand;
            if (_command is not null) _command.PropertyChanged += OnCommandChanged;
            Show(_command?.IsRunning == true);
        }

        void OnCommandChanged(object? sender, PropertyChangedEventArgs e)
        {
            if (e.PropertyName == nameof(IAsyncRelayCommand.IsRunning)) Show(_command!.IsRunning);
        }

        void Show(bool busy)
        {
            if (_button.Classes.Contains("busy") == busy) return;
            _button.Classes.Set("busy", busy);
            AdornerLayer.SetAdorner(_button, busy ? Spinner(Math.Min(18, Math.Max(12, _button.Bounds.Height * 0.4))) : null);
        }

        public void Dispose()
        {
            _button.PropertyChanged -= OnButtonChanged;
            Watch(null);
        }
    }

    /// <summary>The turning arc used wherever the app is waiting on something.</summary>
    public static Control Spinner(double size) => new Panel
    {
        IsHitTestVisible = false,
        Children =
        {
            new Arc
            {
                Classes = { "spinner" },
                Width = size,
                Height = size,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
            },
        },
    };
}
