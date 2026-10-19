package org.musicorganiser.bitwig;

import static org.junit.jupiter.api.Assertions.assertEquals;
import static org.junit.jupiter.api.Assertions.assertFalse;
import static org.musicorganiser.bitwig.FakeBitwig.device;
import static org.musicorganiser.bitwig.FakeBitwig.knob;
import static org.musicorganiser.bitwig.Messages.assertJson;
import static org.musicorganiser.bitwig.Messages.assertMatches;
import static org.musicorganiser.bitwig.Messages.at;
import static org.musicorganiser.bitwig.Messages.error;
import static org.musicorganiser.bitwig.Messages.list;
import static org.musicorganiser.bitwig.Messages.listAt;
import static org.musicorganiser.bitwig.Messages.obj;

import java.nio.file.Path;
import java.util.List;
import java.util.Map;
import java.util.stream.Collectors;
import org.junit.jupiter.api.DisplayName;
import org.junit.jupiter.api.Test;
import org.junit.jupiter.api.io.TempDir;
import org.musicorganiser.bitwig.FakeBitwig.App;
import org.musicorganiser.bitwig.FakeBitwig.Model;

/** What link version 3 adds with Bitwig's newer API: track IDs, parameter steps and more, clicked and recorder. */
class NewerApiTest {
    @TempDir
    Path reports;

    FakeBitwig bitwig;
    App app;

    void start(Model model) {
        bitwig = FakeBitwig.start(model, reports);
        app = bitwig.connect();
        app.take();
    }

    @Test
    @DisplayName("gives a track the ID Bitwig keeps it under, once Bitwig has said")
    void tracksCarryTheirIds() {
        Model m = LinkTest.song();
        m.tracks.get(1).id("a3f0c1d2-2b7e-4c55-9b1e-6d0f2c8e4a10");
        start(m);
        List<Object> tracks = listAt(app.ask(obj("id", 1, "cmd", "project")), "result", "tracks");
        assertEquals("a3f0c1d2-2b7e-4c55-9b1e-6d0f2c8e4a10", at(tracks, 1, "id"));
        assertFalse(((Map<?, ?>) tracks.get(0)).containsKey("id"), "no ID where Bitwig hasn't given one");
        assertEquals(List.of("index", "id", "name"), List.copyOf(((Map<?, ?>) tracks.get(1)).keySet()).subList(0, 3));
    }

    @Test
    @DisplayName("says what Bitwig knows of the remote controls: their steps, step names, origin and automation")
    void remoteControlsCarryTheirDetails() {
        start(LinkTest.song());
        bitwig.cursorDevice.show(device("Polysynth").page("Oscillator", "CONTENTS/PID1", "CONTENTS/PID2"),
                                 knob("CONTENTS/PID1", "Waveform", 1, "Square").steps("Sine", "Triangle", "Saw", "Square"),
                                 knob("CONTENTS/PID2", "Pan", 0.5, "C").origin(0.5).automated(),
                                 knob("CONTENTS/PID3", "Cutoff", 0.7, "2.0 kHz"));
        app.send(obj("id", 1, "cmd", "device"));
        bitwig.clock.advance(100);
        Object result = app.take().get(0).get("result");

        // A parameter that is one of the controls carries what Bitwig says of the control; the others are as before.
        assertJson(list(obj("id", "CONTENTS/PID1", "name", "Waveform", "value", 1, "display", "Square",
                            "steps", 4, "stepNames", list("Sine", "Triangle", "Saw", "Square"), "origin", 0, "automated", false),
                        obj("id", "CONTENTS/PID2", "name", "Pan", "value", 0.5, "display", "C", "steps", -1, "origin", 0.5, "automated", true),
                        obj("id", "CONTENTS/PID3", "name", "Cutoff", "value", 0.7, "display", "2.0 kHz")),
                   at(result, "params"));
        assertEquals("Oscillator", at(result, "page"));
        assertJson(list(obj("index", 0, "name", "Waveform", "value", 1, "display", "Square",
                            "steps", 4, "stepNames", list("Sine", "Triangle", "Saw", "Square"), "origin", 0, "automated", false,
                            "param", "CONTENTS/PID1"),
                        obj("index", 1, "name", "Pan", "value", 0.5, "display", "C", "steps", -1, "origin", 0.5, "automated", true,
                            "param", "CONTENTS/PID2")),
                   at(result, "controls"));
    }

