using System.ComponentModel;
using System.Diagnostics;
using MusicOrganiser.Core.Library;

namespace MusicOrganiser.Core.DawLink;

/// <summary>What a DAW shows right now, through its script.</summary>
/// <param name="Daw"><see cref="Bitwig"/> or <see cref="FlStudio"/>, as the library names them.</param>
/// <param name="Answering">Whether its script answered.</param>
/// <param name="Running">Whether the DAW is open: true when it answers, else what this computer shows, or null if the app can't tell.</param>
/// <param name="Version">The DAW and its version, as it says them: "Bitwig Studio 6.1.2".</param>
/// <param name="Project">The open project's name: Bitwig's, or the one in FL Studio's window title ("" for a new, unsaved one).</param>
/// <param name="Title">FL Studio's window title, which is how it names the project.</param>
/// <param name="Modified">Whether the open project has unsaved changes, or null when the script doesn't say (an older copy of it).</param>
/// <param name="Changes">Whether the script lets the app make changes.</param>
/// <param name="Extension">The Bitwig extension's version, when the extension answers rather than the script.</param>
public sealed record DawState(string Daw, bool Answering, bool? Running = null, string Version = "", string Project = "",
                              string Title = "", bool? Modified = null, bool Changes = false, string? Extension = null,
                              string? LoadedFrom = null)
{
    public const string Bitwig = "Bitwig Studio";
    public const string FlStudio = "FL Studio";

    /// <summary>
    /// Whether the DAW has saved its project since <paramref name="before"/>: its unsaved changes cleared, or it shows
    /// another project with none (after Save As, or after opening another project, which a re-read finds unchanged).
    /// </summary>
    public bool SavedSince(DawState? before) =>
        before is { Answering: true } && Answering && Modified == false && Project.Length > 0 &&
        (before.Project != Project || before.Modified == true);

    /// <summary>Whether this library project is the one the DAW has open: the same DAW, and the name it shows.</summary>
    public bool Shows(ProjectSummary project) =>
        Answering && project.Daw == Daw &&
        (Daw == FlStudio
            ? FlCheck.ShowsProject(Title, project.Name)
            : Project.Trim().Equals(project.Name.Trim(), StringComparison.OrdinalIgnoreCase));

    /// <summary>The library project the DAW has open: the newest save with the name it shows, if the library has one.</summary>
    public ProjectSummary? FindIn(LibraryDb db) =>
        !Answering || Project.Length == 0 ? null : db.ProjectsNamed(Daw, Project).Where(Shows).MaxBy(p => p.Mtime);

    /// <summary>The project's name in FL Studio's window title ("Night Drive - FL Studio 2025"), or "" when it names none.</summary>
    public static string FlProjectName(string title)
    {
        static bool IsFl(string part) => part.StartsWith("FL Studio", StringComparison.OrdinalIgnoreCase);
        var parts = title.Replace("*", "").Trim().Split(" - ").Select(p => p.Trim()).ToList();
        if (parts.Count > 1 && IsFl(parts[^1])) parts.RemoveAt(parts.Count - 1);
        else if (parts.Count > 1 && IsFl(parts[0])) parts.RemoveAt(0);
        else if (parts.Count == 1 && IsFl(parts[0])) return "";
        var name = string.Join(" - ", parts).Trim();
        return name.EndsWith(".flp", StringComparison.OrdinalIgnoreCase) ? name[..^4].TrimEnd() : name;
    }
}

/// <summary>
/// Keeps up with Bitwig and FL Studio through their scripts: whether each answers, which project it has open, and
/// whether that has unsaved changes. The app polls it every couple of seconds, and each poll is quick: Bitwig's link
/// stays connected between polls, and FL Studio is only asked while it's open (as far as this computer can tell).
/// A DAW that doesn't answer is left alone for <see cref="Retry"/> before it's asked again.
/// </summary>
public sealed class DawWatch : IDisposable
{
    /// <summary>A DAW that stopped answering is still shown as answering until it misses this many polls in a row.</summary>
    const int Misses = 3;

    readonly Func<bool?> _flOpen;
    readonly Func<bool?> _bitwigOpen;
    BitwigLink? _bitwig;
    DateTime _bitwigRetry, _flRetry;
    int _bitwigMissed, _flMissed;
    DawState _lastBitwig = new(DawState.Bitwig, false);
    DawState _lastFl = new(DawState.FlStudio, false);

    /// <param name="bitwigPort">Where Bitwig's script listens: <see cref="BitwigLink.Port"/> unless given (tests use a stand-in).</param>
    /// <param name="flMidi">The port FL Studio's script talks on: the Music Organiser MIDI port unless given (tests use the script in a stand-in).</param>
    /// <param name="flOpen">Whether FL Studio is open, or null if the app can't tell: <see cref="FlSetup.IsFlStudioOpen"/> unless given.</param>
    /// <param name="bitwigOpen">Whether Bitwig is open, or null if the app can't tell: any program called Bitwig… unless given.</param>
    public DawWatch(int? bitwigPort = null, IFlMidi? flMidi = null, Func<bool?>? flOpen = null, Func<bool?>? bitwigOpen = null)
    {
        BitwigPort = bitwigPort;
        FlMidi = flMidi;
        _flOpen = flOpen ?? FlSetup.IsFlStudioOpen;
        _bitwigOpen = bitwigOpen ?? IsBitwigOpen;
    }

