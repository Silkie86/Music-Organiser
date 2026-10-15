using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace MusicOrganiser.Core.DawLink;

/// <summary>What the Bitwig script says when the app connects.</summary>
/// <param name="Engine">Whether Bitwig's audio engine is on.</param>
/// <param name="Changes">Whether "Let the app make changes" is on in the controller's settings in Bitwig.</param>
/// <param name="Runtime">"java" when the Bitwig extension answers (link version 3); null for the script.</param>
/// <param name="Extension">The extension's version, when it's the extension.</param>
/// <param name="OnlyIn">Whether the link honours <see cref="BitwigLink.OnlyIn"/> (older scripts don't).</param>
public sealed record BitwigHello(int Link, string Product, string Version, int Api, string Platform, int Port,
                                 string Project, bool Modified, bool Engine, bool Changes, string? Runtime = null,
                                 string? Extension = null, bool OnlyIn = false, string? LoadedFrom = null)
{
    /// <summary>What answers in Bitwig: the extension or the script.</summary>
    public string Answering => Runtime == "java" ? $"the Music Organiser extension {Extension}".TrimEnd() : "the Music Organiser script";
}

/// <summary>The project open in Bitwig, as Bitwig shows it.</summary>
/// <param name="TrackCount">Every track in the project; <see cref="Tracks"/> holds the first 128.</param>
public sealed record BitwigProject(string Name, bool Modified, double Tempo, string TimeSignature, bool Playing,
                                   double Position, int TrackCount, IReadOnlyList<BitwigTrack> Tracks);

/// <summary>One track in Bitwig's flat track list (tracks inside groups included, then effect tracks and the master).</summary>
/// <param name="DeviceCount">Devices at the top of the track's chain; <see cref="Devices"/> holds the first 16.</param>
public sealed record BitwigTrack(int Index, string Name, string Type, bool Group, string Color, bool Active, bool Mute,
                                 bool Solo, bool Arm, string Volume, double? VolumeValue, string Pan, double? PanValue,
                                 int DeviceCount, IReadOnlyList<BitwigDevice> Devices);

/// <param name="Index">Its place in the track's chain (-1 for the selected device).</param>
/// <param name="Kind">Bitwig's device type, e.g. note_to_audio for an instrument.</param>
public sealed record BitwigDevice(int Index, string Name, string Preset, string Kind, bool Plugin, bool Enabled);

/// <summary>The device selected in Bitwig, with every parameter Bitwig lists for it.</summary>
/// <param name="DrumPads">Whether it has drum pads (a Drum Machine).</param>
public sealed record BitwigSelection(string Track, BitwigDevice? Device, IReadOnlyList<BitwigParam> Params, bool DrumPads = false);

/// <param name="Id">The ID Bitwig keeps the parameter under, e.g. "CONTENTS/PID3e8" for a plugin's parameter 1000.</param>
/// <param name="Value">0 to 1, or null when Bitwig can't read it.</param>
/// <param name="Display">The value as Bitwig shows it ("-6.0 dB"), or null if it didn't say in time.</param>
public sealed record BitwigParam(string Id, string Name, double? Value, string? Display);

public sealed record BitwigAction(string Id, string Name, string Category, string Menu);

/// <summary>What Bitwig added to a track's chain. Added is false when it ignored the plugin, device or file.</summary>
/// <param name="Index">Where the new device is in the chain.</param>
public sealed record BitwigInserted(bool Added, int? Index, BitwigDevice? Device);

/// <summary>A sample for a Drum Machine pad: the MIDI note the pad plays on, and the name to give the pad.</summary>
public sealed record BitwigPad(int Note, string Path, string Name = "");

/// <summary>
/// The pads (by note) that got their sample, that already had something on them, and that Bitwig didn't fill. The
/// link also says what was on each pad left alone (since extension 3.2), and which samples went in place of an empty
/// Sampler a Drum Machine preset put on the pad (since 3.3; they're among the filled).
/// </summary>
public sealed record BitwigPadsFilled(IReadOnlyList<int> Filled, IReadOnlyList<int> Skipped, IReadOnlyList<int> Failed,
                                      IReadOnlyList<BitwigPadContents>? Taken = null, IReadOnlyList<int>? Replaced = null);

