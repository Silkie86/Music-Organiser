using System.Text.Json;
using System.Text.Json.Nodes;
using MusicOrganiser.Core.DawLink;
using MusicOrganiser.Core.LiveTests;
using static MusicOrganiser.Core.LiveTests.BitwigLiveTests;

namespace MusicOrganiser.Core.Tests;

/// <summary>
/// The live tests that record a launcher clip into Bitwig's arranger (the Java extension 3.9), and the panel probe, against
/// the stand-in extension (<see cref="FakeJavaBitwig"/>): what the app asks the record job, what it makes of the answers,
/// and how it stops a job that runs over. Nothing here reaches a real Bitwig.
/// </summary>
public class BitwigRecordTests
{
    const string Hello39 =
        """{"link":3,"product":"Bitwig Studio","version":"6.1.3","api":23,"platform":"WINDOWS","port":19791,"project":"MO Live Test","modified":false,"engine":true,"changes":true,"runtime":"java","extension":"3.9","onlyIn":true}""";

    /// <summary>The test project in 4/4 at 120 BPM, with the run's Rec track last but the master.</summary>
    static string Project(string timeSignature = "4/4", double tempo = 120) =>
        $$"""{"name":"MO Live Test","modified":true,"tempo":{{tempo}},"timeSignature":"{{timeSignature}}","playing":false,"position":0,"trackCount":3,"tracks":[""" +
        string.Join(",", new[] { ("Bass", "7f2c"), ("Rec 143012", "rec-1"), ("Master", "7f2e") }.Select((t, i) =>
            $$"""{"index":{{i}},"id":"{{t.Item2}}","name":"{{t.Item1}}","type":"Instrument","group":false,"color":"#3366cc","active":true,"mute":false,"solo":false,"arm":false,"volume":"0.0 dB","volumeValue":0.79,"pan":"C","panValue":0.5,"deviceCount":0,"devices":[]}""")) +
        "]}";

    const string Notes = "[{'key':57,'at':16.02},{'key':60,'at':17.52},{'key':64,'at':19.02},{'key':62,'at':20.02},{'key':65,'at':21.52},{'key':69,'at':23.02}]";

    /// <summary>What the record job answers when it recorded the clip at bar 5 and heard it in the replay, as the extension 3.9 does.</summary>
    static readonly string Recorded = (
        "{'pass':'Record','decided':'recorded','trackId':'rec-1','track':'Rec 143012','slot':0,'at':16,'length':8,'bar':4,'record':true,'arm':false," +
        "'tempo':{'project':120,'used':120},'refused':[]," +
        "'played':{'from':16,'started':true,'startedAfterMs':25,'ended':'done','positionFrom':'bitwig','heard':6,'notes':" + Notes + "}," +
        "'heard':6,'seen':{'clipPlayed':true,'slotRecording':false,'automationWriteSeenOn':false,'stopSentAt':20.02},'launcherClipDeleted':null," +
        "'replay':{'from':16,'started':true,'startedAfterMs':25,'ended':'done','positionFrom':'bitwig','heard':6,'notes':" + Notes +
        ",'to':28,'inPass':6,'before':0,'launcherPlayed':false}," +
        "'after':{'checked':true,'recordOff':true,'playing':false,'playStart':0,'restoreFailed':[]}," +
        "'diff':{'tracksAdded':[],'tracksRemoved':[],'clipsChanged':[]},'ms':10250}").Replace('\'', '"');

    static readonly JsonSerializerOptions Web = new(JsonSerializerDefaults.Web);

    /// <summary>The recorded answer with some of its fields changed.</summary>
    static BitwigRecorded Answer(Action<JsonObject>? change = null)
    {
        var a = JsonNode.Parse(Recorded)!.AsObject();
        change?.Invoke(a);
        return a.Deserialize<BitwigRecorded>(Web)!;
    }

    /// <summary>A stand-in extension 3.9 whose record job answers <paramref name="decide"/>'s answer to each request (the recorded one unless it gives another).</summary>
    static FakeJavaBitwig Stand(Func<JsonNode, string?>? decide = null, string timeSignature = "4/4", double tempo = 120)
    {
        var java = new FakeJavaBitwig(Hello39, new Dictionary<string, string>
        {
            ["project"] = Project(timeSignature, tempo),
            ["newtrack"] = """{"index":1,"name":"Rec 143012","id":"rec-1"}""",
            ["insert"] = """{"added":true,"index":0,"device":{"index":0,"name":"Sampler","preset":"","kind":"note_to_audio","plugin":false,"enabled":true}}""",
            ["clip"] = """{"slot":0}""",
            ["recordabort"] = """{"running":true}""",
        });
        java.Answer = request => (string)request["cmd"]! == "record" ? decide?.Invoke(request) ?? Recorded : null;
        return java;
    }

