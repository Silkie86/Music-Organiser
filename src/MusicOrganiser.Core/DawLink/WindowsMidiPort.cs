using System.ComponentModel;
using System.Diagnostics;
using System.Text;
using System.Text.Json;

namespace MusicOrganiser.Core.DawLink;

/// <summary>What the app found, or did, about the port.</summary>
/// <param name="HasPort">Whether there's a Music Organiser port now.</param>
/// <param name="Message">What to tell the user, in whole sentences.</param>
/// <param name="Step">How far it got, for the tests: there, made, ready, no-api, too-old, legacy, no-loopback, exists, failed…</param>
public sealed record WindowsMidiResult(bool HasPort, string Message, string Step);

/// <summary>
/// Makes the MIDI port FL Studio runs the script for with Windows' own MIDI system (Windows MIDI Services), so there's
/// nothing else to install: a loopback pair whose first end is called Music Organiser, saved so Windows makes it again
/// after a restart. Apps can only do that through the system's own API, which Windows 11 25H2 gets with an update
/// expected late in November 2026 (24H2 never gets it). The app reaches it through Windows PowerShell, which finds
/// Windows' own API by name, so nothing extra is needed to build or run the app, and it says plainly when the API isn't there.
/// </summary>
public static class WindowsMidiPort
{
    /// <summary>Windows 11 25H2. Microsoft only brings the API to 25H2 and later.</summary>
    public const int FirstBuild = 26200;
    /// <summary>The pair's other end, which the app talks through. Named so it isn't taken for the port FL Studio wants.</summary>
    public const string SpareEnd = "Organiser link (spare end)";

    public const string OnlyWindows = "Windows' MIDI system is only on Windows.";
    public const string TooOld = "The app can only make the port with Windows' MIDI system on Windows 11 version 25H2 or later. " +
                                 "Choose loopMIDI instead, or update Windows.";
    public const string NoApi = "Windows' MIDI system isn't open to apps on this PC yet, so the app can't make the port with it. " +
                                "That comes with a Windows 11 25H2 update, expected late in November 2026. Until then, choose loopMIDI.";
    public const string Legacy = "Windows' new MIDI system is switched off on this PC, so the app can't make the port with it. " +
                                 "Choose loopMIDI instead.";
    public const string NoLoopback = "Windows' MIDI system can't make ports like this on this PC yet: Microsoft is turning that on " +
                                     "gradually. Choose loopMIDI for now.";
    public const string SavedNotShowing = "Windows' MIDI system has a Music Organiser port saved, but it isn't showing. " +
                                          "Restart the PC, then press Check connection.";
    public const string Ready = "This PC can make the port with Windows' MIDI system: press Install.";
    public const string Made = "Made a MIDI port called Music Organiser with Windows' MIDI system. It stays after a restart.";
    public const string TimedOut = "Windows' MIDI system didn't answer in time, so the app couldn't make the port. " +
                                   "Try again, or choose loopMIDI.";
    public const string NoAnswer = "Windows PowerShell, which the app makes the port through, didn't answer.";

    static readonly TimeSpan Timeout = TimeSpan.FromSeconds(60);
    static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    /// <summary>Whether there's a port, or whether the app could make one, without making anything.</summary>
    public static Task<WindowsMidiResult> CheckAsync(CancellationToken ct = default) => RunAsync(make: false, ct);

    /// <summary>Makes the port and saves it, unless there's one already.</summary>
    public static Task<WindowsMidiResult> MakeAsync(CancellationToken ct = default) => RunAsync(make: true, ct);

    static async Task<WindowsMidiResult> RunAsync(bool make, CancellationToken ct)
    {
        if (!OperatingSystem.IsWindows()) return new(false, OnlyWindows, "not-windows");
        // Whoever made it (loopMIDI too), FL Studio can use it.
        if (await Task.Run(FlSetup.MidiInputs, ct) is { } inputs && FlSetup.HasOurPort(inputs))
            return new(true, FlSetup.PortThere, "there");
        if (!OperatingSystem.IsWindowsVersionAtLeast(10, 0, FirstBuild)) return new(false, TooOld, "too-old");
        return await RunScriptAsync(make, ct);
    }

