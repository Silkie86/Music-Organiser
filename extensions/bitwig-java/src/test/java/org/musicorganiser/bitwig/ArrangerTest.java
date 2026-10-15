package org.musicorganiser.bitwig;

import static org.junit.jupiter.api.Assertions.assertEquals;
import static org.musicorganiser.bitwig.FakeBitwig.action;
import static org.musicorganiser.bitwig.FakeBitwig.track;
import static org.musicorganiser.bitwig.Messages.assertJson;
import static org.musicorganiser.bitwig.Messages.assertMatches;
import static org.musicorganiser.bitwig.Messages.at;
import static org.musicorganiser.bitwig.Messages.error;
import static org.musicorganiser.bitwig.Messages.list;
import static org.musicorganiser.bitwig.Messages.listAt;
import static org.musicorganiser.bitwig.Messages.obj;

import com.bitwig.extension.controller.api.Application;
import java.nio.file.Path;
import java.util.List;
import java.util.Map;
import java.util.function.Consumer;
import java.util.stream.Collectors;
import org.junit.jupiter.api.DisplayName;
import org.junit.jupiter.api.Test;
import org.junit.jupiter.api.io.TempDir;
import org.musicorganiser.bitwig.FakeBitwig.App;
import org.musicorganiser.bitwig.FakeBitwig.Arranged;
import org.musicorganiser.bitwig.FakeBitwig.Model;

/**
 * The arrange experiment (extension 3.6): a launcher clip copied into the arranger the way a person would do it (select,
 * copy, bring the arranger timeline to the front, move the play start, paste), and the answer saying what Bitwig showed,
 * whether it worked or not. The stand-in Bitwig does what Bitwig is expected to; switches in its model make it paste into
 * the launcher instead, or ignore paste, as the real one might.
 */
class ArrangerTest {
    /** Bar 5 in 4/4: four bars of four beats from the start. */
    static final double BAR_5 = 16;

    @TempDir
    Path reports;

    FakeBitwig bitwig;
    App app;

    /** A test project with a clip in the launcher on two tracks, Bitwig's action that brings the arranger timeline to the front, and changes allowed. */
    void studio(Consumer<Model> extra) {
        Model m = new Model();
        m.project = "MO Live Test";
        m.selectedTrack = "Bass";
        m.tracks(
            track("Bass", "Instrument").slots("C:\\MIDI\\Bass.mid"),
            track("Lead", "Instrument").slots("C:\\MIDI\\Lead.mid", null),
            track("Master", "Master"));
        m.actions.add(action("Save", "Save", "File"));
        m.actions.add(action(Link.FOCUS_ARRANGER, "Focus/toggle Arranger Timeline Panel", "View"));
        extra.accept(m);
        bitwig = FakeBitwig.start(m, reports);
        bitwig.allowChanges(true);
        app = bitwig.connect();
        app.take();
    }

    void studio() {
        studio(m -> {});
    }

    static Map<String, Object> arrange(Object id, Object track, Object slot, Object at) {
        return obj("id", id, "cmd", "arrange", "track", track, "slot", slot, "at", at);
    }

    /** The clip selected in the arranger, as the answer says it: on a track, or none (null). */
    static Map<String, Object> clip(String track) {
        return obj("exists", track != null, "track", track);
    }

    @Test
    @DisplayName("puts a copy of a launcher clip in the arranger at bar 5, on the clip's own track, and says so")
    void arrangesALauncherClip() {
        studio();
        Map<String, Object> reply = app.waitFor(arrange(1, 0, 0, BAR_5));
        assertJson(obj("id", 1, "ok", true, "result", obj(
                       "arranged", true, "at", 16, "layout", "ARRANGE", "timeline", obj("before", true, "after", true), "focus", 1,
                       "before", clip(null), "beforePaste", clip(null), "after", clip("Bass"), "launcherClipsBefore", 1, "launcherClipsAfter", 1)),
                   reply);
        assertEquals(List.of(new Arranged(bitwig.state.tracks.get(0), BAR_5, "C:\\MIDI\\Bass.mid")), bitwig.state.arranged);
        assertEquals(List.of("C:\\MIDI\\Bass.mid"), bitwig.state.tracks.get(0).slots, "the launcher keeps its clip");
        // Select, copy, bring the timeline to the front, paste: the layout was Arrange already, so that's left alone.
        assertEquals(List.of("selectChannel", "selectSlot", "copy", "invoke", "paste"), calls());
        assertEquals(List.of(List.of("selectSlot", 0, 0), List.of("invoke", Link.FOCUS_ARRANGER)),
                     bitwig.calls.stream().filter(c -> c.size() > 1 && !c.get(0).equals("selectChannel")).collect(Collectors.toList()));

        // Without `at`, it goes where the play start is (still bar 5), and the clip selected in the arranger moves to Lead.
        Object next = app.waitFor(obj("id", 2, "cmd", "arrange", "track", 1, "slot", 0)).get("result");
        assertJson(obj("arranged", true, "at", 16, "before", clip("Bass"), "after", clip("Lead")),
                   SelfCheckTest.pick(SelfCheckTest.map(next), "arranged", "at", "before", "after"));
        assertEquals(List.of(BAR_5, BAR_5), bitwig.state.arranged.stream().map(Arranged::start).collect(Collectors.toList()));
    }