    static List<JsonNode> Sent(FakeJavaBitwig java, string cmd) => java.Requests.Where(r => (string)r["cmd"]! == cmd).ToList();

    [Fact]
    public async Task MakesATrackToRecordOnFoundByItsId()
    {
        await using var java = Stand();
        using var link = await BitwigLink.ConnectAsync(java.Port);

        var (verdict, id) = await RecordTrackAsync(link, "Rec 143012", Path.Combine("files", "Rec Clip.mid"));

        Assert.Equal("rec-1", id);
        Assert.Equal((LiveOutcome.Passed, "Bitwig added “Rec 143012” (ID rec-1) with an empty Sampler, an instrument that makes no sound, and put " +
                                          "Rec Clip.mid, two bars of six notes, in its launcher slot 1. The next tests play Bitwig for a minute or so " +
                                          "with its master muted: keep your hands off Bitwig until they're done."),
                     (verdict.Outcome, verdict.Detail));
        // The Sampler by Bitwig's ID on the new track, then the clip on the track found by its ID, in its first slot.
        var insert = Sent(java, "insert").Single();
        Assert.Equal(("bitwig", BitwigSend.Sampler.ToString(), 1), ((string)insert["kind"]!, (string)insert["device"]!, (int)insert["track"]!));
        var clip = Sent(java, "clip").Single();
        Assert.Equal(("rec-1", 0, Path.Combine("files", "Rec Clip.mid")), ((string)clip["trackId"]!, (int)clip["slot"]!, (string)clip["path"]!));
        Assert.Null(clip["track"]);

        // A link that doesn't say a new track's ID: the listing does.
        await using var listed = Stand();
        listed.Answer = request => (string)request["cmd"]! == "newtrack" ? """{"index":1,"name":"Rec 143012"}""" : null;
        using var listedLink = await BitwigLink.ConnectAsync(listed.Port);
        Assert.Equal("rec-1", (await RecordTrackAsync(listedLink, "Rec 143012", "Rec Clip.mid")).Id);

        // Neither does: Failed, and no clip goes anywhere.
        await using var unknown = Stand();
        unknown.Answer = request => (string)request["cmd"]! switch
        {
            "newtrack" => """{"index":1,"name":"Rec 143012"}""",
            "project" => Project().Replace("\"id\":\"rec-1\",", ""),
            _ => null,
        };
        using var unknownLink = await BitwigLink.ConnectAsync(unknown.Port);
        var (failed, none) = await RecordTrackAsync(unknownLink, "Rec 143012", "Rec Clip.mid");
        Assert.Null(none);
        Assert.Equal((LiveOutcome.Failed, "Bitwig added “Rec 143012” (track 2), but didn't say its ID, which the recording tests find it by."),
                     (failed.Outcome, failed.Detail));
        Assert.Empty(Sent(unknown, "clip"));
    }

    [Theory]
    // 4/4 at 120 BPM: a two-bar clip at bar 5, again at bar 9 armed, and at bar 13 at double the tempo, replayed from bar 5.
    [InlineData(RecordGo.Hear, "4/4", 120, 16, 8, false, false, false, null, null, 43)]
    [InlineData(RecordGo.Record, "4/4", 120, 16, 8, true, false, false, null, null, 49)]
    [InlineData(RecordGo.Armed, "4/4", 120, 32, 8, true, true, false, null, null, 49)]
    [InlineData(RecordGo.Fast, "4/4", 120, 48, 8, true, false, true, 16.0, 240.0, 50)]
    // 7/8: bars of 3.5 beats, so the clip's 8 beats are recorded as 3 bars, the goes 5 bars apart.
    [InlineData(RecordGo.Armed, "7/8", 124, 31.5, 10.5, true, true, false, null, null, 35 + 31.5 * 60 / 124.0)]
    // The tempo goes no faster than Bitwig's 666 BPM.
    [InlineData(RecordGo.Fast, "4/4", 400, 48, 8, true, false, true, 16.0, 666.0, 35 + 60 * 60 / 666.0)]
    public void PlansEachGo(RecordGo go, string timeSignature, double tempo, double at, double length, bool record, bool arm, bool delete,
                            double? replayFrom, double? recordTempo, double seconds)
    {
        var project = JsonSerializer.Deserialize<BitwigProject>(Project(timeSignature, tempo), Web)!;
        var (request, timeout) = RecordRequest(go, project, "rec-1", RecordClipBeats);
        Assert.Equal(new BitwigRecordRequest("rec-1", 0, at, length)
                     {
                         Record = record, Arm = arm, DeleteLauncherClip = delete, ReplayFrom = replayFrom, Tempo = recordTempo, Pass = go.ToString(),
                     }, request);
        Assert.Equal(seconds, timeout.TotalSeconds, 3);
    }

