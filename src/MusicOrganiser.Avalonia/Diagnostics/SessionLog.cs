using System.Globalization;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;
using Avalonia.Logging;
using MusicOrganiser.Core.LiveTests;

namespace MusicOrganiser.Avalonia.Diagnostics;

/// <summary>
/// A plain-text diary of each run of the app, in logs\ in the app's data folder: which build it is, the pages
/// opened, and every warning and error, with the details. It's there so Claude can read what happened on
/// Solomon's computer straight from the folder, instead of Solomon copying it out. Nothing of the music is in it,
/// only what the app did. The newest <see cref="Kept"/> runs are kept.
/// </summary>
public sealed class SessionLog : IDisposable
{
    /// <summary>How many runs' logs are kept.</summary>
    public const int Kept = 30;

    /// <summary>A run that writes this much stops writing (and says so), so a runaway warning can't fill the disk.</summary>
    const long MaxBytes = 5 * 1024 * 1024;

    /// <summary>The same message more often than this is counted, not written again.</summary>
    const int RepeatsWritten = 3;

    /// <summary>The log of this run of the app, once <see cref="Start"/> has made it.</summary>
    public static SessionLog? Current { get; private set; }

    /// <summary>logs\ in the app's data folder.</summary>
    public static string DefaultFolder => System.IO.Path.Combine(LiveReports.DataFolder, "logs");

    readonly object _gate = new();
    readonly StreamWriter? _writer;
    readonly Dictionary<string, int> _seen = [];
    readonly Queue<string> _recent = new();
    bool _full;

    /// <summary>This run's file.</summary>
    public string Path { get; }

    public SessionLog(string folder, DateTime? now = null)
    {
        var start = now ?? DateTime.Now;
        Directory.CreateDirectory(folder);
        Path = System.IO.Path.Combine(folder, $"session-{start:yyyy-MM-dd_HH-mm-ss}.log");
        // Two runs started in the same second (tests, or a quick restart) each get their own file.
        for (var n = 2; File.Exists(Path); n++) Path = System.IO.Path.Combine(folder, $"session-{start:yyyy-MM-dd_HH-mm-ss}-{n}.log");
        try
        {
            _writer = new StreamWriter(new FileStream(Path, FileMode.CreateNew, FileAccess.Write, FileShare.ReadWrite | FileShare.Delete),
                                       new UTF8Encoding(false)) { AutoFlush = true };
        }
        catch (IOException) { }   // no log is better than no app
        catch (UnauthorizedAccessException) { }
        Prune(folder);
        Write("Info", "App", Header(start));
    }

    /// <summary>Makes this run's log and catches errors that would otherwise vanish with the app.</summary>
    public static SessionLog Start(string? folder = null)
    {
        Current?.Dispose();
        Current = new SessionLog(folder ?? DefaultFolder);
        AppDomain.CurrentDomain.UnhandledException += (_, e) =>
            Current?.Error("App", "The app stopped because of an error", e.ExceptionObject as Exception);
        TaskScheduler.UnobservedTaskException += (_, e) => Current?.Error("App", "A background job failed", e.Exception);
        return Current;
    }

    static string Header(DateTime start)
    {
        var debug =
#if DEBUG
            true;
#else
            false;
#endif
        var hotReload =
#if HOTAVALONIA_ENABLE
            "on (screens change as their .axaml files are saved)";
#else
            "off";
#endif
        return $"""
                Music Organiser started {start:yyyy-MM-dd HH:mm:ss} ({TimeZoneInfo.Local.StandardName})
                  Version:    {LiveReports.AppVersion}
                  Build:      {(debug ? "Debug" : "Release")}, screen hot reload {hotReload}
                  Watched by: {(Environment.GetEnvironmentVariable("DOTNET_WATCH") == "1" ? "dotnet watch (C# changes apply while it runs)" : "nothing")}
                  System:     {RuntimeInformation.OSDescription} {RuntimeInformation.OSArchitecture}, .NET {Environment.Version}
                  Folder:     {AppContext.BaseDirectory}
                """;
    }

