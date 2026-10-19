using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace MusicOrganiser.Core.DawLink;

/// <summary>What the FL Studio script says about itself and FL Studio.</summary>
/// <param name="Version">FL Studio's version and edition, e.g. "Producer Edition v25.1.6 [build 4997]".</param>
/// <param name="Api">FL Studio's scripting version, which decides what the script can ask for.</param>
/// <param name="Title">FL Studio's window title, which names the open project.</param>
/// <param name="Changes">Whether LET_THE_APP_MAKE_CHANGES is on in the installed script.</param>
/// <param name="Script">The folder FL Studio loaded the script from, if it could tell.</param>
/// <param name="Modified">Whether the open project has unsaved changes, or null from an older copy of the script, which doesn't say.</param>
public sealed record FlHello(int Link, string Product, string Version, int Api, string Title, bool Changes, string Platform,
                             string? Script, bool? Modified = null);

/// <summary>The project open in FL Studio, as FL Studio shows it.</summary>
/// <param name="Tempo">In BPM, or null if FL Studio didn't say.</param>
/// <param name="Grouped">True when FL Studio only listed the channels in the group the channel rack shows (before scripting version 33).</param>
/// <param name="Mixer">Every mixer track, the master first.</param>
public sealed record FlProject(string Title, double? Tempo, bool Modified, bool Grouped,
                               IReadOnlyList<FlChannel> Channels, IReadOnlyList<FlMixerTrack> Mixer);

/// <param name="Index">The channel's place in the whole channel rack, from 0.</param>
/// <param name="Type">FL Studio's channel type: 0 sampler, 2 plugin, 3 layer, 4 audio clip, 5 automation clip.</param>
/// <param name="Mixer">The mixer track the channel plays into.</param>
/// <param name="Plugin">The channel's plugin, or null for a sampler, layer or clip.</param>
/// <param name="Params">How many parameters the plugin lists (4240 for any VST plugin).</param>
public sealed record FlChannel(int Index, string Name, int Type, int Mixer, string? Plugin, int Params);

public sealed record FlMixerTrack(int Index, string Name, IReadOnlyList<FlSlot> Plugins);

/// <param name="Name">The plugin's own name.</param>
/// <param name="Label">The name the slot shows, which you can change in FL Studio.</param>
public sealed record FlSlot(int Slot, string Name, string Label, int Params);

/// <param name="Value">0 to 1, or null if FL Studio couldn't read it.</param>
/// <param name="Text">The value as the plugin shows it ("440 Hz"). FL Studio's own plugins say; others may not.</param>
public sealed record FlParam(int Index, string Name, double? Value, string Text);

/// <summary>A plugin in FL Studio: on a channel (by its place in the whole channel rack), or in a mixer track's slot.</summary>
public sealed record FlPlugin(int? Channel = null, int? Track = null, int? Slot = null)
{
    public static FlPlugin OnChannel(int channel) => new(Channel: channel);
    public static FlPlugin OnMixer(int track, int slot) => new(Track: track, Slot: slot);
}

/// <summary>
/// Talks to FL Studio through the Music Organiser script (extensions/fl-studio/device_MusicOrganiser.py), in SysEx
/// messages on the Music Organiser MIDI port (<see cref="FlMessages"/>): FL Studio doesn't let scripts open files or
/// connect to anything, but they can send and receive MIDI. The app sends a request into the port, and the script
/// answers through FL Studio's Music Organiser MIDI output.
/// </summary>
public sealed class FlLink
{
    public const string Script = "device_MusicOrganiser.py";
    public const string NotRunning = "FL Studio isn't answering. Check that FL Studio is open, that the Music Organiser script " +
                                     "is installed (Settings), that it's the controller type of the Music Organiser MIDI input in " +
                                     "FL Studio (Options > MIDI settings), and that the Music Organiser output there has the same " +
                                     "Port number as the input.";
    public const string OnlyWindows = "The app can only reach FL Studio on Windows so far: it talks to FL Studio's script over MIDI, " +
                                      "which it doesn't do on this system yet.";
    public const string PartAnswer = "FL Studio started answering, but not all of its answer came through. Try again.";
    static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    // One request at a time from the whole app: the script answers one at a time, and drops what's left of an
    // answer when another request comes in.
    static readonly SemaphoreSlim One = new(1, 1);
    // Starts somewhere new each time, so an answer left over from an earlier run is never taken for this one's.
    static int _lastId = Random.Shared.Next(FlMessages.Ids);