    [Fact]
    public async Task RecordsAtBar5ThenFastAtBar13()
    {
        await using var java = Stand(request =>
        {
            var a = JsonNode.Parse(Recorded)!.AsObject();
            a["at"] = (double)request["at"]!;
            if ((bool)request["record"]! == false)
            {
                a["decided"] = "heard";
                a["record"] = false;
                a["replay"] = null;
            }
            if (request["tempo"] is { } tempo) a["tempo"] = new JsonObject { ["project"] = 120, ["used"] = (double)tempo };
            if ((bool)request["deleteLauncherClip"]!)
            {
                a["launcherClipDeleted"] = true;
                a["replay"]!["before"] = 6;
                a["diff"]!["clipsChanged"] = JsonNode.Parse("""[{"trackId":"rec-1","slot":0,"before":true,"after":false}]""");
            }
            return a.ToJsonString();
        });
        using var link = await BitwigLink.ConnectAsync(java.Port);
        link.OnlyIn = "MO Live Test";
        var takes = new Dictionary<RecordGo, BitwigRecorded>();
        var verdicts = new Dictionary<RecordGo, LiveVerdict>();
        foreach (var go in Enum.GetValues<RecordGo>())
        {
            var (verdict, answer) = await RecordGoAsync(link, go, "Rec 143012", "rec-1", takes);
            verdicts[go] = verdict;
            if (answer is not null) takes[go] = answer;
        }

        // Heard, recorded at bar 5, no armed go needed, then recorded fast at bar 13 with the launcher clip deleted.
        var sent = Sent(java, "record");
        Assert.Equal([(16.0, false, false, false), (16.0, true, false, false), (48.0, true, false, true)],
                     sent.Select(r => ((double)r["at"]!, (bool)r["record"]!, (bool)r["arm"]!, (bool)r["deleteLauncherClip"]!)));
        Assert.All(sent, r => Assert.Equal(("rec-1", 0, 8.0, "MO Live Test"), ((string)r["trackId"]!, (int)r["slot"]!, (double)r["length"]!, (string)r["onlyIn"]!)));
        Assert.Equal((240.0, 16.0), ((double)sent[2]["tempo"]!, (double)sent[2]["replayFrom"]!));
        Assert.Null(sent[1]["tempo"]);

        Assert.Equal((LiveOutcome.Passed, "With the master muted, Bitwig played the clip on “Rec 143012” and the link heard 6 notes of it, so it can hear " +
                                          "whether a recording plays back. Nothing was recorded."),
                     (verdicts[RecordGo.Hear].Outcome, verdicts[RecordGo.Hear].Detail));
        Assert.Equal((LiveOutcome.Passed, "Bitwig recorded the clip into its arranger on “Rec 143012” at bars 5 to 6: playing the arranger back, the link " +
                                          "heard 6 notes there, with the launcher clip not playing. The link can't see an arranger clip itself: look at bars " +
                                          "5 to 6 on “Rec 143012” in Bitwig's arranger and say whether it's there."),
                     (verdicts[RecordGo.Record].Outcome, verdicts[RecordGo.Record].Detail));
        Assert.Equal((LiveOutcome.Skipped, "Only tried when Bitwig records nothing with the track unarmed, and Record into the arranger found otherwise (see above)."),
                     (verdicts[RecordGo.Armed].Outcome, verdicts[RecordGo.Armed].Detail));
        var fast = verdicts[RecordGo.Fast];
        Assert.Equal(LiveOutcome.Passed, fast.Outcome);
        Assert.StartsWith("Bitwig recorded the clip into its arranger on “Rec 143012” at bars 13 to 14 at 240 BPM (the project's is 120): playing the " +
                          "arranger back, the link heard 6 notes there, with the launcher clip not playing. The link deleted the launcher clip before the " +
                          "replay, and still heard 6 notes from the earlier recording at bars 5 to 6, so what's recorded in the arranger doesn't need the " +
                          "launcher clip.", fast.Detail);
        Assert.Equal("- The clip played from bar 5, starting 25 ms after the link asked: the link heard 6 notes: A2 at 5.1.1, C3 at 5.2.3, E3 at 5.4.1, " +
                     "D3 at 6.1.1, F3 at 6.2.3, A3 at 6.4.1.", verdicts[RecordGo.Record].Lines[0]);
        Assert.Equal("- The replay from bar 5 to bar 8, starting 25 ms after the link asked: the link heard 6 notes: A2 at 5.1.1, C3 at 5.2.3, E3 " +
                     "at 5.4.1, D3 at 6.1.1, F3 at 6.2.3, A3 at 6.4.1 (6 in the recording's bars, 0 before them).", verdicts[RecordGo.Record].Lines[1]);
        Assert.Equal("recorded", (string?)verdicts[RecordGo.Record].Data!["decided"]);
    }