/// <summary>What Bitwig showed on a pad: its name, how many devices, the first one's name and its sample's file name.</summary>
public sealed record BitwigPadContents(int Note, string Name, int Devices, string First, string Sample = "");

/// <summary>A pitch Bitwig set: on which device, in semitones, and as Bitwig shows it ("-3.50 st").</summary>
public sealed record BitwigPitch(string Device, double Value, string Display);

/// <summary>
/// Talks to Bitwig Studio through the Music Organiser controller script
/// (extensions/bitwig/MusicOrganiser.control.js), which listens on port 19791 on this computer.
/// Each message is JSON with a 4-byte big-endian length in front.
/// </summary>
public sealed class BitwigLink : IDisposable
{
    public const int DefaultPort = 19791;
    /// <summary>The script version this app talks to. Version 1 scripts can be read but can't take tracks, clips or kits.</summary>
    public const int LinkVersion = 2;
    public const string OldScript = "Bitwig has an older copy of the Music Organiser script. Install it again from Settings > DAW links " +
                                    "(Bitwig picks up the new one by itself), then try again.";
    public const string NotRunning = "Bitwig isn't answering. Check that Bitwig Studio is open, with the Music Organiser " +
                                     "controller added (Settings > Controllers > Add controller > Music Organiser > DAW link).";
    const int MaxMessage = 64 << 20;

    static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    readonly TcpClient _tcp;
    readonly NetworkStream _stream;
    readonly BufferedStream _incoming;
    readonly SemaphoreSlim _writing = new(1, 1);
    readonly ConcurrentDictionary<int, TaskCompletionSource<JsonElement>> _waiting = new();
    readonly TaskCompletionSource<JsonElement> _hello = new(TaskCreationOptions.RunContinuationsAsynchronously);
    readonly CancellationTokenSource _closing = new();
    volatile bool _open = true;
    int _lastId;

    /// <summary>How long to wait for each answer. Reading a big plugin's parameters can take a couple of seconds.</summary>
    public TimeSpan Timeout { get; set; } = TimeSpan.FromSeconds(10);

    /// <summary>
    /// How long to wait for a change that Bitwig makes and the script waits to see: a new track, a device, a
    /// clip, a kit. The script gives up on each step after 5 seconds, and a kit has a few.
    /// </summary>
    public TimeSpan ChangeTimeout { get; set; } = TimeSpan.FromSeconds(30);

    /// <summary>The port the app connects on when not told one: the script's, or a stand-in Bitwig's in tests.</summary>
    public static int Port { get; set; } = DefaultPort;

    /// <summary>What Bitwig said when the link connected.</summary>
    public BitwigHello Hello { get; private set; } = null!;

    /// <summary>The same as it came, with any fields newer than <see cref="BitwigHello"/> (the live tests look at those).</summary>
    public JsonElement HelloJson { get; private set; }

    public bool IsOpen => _open;

    /// <summary>
    /// Asked just before each request that changes or selects something in Bitwig's project (the changes, which need "Let
    /// the app make changes", and select); it throws to stop the request. The live tests use it to check, right before each
    /// one, that Bitwig still has their test project open.
    /// </summary>
    public Func<CancellationToken, Task>? BeforeChange { get; set; }

    /// <summary>
    /// A project's name, or null: when set, each request that changes or selects something carries it ("onlyIn"), and the
    /// link in Bitwig refuses the request, or stops a change part way, unless that project is the one open. Bitwig makes
    /// some changes in steps, and only the link sees each step, so this is what keeps a change in the project it was meant
    /// for when another one comes to the front meanwhile (<see cref="BitwigHello.OnlyIn"/> says whether the link does it).
    /// </summary>
    public string? OnlyIn { get; set; }

    /// <summary>The commands that change or select something in the project, as the script and the extension list them.</summary>
    static readonly HashSet<string> ProjectCommands = ["set", "insert", "newtrack", "clip", "pads", "transpose", "action", "recorder", "select"];

    BitwigLink(TcpClient tcp)
    {
        _tcp = tcp;
        _stream = tcp.GetStream();
        _incoming = new BufferedStream(_stream, 1 << 16);
        _ = Task.Run(ReadAllAsync);
    }