    /// <summary>The port Bitwig's script is asked on, or null for <see cref="BitwigLink.Port"/>.</summary>
    public int? BitwigPort { get; }

    /// <summary>The port FL Studio's script talks on, or null for the Music Organiser MIDI port.</summary>
    public IFlMidi? FlMidi { get; }

    /// <summary>How long a DAW that isn't answering is left before it's asked again.</summary>
    public TimeSpan Retry { get; set; } = TimeSpan.FromSeconds(6);

    /// <summary>A link to FL Studio, through the port this watches.</summary>
    public FlLink Fl() => new(FlMidi);

    /// <summary>A new connection to Bitwig's script, on the port this watches.</summary>
    public Task<BitwigLink> ConnectBitwigAsync(CancellationToken ct = default) => BitwigLink.ConnectAsync(BitwigPort, ct: ct);

    /// <summary>What Bitwig and FL Studio show now, in that order.</summary>
    public async Task<IReadOnlyList<DawState>> PollAsync(CancellationToken ct = default)
    {
        var bitwig = BitwigAsync(ct);
        var fl = FlStudioAsync(ct);
        return [await bitwig, await fl];
    }

    async Task<DawState> BitwigAsync(CancellationToken ct)
    {
        if (_bitwig is { IsOpen: true } link)
        {
            try
            {
                var hello = await link.HelloAsync(ct);
                _bitwigMissed = 0;
                return _lastBitwig = FromHello(hello);
            }
            catch (DawLinkException) when (link.IsOpen && ++_bitwigMissed < Misses)
            {
                return _lastBitwig;   // busy for a moment, e.g. loading a plugin
            }
            catch (DawLinkException) { }
        }
        else if (DateTime.UtcNow >= _bitwigRetry)
        {
            try
            {
                _bitwig?.Dispose();
                _bitwig = null;
                link = await BitwigLink.ConnectAsync(BitwigPort, TimeSpan.FromSeconds(1), ct);
                link.Timeout = TimeSpan.FromSeconds(3);
                _bitwig = link;
                _bitwigMissed = 0;
                return _lastBitwig = FromHello(link.Hello);
            }
            catch (DawLinkException) { }
        }
        else return _lastBitwig;

        _bitwig?.Dispose();
        _bitwig = null;
        _bitwigRetry = DateTime.UtcNow + Retry;
        return _lastBitwig = new(DawState.Bitwig, false, await Task.Run(_bitwigOpen, ct));
    }

    static DawState FromHello(BitwigHello h) =>
        new(DawState.Bitwig, true, true, $"{h.Product} {h.Version}", h.Project.Trim(), h.Project, h.Modified, h.Changes,
            h.Runtime == "java" ? h.Extension : null, h.Runtime == "java" ? h.LoadedFrom : null);

    async Task<DawState> FlStudioAsync(CancellationToken ct)
    {
        // Another screen is asking FL Studio something: there's one request at a time, so leave it be.
        if (FlLink.Busy) return _lastFl;
        if (!_lastFl.Answering && DateTime.UtcNow < _flRetry) return _lastFl;
        bool? open = null;
        var looked = !_lastFl.Answering;
        if (looked) open = await Task.Run(_flOpen, ct);
        if (open != false)
        {
            try
            {
                var hello = await new FlLink(FlMidi) { Timeout = TimeSpan.FromSeconds(1.5), Explain = false }.HelloAsync(ct);
                _flMissed = 0;
                return _lastFl = new(DawState.FlStudio, true, true, $"FL Studio {hello.Version}", DawState.FlProjectName(hello.Title),
                                     hello.Title, hello.Modified, hello.Changes);
            }
            catch (DawLinkException) when (_lastFl.Answering && ++_flMissed < Misses)
            {
                return _lastFl;
            }
            catch (DawLinkException) { }
        }
        _flRetry = DateTime.UtcNow + Retry;
        if (!looked) open = await Task.Run(_flOpen, ct);
        return _lastFl = new(DawState.FlStudio, false, open);
    }

    /// <summary>Whether Bitwig Studio is running: a program whose name starts with Bitwig (its own, its audio engine or plugin host).</summary>
    static bool? IsBitwigOpen()
    {
        Process[] all;
        try { all = Process.GetProcesses(); }
        catch (Exception e) when (e is InvalidOperationException or Win32Exception or PlatformNotSupportedException) { return null; }
        try
        {
            return all.Any(p =>
            {
                try { return p.ProcessName.StartsWith("Bitwig", StringComparison.OrdinalIgnoreCase); }
                catch (InvalidOperationException) { return false; }   // ended while being looked at
            });
        }
        finally
        {
            foreach (var p in all) p.Dispose();
        }
    }

    public void Dispose()
    {
        _bitwig?.Dispose();
        _bitwig = null;
    }
}
