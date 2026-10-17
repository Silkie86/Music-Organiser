using System.Text;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using MusicOrganiser.Core;
using MusicOrganiser.Core.LiveTests;
using MusicOrganiser.ViewModels;

namespace MusicOrganiser.Avalonia.Diagnostics;

/// <summary>
/// F12 anywhere in the app: a note to Claude about what's on screen, saved with a picture of the window and the
/// app's last log lines in notes\ in the app's data folder. Claude reads that folder from Solomon's computer, so a
/// test at home needs no copying out, screenshots or describing which build it was.
/// </summary>
public static class NoteForClaude
{
    public static readonly KeyGesture Gesture = new(Key.F12);

    /// <summary>notes\ in the app's data folder.</summary>
    public static string DefaultFolder => Path.Combine(LiveReports.DataFolder, "notes");

    public static async Task AskAsync(Window owner, MainViewModel main)
    {
        // The picture is taken first, so it shows the app as it was, without the note window over it.
        var picture = Picture(owner);
        var page = main.SelectedNav?.Title ?? "";
        var dialog = new NoteWindow(picture is not null);
        if (await dialog.ShowDialog<bool>(owner) != true) return;
        try
        {
            var saved = Save(DefaultFolder, dialog.Text, page, dialog.IncludePicture ? picture : null, DateTime.Now);
            SessionLog.Current?.Info("Note", $"Note for Claude saved: {saved}");
            main.Activities.Done("Note saved for Claude", "Claude can read it from the app's folder now.", "Show", () => Shell.Reveal(saved));
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            SessionLog.Current?.Error("Note", "Couldn't save a note for Claude", e);
            main.Activities.Failed("Couldn't save the note", e.Message);
        }
    }

    /// <summary>Writes note-&lt;time&gt;.md, and .png beside it when there's a picture. Returns the note's path.</summary>
    public static string Save(string folder, string text, string page, byte[]? picture, DateTime now)
    {
        Directory.CreateDirectory(folder);
        var name = $"note-{now:yyyy-MM-dd_HH-mm-ss}";
        var md = new StringBuilder()
            .AppendLine($"# Note for Claude, {now:yyyy-MM-dd HH:mm:ss}")
            .AppendLine()
            .AppendLine(string.IsNullOrWhiteSpace(text) ? picture is null ? "(no words)" : "(no words, just the picture)" : text.Trim())
            .AppendLine()
            .AppendLine($"- Page: {(page.Length > 0 ? page : "?")}")
            .AppendLine($"- Version: {LiveReports.AppVersion}");
        if (SessionLog.Current is { } log)
        {
            md.AppendLine($"- Log: {log.Path}");
        }
        if (picture is not null)
        {
            File.WriteAllBytes(Path.Combine(folder, name + ".png"), picture);
            md.AppendLine($"- Picture: {name}.png");
        }
        if (SessionLog.Current?.Recent() is { Count: > 0 } recent)
        {
            md.AppendLine().AppendLine("## The app's last log lines").AppendLine().AppendLine("```");
            foreach (var line in recent.TakeLast(40)) md.AppendLine(line);
            md.AppendLine("```");
        }
        var path = Path.Combine(folder, name + ".md");
        File.WriteAllText(path, md.ToString());
        return path;
    }

    /// <summary>The window as it looks now, as a PNG; null if it can't be drawn.</summary>
    static byte[]? Picture(Window window)
    {
        try
        {
            var scale = window.RenderScaling;
            var size = new PixelSize(Math.Max(1, (int)(window.Bounds.Width * scale)), Math.Max(1, (int)(window.Bounds.Height * scale)));
            using var bitmap = new RenderTargetBitmap(size, new Vector(96 * scale, 96 * scale));
            bitmap.Render(window);
            using var png = new MemoryStream();
            bitmap.Save(png, new PngBitmapEncoderOptions());
            return png.ToArray();
        }
        catch (Exception e)
        {
            SessionLog.Current?.Warning("Note", $"Couldn't take a picture of the window: {e.Message}");
            return null;
        }
    }

    sealed class NoteWindow : Window
    {
        readonly TextBox _text;
        readonly CheckBox _picture;

        public string Text => _text.Text ?? "";
        public bool IncludePicture => _picture.IsChecked == true;

        public NoteWindow(bool canPicture)
        {
            Title = "Note for Claude";
            Width = 480;
            SizeToContent = SizeToContent.Height;
            CanResize = false;
            WindowStartupLocation = WindowStartupLocation.CenterOwner;

            _text = new TextBox
            {
                AcceptsReturn = true,
                TextWrapping = TextWrapping.Wrap,
                Height = 130,
                PlaceholderText = "What did you try, and what happened? (Ctrl+Enter saves)",
            };
            _picture = new CheckBox { Content = "Add a picture of the app as it was", IsChecked = canPicture, IsEnabled = canPicture };

            var save = new Button { Content = "Save note", IsDefault = false, MinWidth = 90, Classes = { "accent" } };
            save.Click += (_, _) => Close(true);
            var cancel = new Button { Content = "Cancel", IsCancel = true, MinWidth = 80 };
            cancel.Click += (_, _) => Close(false);

            Content = new StackPanel
            {
                Margin = new Thickness(20),
                Spacing = 12,
                Children =
                {
                    new TextBlock
                    {
                        Text = "Saved in the app's folder with the app's recent log, where Claude can read it. Nothing is sent anywhere.",
                        TextWrapping = TextWrapping.Wrap,
                    },
                    _text,
                    _picture,
                    new StackPanel
                    {
                        Orientation = Orientation.Horizontal, Spacing = 8, HorizontalAlignment = HorizontalAlignment.Right,
                        Children = { cancel, save },
                    },
                },
            };

            // Ahead of the text box, which would otherwise take Enter for a new line.
            AddHandler(KeyDownEvent, (_, e) =>
            {
                if (e.Key == Key.Enter && e.KeyModifiers.HasFlag(KeyModifiers.Control)) { e.Handled = true; Close(true); }
            }, RoutingStrategies.Tunnel);
            Opened += (_, _) => _text.Focus();
        }
    }
}
