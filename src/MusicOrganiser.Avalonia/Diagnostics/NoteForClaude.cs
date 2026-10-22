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

    /// <summary>
    /// Says a note is being written (it holds the app's process id). The run-latest launcher waits for it to go before it
    /// restarts the app for a new build, so a note isn't lost half-way through.
    /// </summary>
    public const string WritingFile = "writing.lock";

    /// <summary>What's typed so far, kept as it's typed: if the app closes anyway, the next F12 offers it back.</summary>
    public const string DraftFile = "draft.txt";

    public static async Task AskAsync(Window owner, MainViewModel main)
    {
        // The picture is taken first, so it shows the app as it was, without the note window over it.
        var picture = Picture(owner);
        var page = main.SelectedNav?.Title ?? "";
        var folder = DefaultFolder;
        var dialog = new NoteWindow(picture is not null, ReadDraft(folder), text => KeepDraft(folder, text));
        bool saved;
        Writing(folder, true);
        try { saved = await dialog.ShowDialog<bool>(owner); }
        finally { Writing(folder, false); }
        // Saved or cancelled on purpose: either way the draft has done its job.
        ClearDraft(folder);
        if (!saved) return;
        try
        {
            var note = Save(folder, dialog.Text, page, dialog.IncludePicture ? picture : null, DateTime.Now);
            SessionLog.Current?.Info("Note", $"Note for Claude saved: {note}");
            main.Activities.Done("Note saved for Claude", "Claude can read it from the app's folder now.", "Show", () => Shell.Reveal(note));
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

    /// <summary>The note typed so far that was never saved (the app closed with the note open), or null.</summary>
    public static string? ReadDraft(string folder)
    {
        try
        {
            var path = Path.Combine(folder, DraftFile);
            return File.Exists(path) && File.ReadAllText(path) is { Length: > 0 } text ? text : null;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return null; }
    }

    public static void KeepDraft(string folder, string text)
    {
        try
        {
            Directory.CreateDirectory(folder);
            File.WriteAllText(Path.Combine(folder, DraftFile), text);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }   // the note itself still saves
    }

    public static void ClearDraft(string folder)
    {
        try { File.Delete(Path.Combine(folder, DraftFile)); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
    }

    /// <summary>Puts up or takes down <see cref="WritingFile"/>.</summary>
    public static void Writing(string folder, bool writing)
    {
        var path = Path.Combine(folder, WritingFile);
        try
        {
            if (!writing) { File.Delete(path); return; }
            Directory.CreateDirectory(folder);
            File.WriteAllText(path, Environment.ProcessId.ToString(System.Globalization.CultureInfo.InvariantCulture));
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
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

        public NoteWindow(bool canPicture, string? draft, Action<string> typed)
        {
            Title = "Note for Claude";
            Classes.Add("studio"); // the app's one look (Studio.axaml)
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
                Text = draft,
            };
            _text.TextChanged += (_, _) => typed(_text.Text ?? "");
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
                        Text = draft is null
                            ? "Saved in the app's folder with the app's recent log, where Claude can read it. Nothing is sent anywhere."
                            : "Here's the note you were writing when the app closed. Carry on, or Cancel to drop it.",
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