    @Test
    @DisplayName("reads a plugin's knob from its remote control, and says a value is out of date, when Bitwig doesn't tell its new value")
    void pluginKnobsBitwigDoesntTell() {
        Model m = LinkTest.song();
        m.pluginValuesUntold = true;
        start(m);
        bitwig.cursorDevice.show(device("MO Probe Instrument").plugin().page("MO Probe", "CONTENTS/PID3e9"),
                                 knob("CONTENTS/PID3e9", "MO Knob A", 0.25, "0.250"),
                                 knob("CONTENTS/PID3ea", "MO Gain", 0.833, "0.0 dB"));
        assertJson(list(obj("id", "CONTENTS/PID3e9", "value", 0.25, "display", "0.250"), obj("id", "CONTENTS/PID3ea", "value", 0.833, "display", "0.0 dB")),
                   knobs(selected()));

        // The probe turns both: Bitwig's texts and its remote control follow, but it never tells the link the values.
        bitwig.cursorDevice.turnByPlugin("CONTENTS/PID3e9", 1, "1.000");
        bitwig.cursorDevice.turnByPlugin("CONTENTS/PID3ea", 0, "-60.0 dB");
        bitwig.clock.advance(50);
        List<Object> turned = knobs(selected());
        assertJson(list(obj("id", "CONTENTS/PID3e9", "value", 1, "display", "1.000"),
                        obj("id", "CONTENTS/PID3ea", "value", null, "display", "-60.0 dB", "stale", true)), turned);
        assertJson(list(obj("id", "CONTENTS/PID3e9", "value", 1, "display", "1.000"),
                        obj("id", "CONTENTS/PID3ea", "value", null, "display", "-60.0 dB", "stale", true)), knobs(selected()),
                   "still out of date, though its text hasn't changed since");

        // Once Bitwig tells the values again (selecting the device again, say), they're good.
        bitwig.cursorDevice.show(device("MO Probe Instrument").plugin().page("MO Probe", "CONTENTS/PID3e9"),
                                 knob("CONTENTS/PID3e9", "MO Knob A", 1, "1.000"),
                                 knob("CONTENTS/PID3ea", "MO Gain", 0, "-60.0 dB"));
        assertJson(list(obj("id", "CONTENTS/PID3e9", "value", 1, "display", "1.000"), obj("id", "CONTENTS/PID3ea", "value", 0, "display", "-60.0 dB")),
                   knobs(selected()));
    }

    @Test
    @DisplayName("doesn't call a value out of date when Bitwig has told it")
    void toldValuesArentStale() {
        start(LinkTest.song());
        bitwig.cursorDevice.show(device("Serum").plugin(), knob("CONTENTS/PID1", "Cutoff", 0.5, "50%"));
        knobs(selected());
        bitwig.cursorDevice.turnByPlugin("CONTENTS/PID1", 0.75, "75%");
        bitwig.clock.advance(50);
        assertJson(list(obj("id", "CONTENTS/PID1", "value", 0.75, "display", "75%")), knobs(selected()));
    }

    /** The selected device, as the link answers `device`. */
    Object selected() {
        app.send(obj("id", 1, "cmd", "device"));
        bitwig.clock.advance(100);
        return app.take().get(0).get("result");
    }

    /** Each parameter's ID, value and text, and whether its value is out of date. */
    static List<Object> knobs(Object device) {
        return listAt(device, "params").stream().map(p -> {
            Map<?, ?> m = (Map<?, ?>) p;
            Map<String, Object> k = new java.util.LinkedHashMap<>();
            for (String key : List.of("id", "value", "display", "stale"))
                if (m.containsKey(key)) k.put(key, m.get(key));
            return (Object) k;
        }).collect(Collectors.toList());
    }

    @Test
    @DisplayName("doesn't guess which parameter a remote control is when two look the same")
    void remoteControlsThatCouldBeEitherParameter() {
        start(LinkTest.song());
        bitwig.cursorDevice.show(device("Mixer").page("Levels", "CONTENTS/PID1"),
                                 knob("CONTENTS/PID1", "Level", 0.5, "0 dB"),
                                 knob("CONTENTS/PID2", "Level", 0.5, "0 dB"));
        app.send(obj("id", 1, "cmd", "device"));
        bitwig.clock.advance(100);
        Object result = app.take().get(0).get("result");
        for (Object p : listAt(result, "params")) assertFalse(((Map<?, ?>) p).containsKey("steps"), Json.write(p));
        assertEquals(null, at(result, "controls", 0, "param"));
        assertJson(1, ((List<?>) at(result, "controls")).size());
    }

    @Test
    @DisplayName("lists no remote controls for a device without a page, nor for none")
    void noRemoteControls() {
        start(LinkTest.song());
        bitwig.cursorDevice.show(device("Utility"), knob("a", "Gain", 0.5, "0 dB"));
        app.send(obj("id", 1, "cmd", "device"));
        bitwig.clock.advance(100);
        Object result = app.take().get(0).get("result");
        assertJson(obj("page", "", "controls", list()), obj("page", at(result, "page"), "controls", at(result, "controls")));
        assertJson(obj("id", "a", "name", "Gain", "value", 0.5, "display", "0 dB"), at(result, "params", 0));
    }