    readonly IFlMidi? _midi;

    /// <param name="midi">The port to talk through: the Music Organiser MIDI port unless given (the tests use the
    /// script itself, in a stand-in FL Studio).</param>
    public FlLink(IFlMidi? midi = null) => _midi = midi;

    /// <summary>How long to wait for FL Studio's answer, from the request or the last part of the answer to come in.</summary>
    public TimeSpan Timeout { get; set; } = TimeSpan.FromSeconds(5);

    /// <summary>
    /// Whether to work out why FL Studio isn't answering when it doesn't (from its program and this computer's MIDI
    /// inputs), for the message. Off for quick checks that only need to know whether it answers.
    /// </summary>
    public bool Explain { get; init; } = true;

    /// <summary>Whether a request is waiting for FL Studio's answer, from anywhere in the app.</summary>
    public static bool Busy => One.CurrentCount == 0;

    public async Task<FlHello> HelloAsync(CancellationToken ct = default) =>
        Parse<FlHello>(await RequestAsync(new() { ["cmd"] = "hello" }, ct));

    public async Task<FlProject> ProjectAsync(CancellationToken ct = default) =>
        Parse<FlProject>(await RequestAsync(new() { ["cmd"] = "project" }, ct));

    /// <summary>
    /// Every named parameter of a plugin, with its value. A VST plugin's MIDI CC and aftertouch parameters
    /// (which FL Studio adds to every VST) are left out unless <paramref name="midi"/> is set.
    /// </summary>
    public async Task<IReadOnlyList<FlParam>> ParamsAsync(FlPlugin plugin, bool midi = false, CancellationToken ct = default)
    {
        var all = new List<FlParam>();
        for (int? from = 0; from is { } start;)
        {
            var request = Target(plugin, "params");
            request["from"] = start;
            if (midi) request["midi"] = true;
            var page = Parse<ParamPage>(await RequestAsync(request, ct));
            all.AddRange(page.Params);
            from = page.Next;
            if (from <= start) throw new DawLinkException("FL Studio's list of parameters didn't move on.");
        }
        return all;
    }

    /// <summary>
    /// Sets one of a plugin's parameters to a value from 0 to 1, and returns what it became. This needs
    /// LET_THE_APP_MAKE_CHANGES turned on in the script.
    /// </summary>
    public async Task<FlParam> SetParameterAsync(FlPlugin plugin, int param, double value, CancellationToken ct = default)
    {
        var request = Target(plugin, "set");
        request["param"] = param;
        request["value"] = value;
        return Parse<FlParam>(await RequestAsync(request, ct));
    }

    static JsonObject Target(FlPlugin plugin, string cmd) => plugin switch
    {
        { Channel: { } c } => new() { ["cmd"] = cmd, ["channel"] = c },
        { Track: { } t, Slot: { } s } => new() { ["cmd"] = cmd, ["track"] = t, ["slot"] = s },
        _ => throw new ArgumentException("Name a channel, or a mixer track and slot.", nameof(plugin)),
    };

    static T Parse<T>(JsonElement result)
    {
        try
        {
            return result.Deserialize<T>(Json) ?? throw new DawLinkException("FL Studio's answer was empty.");
        }
        catch (JsonException e)
        {
            throw new DawLinkException("FL Studio's answer wasn't what the app expected. Install the script again from Settings, " +
                                       "in case FL Studio has an older version.", e);
        }
    }