    /// <summary>Connects to the Bitwig script and waits for its greeting.</summary>
    public static async Task<BitwigLink> ConnectAsync(int? port = null, TimeSpan? timeout = null, CancellationToken ct = default)
    {
        var at = port ?? Port;
        var limit = timeout ?? TimeSpan.FromSeconds(3);
        TcpClient? tcp = null;
        Exception? failure = null;
        foreach (var address in new[] { IPAddress.Loopback, IPAddress.IPv6Loopback })
        {
            TcpClient? attempt = null;
            try
            {
                attempt = new TcpClient(address.AddressFamily) { NoDelay = true };
                using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                cts.CancelAfter(limit);
                await attempt.ConnectAsync(address, at, cts.Token);
                tcp = attempt;
                break;
            }
            catch (Exception e) when (e is SocketException || (e is OperationCanceledException && !ct.IsCancellationRequested))
            {
                attempt?.Dispose();
                failure = e;
            }
        }
        ct.ThrowIfCancellationRequested();
        if (tcp is null) throw new DawLinkException(NotRunning, failure);

        var link = new BitwigLink(tcp);
        try
        {
            // The script greets every connection, which also shows it's the Music Organiser script listening.
            link.Greeted(await link._hello.Task.WaitAsync(limit, ct));
            return link;
        }
        catch (TimeoutException)
        {
            link.Dispose();
            throw new DawLinkException($"Something answered on port {at}, but not the Music Organiser script in Bitwig.");
        }
        catch
        {
            link.Dispose();
            throw;
        }
    }

    public void Dispose()
    {
        _open = false;
        _closing.Cancel();
        _tcp.Dispose();
    }

    public async Task<BitwigHello> HelloAsync(CancellationToken ct = default)
    {
        Greeted(await RequestAsync(new() { ["cmd"] = "hello" }, ct));
        return Hello;
    }

    void Greeted(JsonElement hello)
    {
        Hello = Parse<BitwigHello>(hello);
        HelloJson = hello;
    }

    public async Task<BitwigProject> ProjectAsync(CancellationToken ct = default) =>
        Parse<BitwigProject>(await RequestAsync(new() { ["cmd"] = "project" }, ct));

    /// <summary>The device selected in Bitwig with all its parameters (Device is null if none is selected).</summary>
    public async Task<BitwigSelection> SelectedDeviceAsync(CancellationToken ct = default) =>
        Parse<BitwigSelection>(await RequestAsync(new() { ["cmd"] = "device" }, ct));

    /// <summary>Every action Bitwig offers (its menu commands and shortcuts), to run with <see cref="RunActionAsync"/>.</summary>
    public async Task<IReadOnlyList<BitwigAction>> ActionsAsync(CancellationToken ct = default) =>
        Parse<List<BitwigAction>>(await RequestAsync(new() { ["cmd"] = "actions" }, ct));

    /// <summary>Selects a track (its index in <see cref="BitwigProject.Tracks"/>) and, optionally, one of its devices.</summary>
    public Task SelectAsync(int track, int? device = null, CancellationToken ct = default) =>
        RequestAsync(new() { ["cmd"] = "select", ["track"] = track, ["device"] = device }, ct);

    // Changes: these need "Let the app make changes" turned on in Bitwig.

    /// <summary>Sets one of the selected device's parameters (by its Bitwig ID) to a value from 0 to 1.</summary>
    public Task SetParameterAsync(string param, double value, CancellationToken ct = default) =>
        RequestAsync(new() { ["cmd"] = "set", ["param"] = param, ["value"] = value }, ct);

    /// <summary>
    /// Adds a plugin at the end of the selected track's chain, by the ID the plugin scanner reads: a VST3
    /// class ID, a CLAP ID or a VST2 ID (four letters or a number). Bitwig ignores IDs it doesn't know.
    /// </summary>
    public Task InsertPluginAsync(string format, string id, CancellationToken ct = default) =>
        RequestAsync(new() { ["cmd"] = "insert", ["kind"] = format.ToLowerInvariant(), ["plugin"] = PluginId(format, id) }, ct);