    @Test
    @DisplayName("says which parameter was clicked last, once Bitwig has said, without needing changes allowed")
    void saysWhichParameterWasClickedLast() {
        start(LinkTest.song());
        assertJson(obj("exists", false, "locked", false), app.ask(obj("id", 1, "cmd", "clicked")).get("result"));

        bitwig.click(knob("CONTENTS/PID9", "Filter Type", 0.5, "Band-pass").steps("Low-pass", "Band-pass", "High-pass"));
        assertEquals(false, at(app.ask(obj("id", 2, "cmd", "clicked")), "result", "exists"), "Bitwig says so a moment later");
        bitwig.clock.advance(50);
        assertJson(obj("exists", true, "name", "Filter Type", "value", 0.5, "display", "Band-pass", "steps", 3,
                       "stepNames", list("Low-pass", "Band-pass", "High-pass"), "origin", 0, "automated", false, "locked", false),
                   app.ask(obj("id", 3, "cmd", "clicked")).get("result"));

        // A continuous parameter has no step names.
        bitwig.click(knob("CONTENTS/PID3e8", "Cutoff", 0.25, "440 Hz").automated());
        bitwig.lastClicked.locked.v = true;
        bitwig.clock.advance(50);
        assertJson(obj("exists", true, "name", "Cutoff", "value", 0.25, "display", "440 Hz", "steps", -1, "origin", 0, "automated", true,
                       "locked", true),
                   app.ask(obj("id", 4, "cmd", "clicked")).get("result"));
    }

    @Test
    @DisplayName("starts and stops Bitwig's master recorder once changes are allowed, and says for how long it has recorded")
    void usesTheMasterRecorder() {
        start(LinkTest.song());
        for (Object what : new Object[] {"start", "stop", "status", null})
            assertMatches(LinkTest.CHANGES_OFF, error(app.ask(obj("id", 1, "cmd", "recorder", "do", what))));
        assertEquals(List.of(), bitwig.calls);

        bitwig.allowChanges(true);
        assertJson(obj("active", false, "duration", 0), app.ask(obj("id", 2, "cmd", "recorder")).get("result"));
        assertJson(obj("active", false, "duration", 0), app.ask(obj("id", 3, "cmd", "recorder", "do", "status")).get("result"));

        // It answers once Bitwig says it's recording: the stand-in starts 20 ms after it's asked, and the link looks every 50.
        app.send(obj("id", 4, "cmd", "recorder", "do", "start"));
        assertEquals(List.of(), app.take());
        bitwig.clock.advance(50);
        assertJson(obj("id", 4, "ok", true, "result", obj("active", true, "duration", 30)), app.take().get(0));
        bitwig.clock.advance(950);
        assertJson(obj("active", true, "duration", 980), app.ask(obj("id", 5, "cmd", "recorder")).get("result"));
        // Starting again asks nothing more of Bitwig (and answers when the link next looks).
        assertJson(obj("active", true, "duration", 1030), app.waitFor(obj("id", 6, "cmd", "recorder", "do", "start"), 50).get("result"));

        // Stopped 20 ms after it's asked: 1050 ms recorded in all.
        assertJson(obj("active", false, "duration", 1050), app.waitFor(obj("id", 7, "cmd", "recorder", "do", "stop")).get("result"));
        assertEquals(List.of(List.of("startRecording"), List.of("stopRecording")), bitwig.calls);
        assertEquals("Tell the recorder to \"start\", \"stop\" or give its \"status\", not rewind.",
                     error(app.ask(obj("id", 8, "cmd", "recorder", "do", "rewind"))));
    }

    @Test
    @DisplayName("starts the recorder only in the project the request names, and not once another comes to the front")
    void usesTheRecorderOnlyInTheProjectNamed() {
        start(LinkTest.song());
        bitwig.allowChanges(true);
        assertEquals(LinkTest.elsewhere("Night Drive", "MO Live Test"),
                     error(app.ask(obj("id", 1, "cmd", "recorder", "do", "start", "onlyIn", "MO Live Test"))));
        assertEquals(List.of(), bitwig.calls);

        // Asked while Night Drive is open, but another project comes to the front before it starts (behind a new track).
        app.send(obj("id", 2, "cmd", "newtrack", "name", "Lead", "onlyIn", "Night Drive"));
        app.send(obj("id", 3, "cmd", "recorder", "do", "start", "onlyIn", "Night Drive"));
        bitwig.clock.advance(10);
        bitwig.projectName.v = "My Song";
        bitwig.clock.advance(10000);
        assertEquals(List.of(LinkTest.elsewhere("My Song", "Night Drive"), LinkTest.elsewhere("My Song", "Night Drive")),
                     app.take().stream().map(Messages::error).collect(Collectors.toList()));
        assertEquals(List.of(List.of("createInstrumentTrack", -1)), bitwig.calls);

        bitwig.projectName.v = "Night Drive";
        assertJson(obj("active", true, "duration", 30),
                   app.waitFor(obj("id", 4, "cmd", "recorder", "do", "start", "onlyIn", "Night Drive"), 50).get("result"));
    }

    @Test
    @DisplayName("says when Bitwig doesn't start recording")
    void saysWhenRecordingDoesntStart() {
        Model m = LinkTest.song();
        m.engine = false;
        start(m);
        bitwig.allowChanges(true);
        app.send(obj("id", 1, "cmd", "recorder", "do", "start"));
        bitwig.clock.advance(4900);
        assertEquals(List.of(), app.take(), "waits for Bitwig");
        bitwig.clock.advance(200);
        assertEquals("Bitwig didn't start recording. Is its audio engine on?", error(app.take().get(0)));
        // The next change isn't held up.
        assertEquals(true, app.waitFor(obj("id", 2, "cmd", "newtrack", "name", "Lead")).get("ok"));
    }
}
