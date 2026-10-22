using System.Text.RegularExpressions;
using Xunit;

namespace MusicOrganiser.Avalonia.Tests;

/// <summary>
/// The app has one look (Solomon, 2026-10-22: "one cohesive design"): every window wears Studio.axaml, and pages use its
/// shared parts instead of styling their own buttons, boxes and panels, so changing a look there changes it everywhere.
/// These read the pages' markup and fail when a page goes its own way.
/// </summary>
public sealed partial class StyleCheckTests
{
    /// <summary>What a control looks like, as opposed to where it sits: these belong to the shared look.</summary>
    static readonly HashSet<string> LookProperties =
        ["Background", "BorderBrush", "BorderThickness", "CornerRadius", "BoxShadow", "Template", "Foreground", "FontWeight", "FontSize", "FontFamily", "LetterSpacing"];

    /// <summary>Controls whose whole look comes from Studio.axaml.</summary>
    static readonly HashSet<string> SharedControls = ["Button", "ToggleButton", "RepeatButton", "TextBox", "ComboBox", "TabItem", "NumericUpDown"];

    /// <summary>Fixed colours that stay on purpose, with why.</summary>
    static readonly Dictionary<(string File, string Colour), string> AllowedColours = new()
    {
        [("MainWindow.axaml", "#99000000")] = "the dimmed backdrop behind the keys sheet, the same in every theme",
        [("SettingsView.axaml", "#F2994A")] = "the orange song marker drawn in each theme's little preview",
    };