    @Test
    @DisplayName("switches to the Arrange layout and shows the arranger timeline when it's hidden")
    void switchesToTheArrangeLayout() {
        studio(m -> {
            m.layout = Application.PANEL_LAYOUT_MIX;
            m.timeline = false;
            m.playStart = 8;
        });
        Object result = app.waitFor(obj("id", 1, "cmd", "arrange", "track", 0, "slot", 0)).get("result");
        assertJson(obj("arranged", true, "at", 8, "layout", "MIX", "timeline", obj("before", false, "after", true), "focus", 1),
                   SelfCheckTest.pick(SelfCheckTest.map(result), "arranged", "at", "layout", "timeline", "focus"));
        assertEquals(List.of("selectChannel", "selectSlot", "copy", "setPanelLayout", "invoke", "paste"), calls());
        assertEquals(8.0, bitwig.state.arranged.get(0).start());
    }

    @Test
    @DisplayName("runs Bitwig's focus action again when it hid the arranger timeline, which was in front already")
    void focusesTheTimelineAgainWhenItHid() {
        // As if selecting a launcher slot from the extension left the timeline in front.
        studio(m -> {
            m.focus = FakeBitwig.TIMELINE;
            m.selectFocuses = false;
        });
        Object result = app.waitFor(arrange(1, 0, 0, BAR_5)).get("result");
        assertJson(obj("arranged", true, "timeline", obj("before", true, "after", true), "focus", 2, "after", clip("Bass")),
                   SelfCheckTest.pick(SelfCheckTest.map(result), "arranged", "timeline", "focus", "after"));
        assertEquals(List.of("selectChannel", "selectSlot", "copy", "invoke", "invoke", "paste"), calls());
    }

    @Test
    @DisplayName("says so when Bitwig pasted the clip into the launcher instead")
    void saysWhenBitwigPastedIntoTheLauncher() {
        studio(m -> m.pasteInLauncher = true);
        assertJson(obj("arranged", false, "at", 16, "layout", "ARRANGE", "timeline", obj("before", true, "after", true), "focus", 1,
                       "before", clip(null), "beforePaste", clip(null), "after", clip(null), "launcherClipsBefore", 1, "launcherClipsAfter", 2),
                   app.waitFor(arrange(1, 0, 0, BAR_5)).get("result"));
        assertEquals(List.of("C:\\MIDI\\Bass.mid", "C:\\MIDI\\Bass.mid"), bitwig.state.tracks.get(0).slots);
        assertEquals(List.of(), bitwig.state.arranged);
    }

    @Test
    @DisplayName("says so when nothing appeared, once it has waited for Bitwig, and undoes nothing")
    void saysWhenNothingAppeared() {
        studio(m -> m.pasteIgnored = true);
        app.send(arrange(1, 0, 0, BAR_5));
        bitwig.clock.advance(5000);
        assertEquals(List.of(), app.take(), "waits for Bitwig");
        bitwig.clock.advance(5000);
        Map<String, Object> reply = app.take().get(0);
        assertJson(obj("arranged", false, "before", clip(null), "beforePaste", clip(null), "after", clip(null),
                       "launcherClipsBefore", 1, "launcherClipsAfter", 1),
                   SelfCheckTest.pick(SelfCheckTest.map(reply.get("result")), "arranged", "before", "beforePaste", "after",
                                      "launcherClipsBefore", "launcherClipsAfter"));
        assertEquals(List.of("selectChannel", "selectSlot", "copy", "invoke", "paste"), calls());
        // The play start stays where the link put it: only test projects are sent this.
        assertEquals(16.0, bitwig.playStart.v);
    }