    /// <summary>
    /// Runs <see cref="Script"/> in Windows PowerShell. Without <paramref name="make"/> it stops before changing anything,
    /// which is how the tests run it on a real Windows.
    /// </summary>
    public static async Task<WindowsMidiResult> RunScriptAsync(bool make, CancellationToken ct = default)
    {
        var psi = new ProcessStartInfo(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System),
                                                    "WindowsPowerShell", "v1.0", "powershell.exe"))
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        var script = (make ? "$make = $true" : "$make = $false") + "\n" + Script;
        foreach (var arg in new[] { "-NoLogo", "-NoProfile", "-NonInteractive", "-ExecutionPolicy", "Bypass",
                                    "-EncodedCommand", Convert.ToBase64String(Encoding.Unicode.GetBytes(script)) })
            psi.ArgumentList.Add(arg);

        Process powershell;
        try
        {
            powershell = Process.Start(psi) ?? throw new Win32Exception("It didn't start.");
        }
        catch (Win32Exception e)
        {
            return new(false, $"Couldn't run Windows PowerShell to make the port: {e.Message} Choose loopMIDI instead.", "no-powershell");
        }
        using (powershell)
        {
            var output = powershell.StandardOutput.ReadToEndAsync(CancellationToken.None);
            var errors = powershell.StandardError.ReadToEndAsync(CancellationToken.None);
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(Timeout);
            try
            {
                await powershell.WaitForExitAsync(timeout.Token);
            }
            catch (OperationCanceledException)
            {
                try { powershell.Kill(); }
                catch (Exception e) when (e is InvalidOperationException or Win32Exception) { }
                ct.ThrowIfCancellationRequested();
                return new(false, TimedOut, "timeout");
            }
            return Read(await output, await errors);
        }
    }

    /// <summary>What the script's answer (its last line of JSON) means for the user.</summary>
    public static WindowsMidiResult Read(string output, string errors)
    {
        Answer? answer = null;
        if (output.Split('\n').Select(l => l.Trim()).LastOrDefault(l => l.StartsWith('{')) is { } line)
        {
            try { answer = JsonSerializer.Deserialize<Answer>(line, Json); }
            catch (JsonException) { }
        }
        if (answer?.Step is not { Length: > 0 } step)
        {
            // CLIXML is PowerShell's own bookkeeping, not words.
            var said = errors.Split('\n').Select(l => l.Trim()).FirstOrDefault(l => l.Length > 0 && !l.StartsWith("#<", StringComparison.Ordinal));
            return new(false, $"{NoAnswer}{(said is null ? "" : $" It said: {Sentence(said)}")} Choose loopMIDI instead.", "unreadable");
        }
        var windows = answer.Message?.Trim() is { Length: > 0 } m ? $" Windows said: {Sentence(m)}" : "";
        return step switch
        {
            "made" when answer.Saved => new(true, Made, step),
            "made" => new(true, "Made a MIDI port called Music Organiser with Windows' MIDI system, but Windows couldn't save it, " +
                                $"so it goes when the PC restarts, and Install has to make it again.{windows}", step),
            "ready" => new(false, Ready, step),
            "no-api" => new(false, NoApi, step),
            "legacy" => new(false, Legacy, step),
            "no-loopback" => new(false, NoLoopback, step),
            "exists" => new(false, SavedNotShowing, step),
            "failed" => new(false, $"Windows' MIDI system couldn't make the port.{windows} Choose loopMIDI instead.", step),
            _ => new(false, $"Something went wrong making the port with Windows' MIDI system.{windows} Choose loopMIDI instead.", "error"),
        };
    }

    static string Sentence(string text) => text.EndsWith('.') || text.EndsWith('!') || text.EndsWith('?') ? text : text + ".";

    sealed record Answer(string? Step, bool Saved, string? Message);

    /// <summary>
    /// The PowerShell that talks to Windows' MIDI system (Windows.Devices.Midi2), as Microsoft documents it: the API
    /// must be there and the service running (not the older MIDI system), with loopback ports on. Then, unless a
    /// Music Organiser pair is saved already, it makes one and saves it. It answers with one line of JSON.
    /// </summary>
    public const string Script = $$"""
        $ErrorActionPreference = 'Stop'
        $ProgressPreference = 'SilentlyContinue'
        $port = '{{FlSetup.PortName}}'
        $spare = '{{SpareEnd}}'

        # One line of JSON, in plain ASCII whatever the console's code page.
        function Say($answer) {
            $json = ConvertTo-Json -InputObject $answer -Compress
            [regex]::Replace($json, '[^\x20-\x7e]', { param($m) '\u{0:x4}' -f [int][char]$m.Value })
        }

        # A type from Windows' own API, or nothing if this Windows doesn't have it.
        function Find($name) {
            foreach ($file in 'Windows.Devices.Midi2', 'Windows.Devices', 'Windows') {
                try { $type = [Type]::GetType("$name, $file, ContentType=WindowsRuntime") } catch { $type = $null }
                if ($type) { return $type }
            }
            $null
        }

        function Run {
            $api = Find 'Windows.Devices.Midi2.MidiApi'
            $loopbacks = Find 'Windows.Devices.Midi2.Transports.Loopback.MidiLoopbackManager'
            $definition = Find 'Windows.Devices.Midi2.Transports.Loopback.MidiLoopbackEndpointDefinition'
            $creation = Find 'Windows.Devices.Midi2.Transports.Loopback.MidiLoopbackCreationConfig'
            $config = Find 'Windows.Devices.Midi2.ServiceConfig.MidiServiceTransportPluginConfigManager'
            if (-not ($api -and $loopbacks -and $definition -and $creation -and $config)) { return @{ step = 'no-api' } }
            if (-not $api::EnsureServiceAvailable()) { return @{ step = 'legacy' } }
            if (-not $loopbacks::IsTransportAvailable) { return @{ step = 'no-loopback' } }
            try { $saved = @($loopbacks::GetSavedLoopbackEntries()) } catch { $saved = @() }
            if ($saved | Where-Object { $_.EndpointDefinitionA.Name -eq $port }) { return @{ step = 'exists' } }
            if (-not $make) { return @{ step = 'ready' } }

            $a = $definition::new()
            $a.Name = $port
            $a.UniqueId = 'MusicOrganiserLinkA'
            $a.Description = 'For the Music Organiser script in FL Studio'
            $b = $definition::new()
            $b.Name = $spare
            $b.UniqueId = 'MusicOrganiserLinkB'
            $b.Description = 'The other end of the Music Organiser port, for the Music Organiser app'
            $pair = $creation::new($a, $b)
            $made = $loopbacks::CreateTransientLoopback($pair)
            if (-not $made.Success) { return @{ step = 'failed'; message = [string]$made.ErrorMessage } }
            # Saved, Windows makes it again each time it starts.
            $save = $config::SaveUpdate($pair)
            @{ step = 'made'; saved = [bool]$save.Success; message = [string]$save.ErrorMessage }
        }

        try { Say (Run | Select-Object -Last 1) }
        catch {
            # Windows' own words, not PowerShell's wrapping ("Exception calling ... with 1 argument(s)").
            $e = $_.Exception
            while ($e.InnerException) { $e = $e.InnerException }
            Say @{ step = 'error'; message = [string]$e.Message }
        }
        """;
}