    /// <summary>Adds a VST2 plugin by its ID as a number (the scanner's uniqueId), which is never ambiguous.</summary>
    public Task InsertVst2Async(int uniqueId, CancellationToken ct = default) =>
        RequestAsync(new() { ["cmd"] = "insert", ["kind"] = "vst2", ["plugin"] = uniqueId }, ct);

    /// <summary>Adds one of Bitwig's own devices at the end of the selected track's chain.</summary>
    public Task InsertBitwigDeviceAsync(Guid id, CancellationToken ct = default) =>
        RequestAsync(new() { ["cmd"] = "insert", ["kind"] = "bitwig", ["device"] = id.ToString() }, ct);

    /// <summary>Adds a preset or a sample at the end of the selected track's chain.</summary>
    public Task InsertFileAsync(string path, CancellationToken ct = default) =>
        RequestAsync(new() { ["cmd"] = "insert", ["kind"] = "file", ["path"] = path }, ct);

    /// <summary>Runs one of Bitwig's actions by its ID, e.g. to save the project.</summary>
    public Task RunActionAsync(string id, CancellationToken ct = default) =>
        RequestAsync(new() { ["cmd"] = "action", ["action"] = id }, ct);

    /// <summary>
    /// Sends any command, with <paramref name="fields"/> added to it, and returns Bitwig's answer as it came. For commands
    /// and fields newer than this app's records, such as the Java extension's selftest, which the live tests look at.
    /// </summary>
    public Task<JsonElement> CommandAsync(string cmd, JsonObject? fields = null, CancellationToken ct = default)
    {
        var request = fields?.DeepClone().AsObject() ?? new JsonObject();
        request["cmd"] = cmd;
        return RequestAsync(request, ct);
    }

    // Sending things in (link version 2): these wait for Bitwig to make each change.

    /// <summary>
    /// Adds an instrument track after the project's last track (before the effect tracks), names it and selects
    /// it. Returns its index in <see cref="BitwigProject.Tracks"/>, which the methods below take.
    /// </summary>
    /// <param name="color">"#rrggbb", or null for Bitwig's choice.</param>
    public async Task<int> NewTrackAsync(string name, string? color = null, CancellationToken ct = default) =>
        Parse<NewTrack>(await ChangeAsync(new() { ["cmd"] = "newtrack", ["name"] = name, ["color"] = color }, ct)).Index;

    /// <summary>
    /// Adds a file to a track's chain, as if it were dropped there: a preset (.bwpreset, .vstpreset...) loads its
    /// device, and a sample loads into a new Sampler.
    /// </summary>
    public Task<BitwigInserted> InsertFileAsync(int track, string path, bool atStart = false, CancellationToken ct = default) =>
        InsertOnAsync(track, atStart, new() { ["kind"] = "file", ["path"] = path }, ct);

    /// <summary>Adds one of Bitwig's own devices to a track's chain, by the ID Bitwig saves it under.</summary>
    public Task<BitwigInserted> InsertBitwigDeviceAsync(int track, Guid id, bool atStart = false, CancellationToken ct = default) =>
        InsertOnAsync(track, atStart, new() { ["kind"] = "bitwig", ["device"] = id.ToString() }, ct);

    /// <summary>Adds a plugin to a track's chain by the ID the plugin scanner reads (see <see cref="InsertPluginAsync(string, string, CancellationToken)"/>).</summary>
    public Task<BitwigInserted> InsertPluginAsync(int track, string format, string id, bool atStart = false, CancellationToken ct = default) =>
        InsertOnAsync(track, atStart, new() { ["kind"] = format.ToLowerInvariant(), ["plugin"] = PluginId(format, id) }, ct);

    /// <summary>Adds a VST2 plugin to a track's chain by its ID as a number.</summary>
    public Task<BitwigInserted> InsertVst2Async(int track, int uniqueId, bool atStart = false, CancellationToken ct = default) =>
        InsertOnAsync(track, atStart, new() { ["kind"] = "vst2", ["plugin"] = uniqueId }, ct);

    /// <summary>Puts a MIDI file in a track's clip launcher slot (the first empty one unless given), and returns the slot.</summary>
    public async Task<int> AddClipAsync(int track, string path, int? slot = null, CancellationToken ct = default) =>
        Parse<AddedClip>(await ChangeAsync(new() { ["cmd"] = "clip", ["track"] = track, ["path"] = path, ["slot"] = slot }, ct)).Slot;