    static string Ui
    {
        get
        {
            for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir != null; dir = dir.Parent)
                if (Directory.Exists(Path.Combine(dir.FullName, "src", "MusicOrganiser.Avalonia")))
                    return Path.Combine(dir.FullName, "src", "MusicOrganiser.Avalonia");
            throw new DirectoryNotFoundException("Couldn't find src/MusicOrganiser.Avalonia above the test folder.");
        }
    }

    /// <summary>The pages and windows: everything but the shared style files.</summary>
    static IEnumerable<string> Pages() =>
        Directory.EnumerateFiles(Path.Combine(Ui, "Views"), "*.axaml").Append(Path.Combine(Ui, "MainWindow.axaml"));

    static IEnumerable<string> SourceFiles() =>
        Directory.EnumerateFiles(Ui, "*.cs", SearchOption.AllDirectories)
            .Where(f => !f.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}") && !f.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}"));

    [Fact]
    public void Every_window_wears_the_studio_look()
    {
        var missing = new List<string>();
        foreach (var file in Pages().Where(f => Regex.IsMatch(File.ReadAllText(f), @"^\s*<Window\b", RegexOptions.Multiline)))
            if (!Regex.IsMatch(File.ReadAllText(file), @"<Window\b[^>]*\bClasses=""[^""]*\bstudio\b"))
                missing.Add(Path.GetFileName(file));
        // Windows written in code (a window with markup gets its class there, above).
        foreach (var file in SourceFiles().Where(f => !File.Exists(f[..^3])))
        {
            var code = File.ReadAllText(file);
            foreach (Match m in WindowClass().Matches(code))
            {
                // A drag preview is a picture that follows the pointer, not a window with controls in it.
                if (m.Groups[1].Value == "DragPreview") continue;
                var body = code[m.Index..];
                var next = WindowClass().Match(body, m.Length);
                if (next.Success) body = body[..next.Index];
                if (!body.Contains("Classes.Add(\"studio\")")) missing.Add($"{Path.GetFileName(file)} ({m.Groups[1].Value})");
            }
        }
        // Windows made on the spot (the arrangement's full-size window), in any code file.
        foreach (var file in SourceFiles())
        {
            var code = File.ReadAllText(file);
            foreach (Match m in NewWindow().Matches(code))
            {
                var name = m.Groups[1].Value;
                if (!Regex.IsMatch(code[m.Index..], $@"\b{name}\.Classes\.Add\(""studio""\)"))
                    missing.Add($"{Path.GetFileName(file)} ({name} = new Window)");
            }
        }
        Assert.True(missing.Count == 0, "These windows don't have the class \"studio\", so they miss the app's look: " + string.Join(", ", missing));
    }

    [Fact]
    public void Pages_leave_buttons_and_boxes_to_the_shared_look()
    {
        var found = new List<string>();
        foreach (var file in Pages())
        {
            var xaml = File.ReadAllText(file);
            var name = Path.GetFileName(file);
            foreach (var (selector, setters) in PageStyles(xaml))
                foreach (var part in selector.Split(','))
                {
                    var target = Target(part, out var inTemplate);
                    if (!SharedControls.Contains(target)) continue;
                    if (inTemplate) { found.Add($"{name}: {part.Trim()} restyles the inside of a {target}"); continue; }
                    // A smaller version of a box or button (FontSize) is fine; its face, edge and corners aren't.
                    foreach (var p in setters.Where(p => LookProperties.Contains(p) && p != "FontSize"))
                        found.Add($"{name}: {part.Trim()} sets {p}");
                }
            foreach (Match m in InlineButtonLook().Matches(xaml))
                found.Add($"{name}: a {m.Groups[1].Value} sets {m.Groups[2].Value} itself (use flat, link, accent, icon, tiny or trouble)");
        }
        Assert.True(found.Count == 0, "Use the shared parts in Studio.axaml instead:\n" + string.Join("\n", found));
    }

    [Fact]
    public void Pages_dont_restyle_the_shared_parts()
    {
        // Every Type.name Studio.axaml gives a look to, e.g. Border.panel, StackPanel.group, TextBlock.small.
        var shared = new HashSet<string>(SharedPart().Matches(File.ReadAllText(Path.Combine(Ui, "Studio.axaml"))).Select(m => m.Groups[1].Value));
        var found = new List<string>();
        foreach (var file in Pages())
            foreach (var (selector, setters) in PageStyles(File.ReadAllText(file)))
                foreach (var part in selector.Split(','))
                {
                    var last = LastStep(part);
                    if (!shared.Contains(StripStates(last))) continue;
                    foreach (var p in setters.Where(LookProperties.Contains))
                        found.Add($"{Path.GetFileName(file)}: {part.Trim()} sets {p}, but {StripStates(last)} is a shared part");
                }
        Assert.True(found.Count == 0, "Change the shared part in Studio.axaml, or give this its own name:\n" + string.Join("\n", found));
    }

    [Fact]
    public void Pages_take_their_colours_from_the_theme()
    {
        var found = new List<string>();
        foreach (var file in Pages())
            foreach (Match m in Colour().Matches(File.ReadAllText(file)))
                if (!AllowedColours.ContainsKey((Path.GetFileName(file), m.Groups[1].Value)))
                    found.Add($"{Path.GetFileName(file)}: {m.Groups[1].Value}");
        Assert.True(found.Count == 0, "Use a theme brush from App.axaml (it follows Settings › Appearance) instead of: " + string.Join(", ", found));
    }

    /// <summary>The page's own styles, as their selectors and the properties they set.</summary>
    static IEnumerable<(string Selector, List<string> Setters)> PageStyles(string xaml)
    {
        foreach (Match m in StyleBlock().Matches(xaml))
            yield return (m.Groups[1].Value, SetterProperty().Matches(m.Groups[2].Value).Select(s => s.Groups[1].Value).ToList());
    }

    /// <summary>The last step of a selector ("Border.row.copy:pointerover"), with a namespace prefix dropped.</summary>
    static string LastStep(string selector)
    {
        var steps = selector.Split([' ', '>', '\n', '\r', '\t'], StringSplitOptions.RemoveEmptyEntries);
        var last = steps.Length == 0 ? "" : steps[^1];
        return last.Contains('|') ? last[(last.IndexOf('|') + 1)..] : last;
    }

    static string StripStates(string step) => Regex.Replace(step, @"[:\[#].*$", "");

    /// <summary>The control a selector styles; <paramref name="inTemplate"/> when it reaches inside its template.</summary>
    static string Target(string selector, out bool inTemplate)
    {
        var at = selector.IndexOf("/template/", StringComparison.Ordinal);
        inTemplate = at >= 0;
        var step = LastStep(inTemplate ? selector[..at] : selector);
        return Regex.Match(step, @"^[A-Za-z]+").Value;
    }

    [GeneratedRegex(@"\bclass\s+(\w+)\s*(?:\([^)]*\))?\s*:\s*Window\b")]
    private static partial Regex WindowClass();

    [GeneratedRegex(@"\b(\w+)\s*=\s*new\s+Window\b")]
    private static partial Regex NewWindow();

    [GeneratedRegex(@"<Style\s+Selector=""([^""]*)""\s*>(.*?)</Style>", RegexOptions.Singleline)]
    private static partial Regex StyleBlock();

    [GeneratedRegex(@"<Setter\s+Property=""([A-Za-z.]+)""")]
    private static partial Regex SetterProperty();

    [GeneratedRegex(@"<(Button|ToggleButton|RepeatButton)\b[^>]*?\s(Background|BorderBrush|CornerRadius|BorderThickness|BoxShadow)=""")]
    private static partial Regex InlineButtonLook();

    [GeneratedRegex(@"\.studio\s+([A-Za-z]+\.[A-Za-z]+)(?=[\s,"":/>])")]
    private static partial Regex SharedPart();

    [GeneratedRegex(@"=""(#[0-9A-Fa-f]{6,8})""")]
    private static partial Regex Colour();
}