    public void Info(string area, string message) => Write("Info", area, message);

    public void Warning(string area, string message) => Write("Warning", area, message);

    public void Error(string area, string message, Exception? error = null) =>
        Write("Error", area, error is null ? message : $"{message}: {error}");

    /// <summary>The last lines written, newest last, for a note to Claude.</summary>
    public IReadOnlyList<string> Recent()
    {
        lock (_gate) return [.. _recent];
    }

    void Write(string level, string area, string message)
    {
        lock (_gate)
        {
            // A warning that keeps coming (a binding each time a row draws) is written a few times, then counted.
            var key = level + area + message;
            var times = _seen[key] = _seen.GetValueOrDefault(key) + 1;
            if (times > RepeatsWritten)
            {
                if (IsPowerOfTen(times)) message = $"(said {times} times now) {FirstLine(message)}";
                else return;
            }
            // Avalonia pads some messages with spaces; they only make the log wide.
            message = Regex.Replace(message, @"[ \t]+(?=\r?\n|$)", "");
            var line = $"{DateTime.Now:HH:mm:ss.fff} {level,-7} [{area}] {message}";
            _recent.Enqueue(line);
            while (_recent.Count > 60) _recent.Dequeue();
            if (_writer is null || _full) return;
            try
            {
                _writer.WriteLine(line);
                if (_writer.BaseStream.Length > MaxBytes)
                {
                    _writer.WriteLine("The log is full, so nothing more is written for this run.");
                    _full = true;
                }
            }
            catch (IOException) { }
            catch (ObjectDisposedException) { }
        }
    }

    static bool IsPowerOfTen(int n)
    {
        while (n >= 10 && n % 10 == 0) n /= 10;
        return n == 1;
    }

    static string FirstLine(string s) => s.Split('\n')[0].TrimEnd('\r');

    static void Prune(string folder)
    {
        try
        {
            foreach (var old in new DirectoryInfo(folder).GetFiles("session-*.log")
                         .OrderByDescending(f => f.Name, StringComparer.Ordinal).Skip(Kept))
            {
                try { old.Delete(); } catch (IOException) { } catch (UnauthorizedAccessException) { }
            }
        }
        catch (IOException) { }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_writer is null) return;
            try { _writer.WriteLine($"{DateTime.Now:HH:mm:ss.fff} Info    [App] Closed"); } catch (IOException) { } catch (ObjectDisposedException) { }
            _writer.Dispose();
        }
    }

    /// <summary>
    /// Avalonia's own warnings and errors (a binding that can't find its property, a style that doesn't apply), into
    /// the log as well as wherever they went before.
    /// </summary>
    public sealed class Sink(SessionLog log, ILogSink? next) : ILogSink
    {
        public bool IsEnabled(LogEventLevel level, string area) =>
            level >= LogEventLevel.Warning || next?.IsEnabled(level, area) == true;

        public void Log(LogEventLevel level, string area, object? source, string messageTemplate) =>
            Log(level, area, source, messageTemplate, []);

        public void Log(LogEventLevel level, string area, object? source, string messageTemplate, params object?[] propertyValues)
        {
            if (next?.IsEnabled(level, area) == true) next.Log(level, area, source, messageTemplate, propertyValues);
            if (level < LogEventLevel.Warning) return;
            var text = Fill(messageTemplate, propertyValues);
            if (source is not null) text += $" (in {source.GetType().Name})";
            log.Write(level >= LogEventLevel.Error ? "Error" : "Warning", area, text);
        }

        /// <summary>Puts the values into Avalonia's message: "Could not find {Property} on {Type}" and so on.</summary>
        internal static string Fill(string template, object?[] values)
        {
            var i = 0;
            return Regex.Replace(template, @"\{[^{}]+\}", m =>
                i < values.Length ? Convert.ToString(values[i++], CultureInfo.InvariantCulture) ?? "null" : m.Value);
        }
    }
}