    [Fact]
    public async Task TriesArmedWhenUnarmedRecordsNothing()
    {
        await using var java = Stand(request =>
        {
            var a = JsonNode.Parse(Recorded)!.AsObject();
            a["at"] = (double)request["at"]!;
            a["arm"] = (bool)request["arm"]!;
            if (!(bool)request["arm"]! && (bool)request["record"]!)
            {
                a["decided"] = "nothing";
                a["replay"]!["inPass"] = 0;
            }
            return a.ToJsonString();
        });
        using var link = await BitwigLink.ConnectAsync(java.Port);
        var takes = new Dictionary<RecordGo, BitwigRecorded>();
        var verdicts = new Dictionary<RecordGo, LiveVerdict>();
        foreach (var go in new[] { RecordGo.Record, RecordGo.Armed, RecordGo.Fast })
        {
            var (verdict, answer) = await RecordGoAsync(link, go, "Rec 143012", "rec-1", takes);
            verdicts[go] = verdict;
            if (answer is not null) takes[go] = answer;
        }

        // Unarmed and nothing in: a finding, as the app records armed.
        Assert.Equal((LiveOutcome.Noted, "Bitwig played the clip on “Rec 143012” with arranger record on (the link heard 6 notes), but playing the " +
                                         "arranger back it heard none of them at bars 5 to 6, so nothing went in the arranger. With the track unarmed, " +
                                         "that's what Bitwig may do: the next go tries with it armed, as the app records. Look at bars 5 to 6 on " +
                                         "“Rec 143012” in Bitwig's arranger and say whether the clip is there."),
                     (verdicts[RecordGo.Record].Outcome, verdicts[RecordGo.Record].Detail));
        Assert.StartsWith("Bitwig recorded the clip into its arranger on “Rec 143012” at bars 9 to 10, with the track armed:", verdicts[RecordGo.Armed].Detail);
        // The fast go arms the track as well, since only the armed go recorded.
        Assert.Equal([(16.0, false), (32.0, true), (48.0, true)],
                     Sent(java, "record").Select(r => ((double)r["at"]!, (bool)r["arm"]!)));
        Assert.Equal(LiveOutcome.Passed, verdicts[RecordGo.Fast].Outcome);

        // Nothing either way: the fast go isn't tried.
        var none = new Dictionary<RecordGo, BitwigRecorded>
        {
            [RecordGo.Record] = Answer(a => a["decided"] = "nothing"),
            [RecordGo.Armed] = Answer(a => a["decided"] = "nothing"),
        };
        var skipped = (await RecordGoAsync(link, RecordGo.Fast, "Rec 143012", "rec-1", none)).Verdict;
        Assert.Equal((LiveOutcome.Skipped, "Only tried once an earlier go recorded the clip into the arranger, or the link couldn't tell, and none did (see above)."),
                     (skipped.Outcome, skipped.Detail));
        // Without the first go at all, the armed one says what it needs.
        Assert.Equal("This needs a go at recording unarmed first, which didn't run (see above).",
                     (await RecordGoAsync(link, RecordGo.Armed, "Rec 143012", "rec-1", new Dictionary<RecordGo, BitwigRecorded>())).Verdict.Detail);
        Assert.Equal(3, Sent(java, "record").Count);
    }