    async Task<JsonElement> RequestAsync(JsonObject request, CancellationToken ct)
    {
        await One.WaitAsync(ct);
        try
        {
            // Off the caller's thread: after a send times out (FL Studio busy loading a project) the port is closed and
            // opened again, which waits on Windows' MIDI driver for seconds, and the caller is often the window's own thread.
            var midi = _midi ?? await Task.Run(Port, ct) ?? throw new DawLinkException(Explain ? FlSetup.WhyNotAnswering() : NotRunning);
            var id = Interlocked.Increment(ref _lastId) & (FlMessages.Ids - 1);
            request["id"] = id;
            request["sent"] = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() / 1000.0;
            var waiting = new Waiting(id);
            midi.Received += waiting.Take;
            try
            {
                // Default JSON escapes every letter outside plain ASCII, which is all the messages carry.
                var messages = FlMessages.Write(FlMessages.Request, id, request.ToJsonString());
                await Task.Run(() =>
                {
                    foreach (var m in messages) midi.Send(m);
                }, ct);
                while (waiting.Left(Timeout) is { } left)
                {
                    // Once cancelled, the delay finishes at once, so without this the loop would spin until the timeout.
                    ct.ThrowIfCancellationRequested();
                    if (await Task.WhenAny(waiting.Done, Task.Delay(left, ct)) == waiting.Done) return Result(Parse(await waiting.Done));
                }
                ct.ThrowIfCancellationRequested();
                throw new DawLinkException(waiting.Started ? PartAnswer : Explain ? FlSetup.WhyNotAnswering() : NotRunning);
            }
            finally
            {
                midi.Received -= waiting.Take;
            }
        }
        finally
        {
            One.Release();
        }
    }

    /// <summary>The Music Organiser MIDI port, or null when there isn't one.</summary>
    static IFlMidi? Port() =>
        OperatingSystem.IsWindows() ? FlMidi.Shared() : throw new DawLinkException(OnlyWindows);

    static JsonElement Parse(string text)
    {
        try
        {
            using var doc = JsonDocument.Parse(text);
            return doc.RootElement.Clone();
        }
        catch (JsonException e) { throw new DawLinkException("FL Studio's answer wasn't readable.", e); }
    }

    static JsonElement Result(JsonElement reply)
    {
        if (reply.TryGetProperty("ok", out var ok) && ok.ValueKind == JsonValueKind.True && reply.TryGetProperty("result", out var result))
            return result;
        throw new DawLinkException(reply.TryGetProperty("error", out var error) && error.ValueKind == JsonValueKind.String
            ? error.GetString()! : "FL Studio's answer didn't say what happened.");
    }

    /// <summary>One request's answer coming in, in parts, on the port's own thread.</summary>
    sealed class Waiting(int id)
    {
        readonly FlAnswer _answer = new(id);
        readonly TaskCompletionSource<string> _done = new(TaskCreationOptions.RunContinuationsAsynchronously);
        long _heard = Stopwatch.GetTimestamp();

        public Task<string> Done => _done.Task;

        public bool Started
        {
            get { lock (_answer) return _answer.Started; }
        }

        public void Take(byte[] message)
        {
            if (FlMessages.Read(message) is not { Kind: FlMessages.Answer } m || m.Id != id) return;
            lock (_answer)
            {
                Volatile.Write(ref _heard, Stopwatch.GetTimestamp());
                if (_answer.Take(m) is { } text) _done.TrySetResult(text);
            }
        }

        /// <summary>How much longer to wait: the timeout runs from the request or the last part of the answer.</summary>
        public TimeSpan? Left(TimeSpan timeout) =>
            timeout - Stopwatch.GetElapsedTime(Volatile.Read(ref _heard)) is { } left && left > TimeSpan.Zero ? left : null;
    }

    sealed record ParamPage(string Plugin, int Count, IReadOnlyList<FlParam> Params, int? Next);
}