/// <summary>The studio look reaching what pops up from a page: a flyout's buttons look like the page's.</summary>
public sealed class StudioPopupTests
{
    [global::Avalonia.Headless.XUnit.AvaloniaFact]
    public void A_flyout_from_a_studio_window_has_the_studio_look()
    {
        var inside = new global::Avalonia.Controls.Button { Content = "Inside" };
        var opener = new global::Avalonia.Controls.Button { Content = "Open", Flyout = new global::Avalonia.Controls.Flyout { Content = inside } };
        var window = new global::Avalonia.Controls.Window { Content = opener, Width = 400, Height = 300, Classes = { "studio" } };
        window.Show();
        global::Avalonia.Threading.Dispatcher.UIThread.RunJobs();
        opener.Flyout!.ShowAt(opener);
        global::Avalonia.Threading.Dispatcher.UIThread.RunJobs();

        Assert.True(inside.IsVisible && inside.Bounds.Height > 0, "The flyout's button should be shown");
        Assert.Equal(24, inside.MinHeight); // Studio.axaml's button height, so the studio look reached it
        window.Close();
    }
}

/// <summary>The shared look on real controls: the parts the markup checks above can't see.</summary>
public sealed class StudioLookTests
{
    static global::Avalonia.Controls.Window Show(global::Avalonia.Controls.Control content)
    {
        var window = new global::Avalonia.Controls.Window { Content = content, Width = 600, Height = 400, Classes = { "studio" } };
        window.Show();
        global::Avalonia.Threading.Dispatcher.UIThread.RunJobs();
        return window;
    }

    static global::Avalonia.Thickness WellEdge(global::Avalonia.Controls.Button b) =>
        global::Avalonia.VisualTree.VisualExtensions.GetVisualDescendants(b).OfType<global::Avalonia.Controls.Border>().First(x => x.Name == "PART_Well").BorderThickness;

    [global::Avalonia.Headless.XUnit.AvaloniaFact]
    public void A_strip_closes_after_its_last_shown_button()
    {
        // Like a song's Open strip when it isn't an FL song and has no older saves.
        var open = new global::Avalonia.Controls.Button { Content = "Open" };
        var fl = new global::Avalonia.Controls.Button { Content = "Open in FL Studio", IsVisible = false };
        var older = new global::Avalonia.Controls.Button { Content = "Older saves", IsVisible = false };
        var window = Show(new global::Avalonia.Controls.StackPanel { Classes = { "group" }, Children = { open, fl, older } });

        Assert.Equal(new global::Avalonia.Thickness(1), WellEdge(open));

        older.IsVisible = true; // and when it shows again, the strip's end moves to it
        global::Avalonia.Threading.Dispatcher.UIThread.RunJobs();
        Assert.Equal(new global::Avalonia.Thickness(1, 1, 0, 1), WellEdge(open));
        Assert.Equal(new global::Avalonia.Thickness(0, 1, 1, 1), WellEdge(older));
        window.Close();
    }

    [global::Avalonia.Headless.XUnit.AvaloniaFact]
    public void A_main_button_that_is_off_doesnt_look_lit()
    {
        var off = new global::Avalonia.Controls.Button { Content = "Find duplicates", Classes = { "accent" }, IsEnabled = false };
        var on = new global::Avalonia.Controls.Button { Content = "Find duplicates", Classes = { "accent" } };
        var window = Show(new global::Avalonia.Controls.StackPanel { Children = { off, on } });

        static global::Avalonia.Media.IBrush? Face(global::Avalonia.Controls.Button b) =>
            global::Avalonia.VisualTree.VisualExtensions.GetVisualDescendants(b).OfType<global::Avalonia.Controls.Presenters.ContentPresenter>().First().Background;
        Assert.NotEqual(Face(on), Face(off));
        Assert.True(global::Avalonia.Controls.ResourceNodeExtensions.TryFindResource(window, "AccentBrush", out var accent));
        Assert.Same(accent, Face(on));
        window.Close();
    }

    [global::Avalonia.Headless.XUnit.AvaloniaFact]
    public void A_search_box_cross_stays_quiet()
    {
        var box = new global::Avalonia.Controls.TextBox { Text = "techno", Classes = { "clearButton" }, Width = 200 };
        var window = Show(box);
        box.Focus();
        global::Avalonia.Threading.Dispatcher.UIThread.RunJobs();

        var cross = global::Avalonia.VisualTree.VisualExtensions.GetVisualDescendants(box).OfType<global::Avalonia.Controls.Button>().Single();
        Assert.Equal(0, cross.MinHeight);
        Assert.Equal(global::Avalonia.Media.Brushes.Transparent, cross.Background);
        window.Close();
    }

    public sealed record Row(string Name, string Group);

    [global::Avalonia.Headless.XUnit.AvaloniaFact]
    public void A_grouped_lists_fold_arrow_keeps_its_size()
    {
        var rows = new global::Avalonia.Collections.DataGridCollectionView(new[] { new Row("kick", "Drums"), new Row("pad", "Synths") });
        rows.GroupDescriptions.Add(new global::Avalonia.Collections.DataGridPathGroupDescription(nameof(Row.Group)));
        var grid = new global::Avalonia.Controls.DataGrid { ItemsSource = rows, Height = 200, AutoGenerateColumns = true };
        var window = Show(grid);

        var arrow = global::Avalonia.VisualTree.VisualExtensions.GetVisualDescendants(grid).OfType<global::Avalonia.Controls.Primitives.ToggleButton>().First();
        Assert.Equal(0, arrow.MinHeight);
        window.Close();
    }
}