    /// <summary>
    /// Puts samples on a Drum Machine's pads, leaving pads that already have something on them alone. The Drum
    /// Machine is <paramref name="device"/> on <paramref name="track"/> (which this selects), or else the device
    /// selected in Bitwig.
    /// </summary>
    public async Task<BitwigPadsFilled> FillPadsAsync(IReadOnlyList<BitwigPad> pads, int? track = null, int? device = null,
                                                      CancellationToken ct = default)
    {
        var list = new JsonArray();
        foreach (var p in pads) list.Add(new JsonObject { ["note"] = p.Note, ["path"] = p.Path, ["name"] = p.Name.Length > 0 ? p.Name : null });
        return Parse<BitwigPadsFilled>(await ChangeAsync(
            new() { ["cmd"] = "pads", ["track"] = track, ["device"] = device, ["pads"] = list }, ct));
    }

    /// <summary>
    /// Sets the pitch of a Sampler (its Transpose, which can be a fraction of a semitone) or a Note Transpose (whole
    /// semitones), one of the first two devices on a track. Returns what Bitwig set, which a device's range may limit.
    /// </summary>
    public async Task<BitwigPitch> TransposeAsync(int track, int device, double semitones, CancellationToken ct = default) =>
        Parse<BitwigPitch>(await ChangeAsync(
            new() { ["cmd"] = "transpose", ["track"] = track, ["device"] = device, ["semitones"] = semitones }, ct));

    async Task<BitwigInserted> InsertOnAsync(int track, bool atStart, JsonObject request, CancellationToken ct)
    {
        request["cmd"] = "insert";
        request["track"] = track;
        if (atStart) request["at"] = "start";
        return Parse<BitwigInserted>(await ChangeAsync(request, ct));
    }

    Task<JsonElement> ChangeAsync(JsonObject request, CancellationToken ct)
    {
        if (Hello.Link < LinkVersion) throw new DawLinkException(OldScript);
        return RequestAsync(request, ct, ChangeTimeout);
    }

    static JsonNode PluginId(string format, string id) => format.ToUpperInvariant() switch
    {
        "VST3" or "CLAP" => id,
        "VST2" => Vst2Id(id) ?? throw new ArgumentException($"Not a VST2 plugin ID: {id}", nameof(id)),
        _ => throw new ArgumentException($"Bitwig can't insert {format} plugins by ID.", nameof(format)),
    };

    sealed record NewTrack(int Index, string Name);
    sealed record AddedClip(int Slot);

    /// <summary>
    /// A VST2 ID as a number, read the way the plugin scanner writes it: four printable characters are the
    /// ID's own letters ("MoB2"), and anything else is the number itself ("-1294"). A number such as 1234
    /// reads as the letters "1234", so where the scanner's uniqueId is to hand, use that instead.
    /// </summary>
    public static int? Vst2Id(string id)
    {
        if (id.Length == 4 && id.All(c => c is >= ' ' and <= '~')) return BinaryPrimitives.ReadInt32BigEndian(Encoding.ASCII.GetBytes(id));
        return int.TryParse(id, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var n) ? n : null;
    }

    static T Parse<T>(JsonElement result)
    {
        try
        {
            return result.Deserialize<T>(Json) ?? throw new DawLinkException("Bitwig's answer was empty.");
        }
        catch (JsonException e)
        {
            throw new DawLinkException("Bitwig's answer wasn't what the app expected. Install the script again from Settings, " +
                                       "in case Bitwig has an older version.", e);
        }
    }