    @Test
    @DisplayName("refuses a slot without a clip, one out of reach and an odd place, before touching anything")
    void refusesWhatItCantArrange() {
        studio();
        assertEquals("Slot 1 on that track has no clip to put in the arranger.", error(app.ask(arrange(1, 1, 1, BAR_5))));
        assertEquals("Slot 4 on that track has no clip to put in the arranger.", error(app.ask(arrange(2, 1, 4, BAR_5))));
        assertEquals("The app can reach the first 8 clip slots on a track, not slot 8.", error(app.ask(arrange(3, 0, 8, BAR_5))));
        assertMatches("not slot undefined", error(app.ask(obj("id", 4, "cmd", "arrange", "track", 0))));
        assertMatches("no track 9", error(app.ask(arrange(5, 9, 0, BAR_5))));
        for (Object at : list(-1, "16", true, list(16), obj()))
            assertMatches("^Say where in the arranger as beats from the start \\(0 or more\\), not ", error(app.ask(arrange(6, 0, 0, at))));
        assertEquals(List.of(), bitwig.calls);
        assertEquals(List.of(), bitwig.state.arranged);
    }

    @Test
    @DisplayName("needs changes turned on, and keeps to the project the request names")
    void needsChangesTurnedOn() {
        studio();
        bitwig.allowChanges(false);
        assertMatches(LinkTest.CHANGES_OFF, error(app.ask(arrange(1, 0, 0, BAR_5))));
        bitwig.allowChanges(true);
        assertEquals(LinkTest.elsewhere("MO Live Test", "Night Drive"),
                     error(app.ask(obj("id", 2, "cmd", "arrange", "track", 0, "slot", 0, "onlyIn", "Night Drive"))));
        assertEquals(List.of(), bitwig.calls);

        // Another project comes to the front once the clip is being selected: nothing is copied or pasted.
        bitwig.switchDuring("arrange", "My Song");
        assertJson(obj("id", 3, "ok", false, "error", LinkTest.elsewhere("My Song", "MO Live Test")),
                   app.waitFor(obj("id", 3, "cmd", "arrange", "track", 0, "slot", 0, "onlyIn", "MO Live Test")));
        assertEquals(List.of("selectChannel", "selectSlot"), calls());
        assertEquals(List.of(), bitwig.state.arranged);
    }

    @Test
    @DisplayName("says when Bitwig has no action to bring the arranger timeline to the front")
    void needsBitwigsFocusAction() {
        studio(m -> m.actions.removeIf(a -> a.id().equals(Link.FOCUS_ARRANGER)));
        assertEquals("Bitwig has no action \"focus_or_toggle_arranger\", which the link runs to bring the arranger timeline to the front.",
                     error(app.ask(arrange(1, 0, 0, BAR_5))));
        assertEquals(List.of(), bitwig.calls);
    }

    @Test
    @DisplayName("still starts in a Bitwig without an arranger for extensions, and says the self-check tells why")
    void startsWithoutTheArranger() {
        studio(m -> m.broken.add("createArranger"));
        assertEquals("MO Live Test", at(app.ask(obj("id", 1, "cmd", "hello")), "result", "project"));
        assertEquals(true, app.ask(obj("id", 2, "cmd", "project")).get("ok"));
        assertEquals("This Bitwig didn't let the link reach its arranger. A selftest says why.", error(app.ask(arrange(3, 0, 0, BAR_5))));

        Map<String, Object> result = SelfCheckTest.map(app.ask(obj("id", 4, "cmd", "selftest")).get("result"));
        assertEquals(SelfCheckTest.CHECKS, SelfCheckTest.names(result));
        assertEquals(List.of("arranger"), listAt(result, "checks").stream().filter(c -> !(Boolean) at(c, "ok")).map(c -> at(c, "name"))
                                                                  .collect(Collectors.toList()));
        assertMatches("^Couldn't set it up: java.lang.AbstractMethodError: ", SelfCheckTest.detail(result, "arranger"));
        assertMatches("couldn't set up the arranger", bitwig.errors.get(0));
        assertEquals(List.of(), bitwig.calls);
    }

    /** What the extension asked of Bitwig, by name. */
    private List<Object> calls() {
        return bitwig.calls.stream().map(c -> c.get(0)).collect(Collectors.toList());
    }
}