    [Fact]
    public async Task NeedsTheExtension39()
    {
        foreach (var (hello, skipped) in new[]
                 {
                     (Hello39.Replace("\"3.9\"", "\"3.8\""), "This needs the Music Organiser extension 3.9 or later, and Bitwig runs 3.8. " + Reinstall),
                     (FakeJavaBitwig.Hello("""{"link":2,"runtime":null,"extension":null}"""), NeedsJava),
                 })
        {
            await using var older = new FakeJavaBitwig(hello, new Dictionary<string, string> { ["project"] = Project() });
            using var link = await BitwigLink.ConnectAsync(older.Port);
            foreach (var go in Enum.GetValues<RecordGo>())
                Assert.Equal((LiveOutcome.Skipped, skipped), ((await RecordGoAsync(link, go, "Rec", "rec-1", new Dictionary<RecordGo, BitwigRecorded>())) is var (v, _) ? (v.Outcome, v.Detail) : default));
            Assert.Equal((LiveOutcome.Skipped, skipped), (await RecordTrackAsync(link, "Rec", "Rec Clip.mid")) is var (t, _) ? (t.Outcome, t.Detail) : default);
            var probe = await PanelCommandsAsync(link);
            Assert.Equal((LiveOutcome.Skipped, skipped), (probe.Outcome, probe.Detail));
            Assert.DoesNotContain(older.Requests, r => (string)r["cmd"]! is "record" or "probe" or "newtrack");
        }

        // The extension 3.9 records the clip into the arranger, so the copy-and-paste experiment and the MIDI file between tracks aren't tried.
        await using var java = Stand();
        using var newer = await BitwigLink.ConnectAsync(java.Port);
        var arrange = await ClipToArrangerAsync(newer, "Sample 143012", 0, () => true);
        Assert.Equal((LiveOutcome.Skipped, RecordsInstead), (arrange.Outcome, arrange.Detail));
        var file = await FileAsTrackAsync(newer, "Sample 143012", "Live Clip.mid");
        Assert.Equal((LiveOutcome.Skipped, FileMadeNothing), (file.Outcome, file.Detail));
        Assert.DoesNotContain(java.Requests, r => (string)r["cmd"]! is "arrange" or "filetrack");
    }

    [Fact]
    public async Task AsksTheJobToStopWhenItRunsOver()
    {
        // The stand-in's job answers late (as one stopped by recordabort would): the app asks it to stop once its time is up.
        await using var java = Stand(request =>
        {
            Thread.Sleep(1500);
            return JsonNode.Parse(Recorded)!.AsObject() is var a && (a["decided"] = "aborted") is not null ? a.ToJsonString() : null;
        });
        using var link = await BitwigLink.ConnectAsync(java.Port);
        link.OnlyIn = "MO Live Test";
        var answer = await link.RecordAsync(new BitwigRecordRequest("rec-1", 0, 16, 8), TimeSpan.FromMilliseconds(300));
        Assert.Equal("aborted", answer.Decided);
        var abort = Sent(java, "recordabort").Single();
        // It reaches the job whatever project is in front, so it doesn't carry the test project's name.
        Assert.Null(abort["onlyIn"]);
        Assert.Equal("MO Live Test", (string?)Sent(java, "record").Single()["onlyIn"]);

        // Stopped (the run, or its time for the test): the same, then it stops.
        using var stop = new CancellationTokenSource(TimeSpan.FromMilliseconds(300));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => link.RecordAsync(new BitwigRecordRequest("rec-1", 0, 16, 8), TimeSpan.FromMinutes(1), stop.Token));
        for (var waited = 0; Sent(java, "recordabort").Count < 2 && waited < 5000; waited += 20) await Task.Delay(20);
        Assert.Equal(2, Sent(java, "recordabort").Count);

