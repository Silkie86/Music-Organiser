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
public sealed record FlHello(int Link, string Product, string Version, int Api, string Title, bool Changes, string Platform,
                             string? Script);

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
/// Talks to FL Studio through the Music Organiser script (extensions/fl-studio/device_MusicOrganiser.py), with
/// files in a folder of the app's: the app writes request.json, and the script answers in response-&lt;id&gt;.json.
/// The script looks for a request five times a second.
/// </summary>
public sealed class FlLink
{
    public const string Script = "device_MusicOrganiser.py";
    public const string NotRunning = "FL Studio isn't answering. Check that FL Studio is open, that the Music Organiser script " +
                                     "is installed (Settings), and that it's the controller type of a MIDI input in FL Studio " +
                                     "(Options > MIDI settings).";
    static readonly TimeSpan Poll = TimeSpan.FromMilliseconds(25);
    static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    // One request at a time from the whole app: there's only one request file, whichever screen is asking.
    static readonly SemaphoreSlim One = new(1, 1);
    // Starts somewhere new each time, so an answer left over from an earlier run is never taken for this one's.
    int _lastId = Random.Shared.Next(1, 1 << 30);

    /// <param name="folder">Where requests and answers go: <see cref="DefaultFolder"/>, which the script also uses.</param>
    public FlLink(string? folder = null) => Folder = folder ?? DefaultFolder;

    /// <summary>
    /// The link's folder in the app's data folder (AppData\Local on Windows, Library/Application Support on a Mac):
    /// somewhere the app and the script both know, wherever FL Studio keeps its own files.
    /// </summary>
    public static string DefaultFolder => Path.Combine(OperatingSystem.IsMacOS()
        ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Library", "Application Support")
        : Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "MusicOrganiser", "FL Studio link");

    public string Folder { get; }
    public TimeSpan Timeout { get; set; } = TimeSpan.FromSeconds(5);

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
            Directory.CreateDirectory(Folder);
            var id = ++_lastId;
            request["id"] = id;
            request["sent"] = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() / 1000.0;
            ClearAnswers();

            // Written aside and moved into place, so the script never reads half a request.
            var asked = Path.Combine(Folder, "request.json");
            var temp = Path.Combine(Folder, "request.tmp");
            await File.WriteAllTextAsync(temp, request.ToJsonString(), ct);
            File.Move(temp, asked, overwrite: true);

            var answer = Path.Combine(Folder, $"response-{id}.json");
            var waited = Stopwatch.StartNew();
            try
            {
                while (true)
                {
                    if (File.Exists(answer) && await ReadAsync(answer, ct) is { } reply) return Result(reply);
                    if (waited.Elapsed >= Timeout) break;
                    await Task.Delay(Poll, ct);
                }
            }
            catch (OperationCanceledException)
            {
                TryDelete(asked);
                throw;
            }
            if (!File.Exists(asked)) throw new DawLinkException("FL Studio took the request but didn't answer in time.");
            TryDelete(asked);
            throw new DawLinkException(NotRunning);
        }
        finally
        {
            One.Release();
        }
    }

    static async Task<JsonElement?> ReadAsync(string path, CancellationToken ct)
    {
        string text;
        try
        {
            text = await File.ReadAllTextAsync(path, ct);
        }
        catch (IOException) { return null; }   // still being moved into place; read it next time
        TryDelete(path);
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

    /// <summary>Removes answers no one is waiting for any more (to requests that took too long).</summary>
    void ClearAnswers()
    {
        foreach (var old in Directory.EnumerateFiles(Folder, "response-*")) TryDelete(old);
    }

    static void TryDelete(string path)
    {
        try { File.Delete(path); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
    }

    sealed record ParamPage(string Plugin, int Count, IReadOnlyList<FlParam> Params, int? Next);
}