    async Task<JsonElement> RequestAsync(JsonObject request, CancellationToken ct, TimeSpan? timeout = null)
    {
        if (request["cmd"] is JsonValue cmd && cmd.TryGetValue<string>(out var name) && ProjectCommands.Contains(name))
        {
            if (BeforeChange is { } before) await before(ct);
            if (OnlyIn is { } project) request["onlyIn"] = project;
        }
        var id = Interlocked.Increment(ref _lastId);
        request["id"] = id;
        var answer = new TaskCompletionSource<JsonElement>(TaskCreationOptions.RunContinuationsAsynchronously);
        _waiting[id] = answer;
        try
        {
            if (!_open) throw new DawLinkException("The connection to Bitwig is closed.");
            await SendAsync(Encoding.UTF8.GetBytes(request.ToJsonString()), ct);
            return await answer.Task.WaitAsync(timeout ?? Timeout, ct);
        }
        catch (TimeoutException)
        {
            throw new DawLinkException("Bitwig didn't answer in time.");
        }
        catch (Exception e) when (e is IOException or ObjectDisposedException)
        {
            throw new DawLinkException("Lost the connection to Bitwig.", e);
        }
        finally
        {
            _waiting.TryRemove(id, out _);
        }
    }

    async Task SendAsync(byte[] json, CancellationToken ct)
    {
        var frame = new byte[4 + json.Length];
        BinaryPrimitives.WriteInt32BigEndian(frame, json.Length);
        json.CopyTo(frame, 4);
        await _writing.WaitAsync(ct);
        try { await _stream.WriteAsync(frame, ct); }
        finally { _writing.Release(); }
    }

    async Task ReadAllAsync()
    {
        Exception? failure = null;
        try
        {
            while (await ReadMessageAsync(_closing.Token) is { } message)
                Dispatch(message);
        }
        catch (Exception e) when (e is IOException or ObjectDisposedException or OperationCanceledException or InvalidDataException or SocketException)
        {
            failure = e;
        }
        _open = false;
        var closed = new DawLinkException("Bitwig closed the connection.", failure);
        _hello.TrySetException(closed);
        foreach (var id in _waiting.Keys)
            if (_waiting.TryRemove(id, out var waiting)) waiting.TrySetException(closed);
    }

    /// <summary>
    /// One message from Bitwig. Bitwig puts a 4-byte big-endian length in front of each; the script also
    /// ends each with a newline, so a message that arrives without the length still reads.
    /// </summary>
    async Task<byte[]?> ReadMessageAsync(CancellationToken ct)
    {
        var first = new byte[1];
        if (await _incoming.ReadAsync(first, ct) == 0) return null;
        if (first[0] == (byte)'{')
        {
            var line = new MemoryStream();
            line.WriteByte(first[0]);
            while (true)
            {
                // Buffered, so most of these return straight away.
                if (await _incoming.ReadAsync(first, ct) == 0) return null;
                if (first[0] == '\n') return line.ToArray();
                line.WriteByte(first[0]);
                if (line.Length > MaxMessage) throw new InvalidDataException("Bitwig sent a message that never ends.");
            }
        }
        var header = new byte[4];
        header[0] = first[0];
        await _incoming.ReadExactlyAsync(header.AsMemory(1), ct);
        var length = BinaryPrimitives.ReadInt32BigEndian(header);
        if (length is < 0 or > MaxMessage) throw new InvalidDataException($"Bitwig sent a message of {length} bytes.");
        var body = new byte[length];
        await _incoming.ReadExactlyAsync(body, ct);
        return body;
    }

    void Dispatch(byte[] message)
    {
        // Something we can't use: the request it answers will time out.
        try { DispatchJson(message); }
        catch (Exception e) when (e is JsonException or DawLinkException or InvalidOperationException) { }
    }

    void DispatchJson(byte[] message)
    {
        using (var doc = JsonDocument.Parse(message))
        {
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object) return;
            if (root.TryGetProperty("event", out var ev) && ev.ValueKind == JsonValueKind.String)
            {
                if (ev.GetString() == "hello" && root.TryGetProperty("result", out var hello))
                    _hello.TrySetResult(hello.Clone());
                return;
            }
            if (!root.TryGetProperty("id", out var idElement) || !idElement.TryGetInt32(out var id)) return;
            if (!_waiting.TryRemove(id, out var waiting)) return;
            if (root.TryGetProperty("ok", out var ok) && ok.ValueKind == JsonValueKind.True)
                waiting.TrySetResult(root.TryGetProperty("result", out var result) ? result.Clone() : default);
            else
                waiting.TrySetException(new DawLinkException(
                    root.TryGetProperty("error", out var error) && error.GetString() is { Length: > 0 } text ? text : "Bitwig said no."));
        }
    }
}