        // In time: no recordabort.
        java.Answer = request => (string)request["cmd"]! == "record" ? Recorded : null;
        Assert.Equal("recorded", (await link.RecordAsync(new BitwigRecordRequest("rec-1", 0, 16, 8), TimeSpan.FromSeconds(10))).Decided);
        Assert.Equal(2, Sent(java, "recordabort").Count);
        Assert.True(await link.AbortRecordingAsync());
    }

    [Fact]
    public void SaysWhatTheRecordingFound()
    {
        LiveVerdict Verdict(Action<JsonObject> change, RecordGo go = RecordGo.Record) => RecordVerdict(Answer(change), go, "Rec 143012");
        const string look = "Look at bars 5 to 6 on “Rec 143012” in Bitwig's arranger and say whether the clip is there.";

        var deaf = Verdict(a =>
        {
            a["decided"] = "cantTell";
            a["played"]!["heard"] = 0;
            a["played"]!["notes"] = new JsonArray();
            a["heard"] = 0;
        });
        Assert.Equal((LiveOutcome.Noted, "Bitwig played the clip on “Rec 143012” with arranger record on, but the link heard no notes, as it played or " +
                                         "in the replay, so only a look tells. " + look),
                     (deaf.Outcome, deaf.Detail));
        Assert.Equal("- The clip played from bar 5, starting 25 ms after the link asked: the link heard no notes.", deaf.Lines[0]);
        var cantHear = Verdict(a =>
        {
            a["decided"] = "cantHear";
            a["record"] = false;
            a["replay"] = null;
        }, RecordGo.Hear);
        Assert.Equal((LiveOutcome.Noted, "Bitwig played the clip on “Rec 143012”, but the link heard none of its notes, so it can't hear whether a " +
                                         "recording plays back: the recording tests ask you to look instead. Nothing was recorded."),
                     (cantHear.Outcome, cantHear.Detail));
        Assert.Single(cantHear.Lines);

        var refused = Verdict(a =>
        {
            a["decided"] = "refused";
            a["refused"] = new JsonArray("pre-roll", "master mute");
            a["played"] = null;
            a["replay"] = null;
        });
        Assert.Equal((LiveOutcome.Failed, "The link didn't record, as Bitwig didn't take pre-roll and master mute as the recording needs them (so only " +
                                          "the clip records, and nothing is heard). Everything was put back."),
                     (refused.Outcome, refused.Detail));
        Assert.Empty(refused.Lines);
        Assert.Equal((LiveOutcome.Failed, "Bitwig didn't start playing when the link launched the clip (the link waited 3 seconds), so nothing was " +
                                          "recorded. Everything was put back."),
                     Verdict(a => a["decided"] = "didntStart") is var d ? (d.Outcome, d.Detail) : default);
        Assert.Equal("The recording stopped part way: The track went away. The link turned record off and put everything back. " + look,
                     Verdict(a =>
                     {
                         a["decided"] = "aborted";
                         a["abortWhy"] = "The track went away.";
                     }).Detail);
        Assert.Equal("The extension stopped with an error: java.lang.IllegalStateException: no. It turned record off, stopped Bitwig and put things " +
                     "back as well as it could: check that arranger record is off and Bitwig has stopped.",
                     Verdict(a =>
                     {
                         a["decided"] = "failed";
                         a["error"] = "java.lang.IllegalStateException: no";
                         a["after"]!["restoreFailed"] = null;
                     }).Detail);
        // Left alone in another project: what to do goes in the lines too, which the run keeps when it says it stopped part way.
        var abandoned = Verdict(a =>
        {
            a["decided"] = "abandoned";
            a["after"] = JsonNode.Parse("""{"checked":false,"restoreFailed":null}""");
            a["diff"] = null;
        });
        Assert.Equal(LiveOutcome.Failed, abandoned.Outcome);
        Assert.Equal("- " + abandoned.Detail, abandoned.Lines[0]);
        Assert.StartsWith("Another project came to the front while Bitwig recorded, so the link left Bitwig alone", abandoned.Detail);

        // Recorded, but something else changed or wasn't put back: Failed all the same.
        var messy = Verdict(a =>
        {
            a["after"]!["recordOff"] = false;
            a["after"]!["restoreFailed"] = new JsonArray("arranger record", "play start");
            a["diff"] = JsonNode.Parse("""{"tracksAdded":[{"id":"x","name":"Rec 143012 (pasted)"}],"tracksRemoved":["7f2c"],"clipsChanged":[{"trackId":"7f2c","slot":2,"before":false,"after":true},{"trackId":"rec-1","slot":0,"before":true,"after":false}]}""");
            a["seen"]!["slotRecording"] = true;
        });
        Assert.Equal(LiveOutcome.Failed, messy.Outcome);
        Assert.EndsWith(" But Bitwig's arranger record is still on, Bitwig didn't take back arranger record and play start as they were, Bitwig added " +
                        "“Rec 143012 (pasted)” meanwhile, 1 track went away meanwhile, 2 launcher slots changed that the link didn't change (slot 3 on the " +
                        "track with ID 7f2c got a clip, slot 1 on “Rec 143012” lost its clip) and Bitwig recorded into launcher slot 1 on “Rec 143012” as " +
                        "well, which the test project should have been left without.", messy.Detail);
        Assert.Equal(LiveOutcome.Failed, Verdict(a => a["after"]!["restoreFailed"] = null).Outcome);

        // What Bitwig did on the way is said too.
        var noted = Verdict(a =>
        {
            a["seen"]!["automationWriteSeenOn"] = true;
            a["played"]!["ended"] = "timedOut";
            a["played"]!["positionFrom"] = "clock";
        });
        Assert.Equal(LiveOutcome.Passed, noted.Outcome);
        Assert.EndsWith(" Bitwig turned automation write on while it recorded (its “Write automation when recording” setting); the link moved nothing " +
                        "then, so nothing should have been written, but look for automation on the tracks. Bitwig didn't stop the track at the clip's " +
                        "end, so the link stopped Bitwig when the time ran out: what went in may run past bars 5 to 6. Bitwig's play position didn't " +
                        "move as it played, so the link went by the clock and the tempo to tell where it was.", noted.Detail);
        Assert.EndsWith(" It ran out of time.", noted.Lines[0]);
        // Armed and nothing in: a fault.
        var armedNothing = RecordVerdict(Answer(a => { a["decided"] = "nothing"; a["arm"] = true; }), RecordGo.Armed, "Rec 143012");
        Assert.Equal(LiveOutcome.Failed, armedNothing.Outcome);
        Assert.DoesNotContain("unarmed", armedNothing.Detail);
        // Bitwig went on playing past the clip's end, so the extension (3.10) cut the take there.
        var cut = Verdict(a => a["played"]!["ended"] = "cut");
        Assert.Equal(LiveOutcome.Passed, cut.Outcome);
        Assert.EndsWith(" Bitwig didn't stop the track at the clip's end, so the link turned arranger record off just past it and stopped Bitwig: " +
                        "what went in may run a moment past bars 5 to 6.", cut.Detail);
        Assert.EndsWith(" Bitwig didn't stop the track at the clip's end, so the link stopped there.", cut.Lines[0]);

        // Only the clip's first bar played, and that went in (Solomon's second run): noted, not passed.
        void FirstBar(JsonObject a)
        {
            foreach (var part in new[] { a["played"]!, a["replay"]! })
            {
                part["heard"] = 3;
                var notes = part["notes"]!.AsArray();
                while (notes.Count > 3) notes.RemoveAt(notes.Count - 1);
            }
            a["heard"] = 3;
            a["replay"]!["inPass"] = 3;
        }
        var firstBar = RecordVerdict(Answer(FirstBar), RecordGo.Record, "Rec 143012", clipNotes: 6);
        Assert.Equal(LiveOutcome.Noted, firstBar.Outcome);
        Assert.StartsWith("Bitwig recorded the clip into its arranger on “Rec 143012” at bars 5 to 6: playing the arranger back, the link heard 3 notes " +
                          "there, with the launcher clip not playing. That's only part of the clip, which has 6 notes: Bitwig played only 3 of them, so " +
                          "only those went in. The link can't see an arranger clip itself:", firstBar.Detail);
        // It all played, but only some went in.
        var halfIn = RecordVerdict(Answer(a => a["replay"]!["inPass"] = 4), RecordGo.Record, "Rec 143012", clipNotes: 6);
        Assert.Equal(LiveOutcome.Noted, halfIn.Outcome);
        Assert.Contains(" That's only part of the clip, which has 6 notes: Bitwig played them all, but only 4 went in.", halfIn.Detail);
        // Without the clip's count, nothing is checked against it.
        Assert.Equal(LiveOutcome.Passed, RecordVerdict(Answer(FirstBar), RecordGo.Record, "Rec 143012").Outcome);
        Assert.Equal(LiveOutcome.Passed, RecordVerdict(Answer(), RecordGo.Record, "Rec 143012", clipNotes: 6).Outcome);

        // Extension 3.11's trace comes through with the answer, and into the report's data.
        var traced = Answer(a => a["trace"] = new JsonArray("0 recordOn @16.000", "25 launch @16.000"));
        Assert.Equal(["0 recordOn @16.000", "25 launch @16.000"], traced.Trace);
        Assert.Equal("25 launch @16.000", (string?)RecordVerdict(traced, RecordGo.Record, "Rec 143012").Data!["trace"]![1]);
        Assert.Null(Answer().Trace);

        // The fast go deleted the launcher clip, and then the earlier recording went silent: noted, as Bitwig may record alias clips.
        var earlier = new Dictionary<RecordGo, BitwigRecorded> { [RecordGo.Record] = Answer() };
        var alias = RecordVerdict(Answer(a =>
        {
            a["at"] = 48;
            a["launcherClipDeleted"] = true;
            a["tempo"] = JsonNode.Parse("""{"project":120,"used":240}""");
            a["diff"]!["clipsChanged"] = JsonNode.Parse("""[{"trackId":"rec-1","slot":0,"before":true,"after":false}]""");
        }), RecordGo.Fast, "Rec 143012", earlier);
        Assert.Equal(LiveOutcome.Noted, alias.Outcome);
        Assert.Contains("The link deleted the launcher clip before the replay, and then heard nothing of the earlier recording at bars 5 to 6, so Bitwig " +
                        "may have recorded it as copies that share the launcher clip's notes (Play menu › Record to Arranger as Alias Clips).", alias.Detail);
        Assert.DoesNotContain(" But ", alias.Detail);
    }

    /// <summary>What the panel probe answers by a route: whether each panel showed before and after each go.</summary>
    static string Probed(string via, string toggle, string launcher, string arranger) =>
        $$"""{"via":"{{via}}","layout":"ARRANGE","steps":[{"action":"toggle_arranger","ran":[true,true],"visible":{{toggle}},"restored":null},""" +
        $$"""{"action":"focus_or_toggle_clip_launcher","ran":[true,true],"visible":{{launcher}},"restored":true},""" +
        $$"""{"action":"focus_or_toggle_arranger","ran":[true,true],"visible":{{arranger}},"restored":true}]}""";

    [Fact]
    public async Task ProbesThePanelsFromTheFlushThenStraightAway()
    {
        await using var java = Stand();
        java.Answer = request => (string)request["cmd"]! == "probe"
            ? (string?)request["via"] == "direct"
                ? Probed("direct", "[true,true,true]", "[true,true,true]", "[true,true,true]")
                : Probed("flush", "[true,false,true]", "[true,true,false]", "[true,true,false]")
            : null;
        using var link = await BitwigLink.ConnectAsync(java.Port);
        var verdict = await PanelCommandsAsync(link);

        Assert.Equal([null, "direct"], Sent(java, "probe").Select(r => (string?)r["via"]));
        Assert.Equal((LiveOutcome.Passed, "Bitwig carried out each of its panel commands for the link from Bitwig's flush (flush): a panel showed or hid " +
                                          "with each, so it goes along with what the link asks of its panels, and copy and paste failed because they act " +
                                          "on what was last clicked in Bitwig."),
                     (verdict.Outcome, verdict.Detail));
        Assert.Equal(["- From Bitwig's flush (flush), toggle_arranger: the arranger timeline went shown, hidden, shown, so Bitwig carried it out",
                      "- From Bitwig's flush (flush), focus_or_toggle_clip_launcher: the clip launcher went shown, shown, hidden, so Bitwig carried it out",
                      "- From Bitwig's flush (flush), focus_or_toggle_arranger: the arranger timeline went shown, shown, hidden, so Bitwig carried it out",
                      "- Straight away (direct), toggle_arranger: the arranger timeline went shown, shown, shown, so Bitwig ignored it",
                      "- Straight away (direct), focus_or_toggle_clip_launcher: the clip launcher went shown, shown, shown, so Bitwig ignored it",
                      "- Straight away (direct), focus_or_toggle_arranger: the arranger timeline went shown, shown, shown, so Bitwig ignored it"],
                     verdict.Lines);
        Assert.Equal(2, verdict.Data!.AsArray().Count);

        static BitwigProbed P(string json) => JsonSerializer.Deserialize<BitwigProbed>(json, Web)!;
        var ignored = ProbeVerdict([P(Probed("flush", "[true,true,true]", "[true,true,true]", "[false,false,false]"))]);
        Assert.Equal((LiveOutcome.Failed, "Bitwig carried out none of its panel commands for the link, by any route tried: no panel showed or hid with " +
                                          "them, so it ignores them from an extension as it ignored copy and paste."),
                     (ignored.Outcome, ignored.Detail));
        var some = ProbeVerdict([P(Probed("flush", "[true,false,true]", "[true,true,true]", "[true,true,false]"))]);
        Assert.Equal(LiveOutcome.Noted, some.Outcome);
        var missing = ProbeVerdict([P("""{"via":"flush","layout":"MIX","steps":[{"action":"toggle_arranger","missing":true},{"action":"focus_or_toggle_clip_launcher","ran":[true,false],"visible":[true,true,true],"restored":null},{"action":"focus_or_toggle_arranger","ran":[true,true],"visible":[true,false,false],"restored":false}]}""")]);
        Assert.Equal(["- From Bitwig's flush (flush), toggle_arranger: Bitwig has no such action",
                      "- From Bitwig's flush (flush), focus_or_toggle_clip_launcher: the route didn't run it",
                      "- From Bitwig's flush (flush), focus_or_toggle_arranger: the arranger timeline went shown, hidden, hidden, so Bitwig carried it out " +
                      "(the link couldn't show or hide the arranger timeline as it was again)"], missing.Lines);
        Assert.Equal(LiveOutcome.Noted, missing.Outcome);
    }
}
