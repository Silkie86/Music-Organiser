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
 * The arrange experiment (extension 3.6): a launcher clip copied into the arranger the way a person would do it (show the
 * Arrange layout and its clip launcher, select the clip, give the launcher the focus, copy, move the play start, bring the
 * arranger timeline to the front, paste), and the answer saying what Bitwig showed, whether it worked or not. The
 * stand-in Bitwig does what Bitwig is expected to, copying and pasting in whichever panel has the focus; switches in its
 * model make it ignore a step, copy from the timeline, paste into the launcher instead, or ignore paste, as the real one
 * might.
 */
class ArrangerTest {
    /** Bar 5 in 4/4: four bars of four beats from the start. */
    static final double BAR_5 = 16;

    @TempDir
    Path reports;

    FakeBitwig bitwig;
    App app;

    /**
     * A test project with a clip in the launcher on two tracks, Bitwig's actions that bring the arranger timeline and the
     * clip launcher to the front, and changes allowed.
     */
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
        m.actions.add(action(Link.FOCUS_LAUNCHER, "Focus/toggle Clip Launcher Panel", "View"));
        extra.accept(m);
        bitwig = FakeBitwig.start(m, reports);
        bitwig.allowChanges(true);
        app = bitwig.connect();
        app.take();
    }

    void studio() {
        studio(m -> {});
    }

    /** As {@link #studio(Consumer)}, without Bitwig's action that brings the clip launcher to the front. */
    void studioWithoutLauncherFocus(Consumer<Model> extra) {
        studio(m -> {
            m.actions.removeIf(a -> a.id().equals(Link.FOCUS_LAUNCHER));
            extra.accept(m);
        });
    }

    /** Lead's clip, in the arranger at the start and selected there, as if someone had just clicked it in the timeline. */
    void leadClipSelectedInTheArranger() {
        Arranged lead = new Arranged(bitwig.state.tracks.get(1), 0, "C:\\MIDI\\Lead.mid");
        bitwig.state.arranged.add(lead);
        bitwig.state.arrangerSelected = lead;
        bitwig.refresh();
    }

    static Map<String, Object> arrange(Object id, Object track, Object slot, Object at) {
        return obj("id", id, "cmd", "arrange", "track", track, "slot", slot, "at", at);
    }

    /** The clip selected in the arranger, as the answer says it: on a track, or none (null). */
    static Map<String, Object> clip(String track) {
        return obj("exists", track != null, "track", track);
    }

    static Map<String, Object> shown(boolean before, boolean after) {
        return obj("before", before, "after", after);
    }

    @Test
    @DisplayName("puts a copy of a launcher clip in the arranger at bar 5, on the clip's own track, and says so")
    void arrangesALauncherClip() {
        studio();
        Map<String, Object> reply = app.waitFor(arrange(1, 0, 0, BAR_5));
        assertJson(obj("id", 1, "ok", true, "result", obj(
                       "arranged", true, "copiedFromTimeline", false, "at", 16, "layout", "ARRANGE", "launcher", shown(true, true), "launcherFocus", 1,
                       "timeline", shown(true, true), "focus", 1, "before", clip(null), "beforePaste", clip(null), "after", clip("Bass"),
                       "launcherClipsBefore", 1, "launcherClipsAfter", 1)),
                   reply);
        assertEquals(List.of(new Arranged(bitwig.state.tracks.get(0), BAR_5, "C:\\MIDI\\Bass.mid")), bitwig.state.arranged);
        assertEquals(List.of("C:\\MIDI\\Bass.mid"), bitwig.state.tracks.get(0).slots, "the launcher keeps its clip");
        // Select, give the launcher the focus, copy, move the play start, bring the timeline to the front, paste: the layout
        // was Arrange already, with its clip launcher showing, so that's left alone.
        assertEquals(List.of("selectChannel", "selectSlot", "invoke", "copy", "playStart", "invoke", "paste"), calls());
        assertEquals(List.of(List.of("selectSlot", 0, 0), List.of("invoke", Link.FOCUS_LAUNCHER), List.of("playStart", BAR_5),
                             List.of("invoke", Link.FOCUS_ARRANGER)),
                     bitwig.calls.stream().filter(c -> c.size() > 1 && !c.get(0).equals("selectChannel")).collect(Collectors.toList()));

        // Without `at`, it goes where the play start is (still bar 5), and the clip selected in the arranger moves to Lead.
        // The timeline has the focus after the paste: the launcher gets it back before the copy.
        Object next = app.waitFor(obj("id", 2, "cmd", "arrange", "track", 1, "slot", 0)).get("result");
        assertJson(obj("arranged", true, "copiedFromTimeline", false, "at", 16, "launcherFocus", 1, "focus", 1, "before", clip("Bass"), "after", clip("Lead")),
                   SelfCheckTest.pick(SelfCheckTest.map(next), "arranged", "copiedFromTimeline", "at", "launcherFocus", "focus", "before", "after"));
        assertEquals(List.of(BAR_5, BAR_5), bitwig.state.arranged.stream().map(Arranged::start).collect(Collectors.toList()));
        assertEquals("C:\\MIDI\\Lead.mid", bitwig.state.arranged.get(1).clip());
    }

    @Test
    @DisplayName("switches to the Arrange layout, and shows its clip launcher and the arranger timeline when they're hidden")
    void switchesToTheArrangeLayout() {
        studio(m -> {
            m.layout = Application.PANEL_LAYOUT_MIX;
            m.timeline = false;
            m.launcher = false;
            m.playStart = 8;
        });
        Object result = app.waitFor(obj("id", 1, "cmd", "arrange", "track", 0, "slot", 0)).get("result");
        assertJson(obj("arranged", true, "at", 8, "layout", "MIX", "launcher", shown(false, true), "launcherFocus", 1, "timeline", shown(false, true),
                       "focus", 1),
                   SelfCheckTest.pick(SelfCheckTest.map(result), "arranged", "at", "layout", "launcher", "launcherFocus", "timeline", "focus"));
        assertEquals(List.of("setPanelLayout", "showLauncher", "selectChannel", "selectSlot", "invoke", "copy", "playStart", "invoke", "paste"), calls());
        assertEquals(List.of(List.of("setPanelLayout", "ARRANGE"), List.of("showLauncher", true)), bitwig.calls.subList(0, 2));
        assertEquals(8.0, bitwig.state.arranged.get(0).start());
    }

    @Test
    @DisplayName("gives the clip launcher the focus before it copies, so a timeline in front doesn't spoil the copy")
    void givesTheLauncherTheFocusBeforeCopying() {
        studio(m -> m.focus = FakeBitwig.TIMELINE);
        leadClipSelectedInTheArranger();
        Object result = app.waitFor(arrange(1, 0, 0, BAR_5)).get("result");
        assertJson(obj("arranged", true, "copiedFromTimeline", false, "launcherFocus", 1, "focus", 1, "before", clip("Lead"), "after", clip("Bass")),
                   SelfCheckTest.pick(SelfCheckTest.map(result), "arranged", "copiedFromTimeline", "launcherFocus", "focus", "before", "after"));
        assertEquals("C:\\MIDI\\Bass.mid", bitwig.clipboard);
        assertEquals(new Arranged(bitwig.state.tracks.get(0), BAR_5, "C:\\MIDI\\Bass.mid"), bitwig.state.arranged.get(1));
    }

    @Test
    @DisplayName("runs Bitwig's launcher focus action again when it hid the clip launcher, which was in front already")
    void focusesTheLauncherAgainWhenItHid() {
        // Selecting the slot gave the launcher the focus, as clicking it would.
        studio(m -> m.selectFocuses = true);
        Object result = app.waitFor(arrange(1, 0, 0, BAR_5)).get("result");
        assertJson(obj("arranged", true, "launcher", shown(true, true), "launcherFocus", 2, "focus", 1, "after", clip("Bass")),
                   SelfCheckTest.pick(SelfCheckTest.map(result), "arranged", "launcher", "launcherFocus", "focus", "after"));
        assertEquals(List.of("selectChannel", "selectSlot", "invoke", "invoke", "copy", "playStart", "invoke", "paste"), calls());
    }

    @Test
    @DisplayName("doesn't paste when the arranger timeline had the focus as it copied (its focus action hid the timeline), and says so")
    void doesntPasteWhatItCopiedFromTheTimeline() {
        // Without Bitwig's launcher focus action, and with Lead's clip selected in the timeline, which has the focus: Bitwig
        // copies that, not Bass's launcher clip. Bringing the timeline to the front then hides it, which is how the link knows.
        studioWithoutLauncherFocus(m -> m.focus = FakeBitwig.TIMELINE);
        leadClipSelectedInTheArranger();
        assertJson(obj("arranged", false, "copiedFromTimeline", true, "at", 16, "layout", "ARRANGE", "launcher", shown(true, true), "launcherFocus", 0,
                       "timeline", shown(true, true), "focus", 2, "before", clip("Lead"), "beforePaste", clip("Lead"), "after", clip("Lead"),
                       "launcherClipsBefore", 1, "launcherClipsAfter", 1),
                   app.waitFor(arrange(1, 0, 0, BAR_5)).get("result"));
        assertEquals("C:\\MIDI\\Lead.mid", bitwig.clipboard, "Bitwig copied from the timeline");
        assertEquals(List.of("selectChannel", "selectSlot", "copy", "playStart", "invoke", "invoke"), calls());
        assertEquals(1, bitwig.state.arranged.size(), "nothing pasted");
        assertEquals(List.of("C:\\MIDI\\Bass.mid"), bitwig.state.tracks.get(0).slots);

        // The same when Bitwig has the action but ignores it.
        studio(m -> {
            m.focus = FakeBitwig.TIMELINE;
            m.focusIgnored.add(Link.FOCUS_LAUNCHER);
        });
        leadClipSelectedInTheArranger();
        assertJson(obj("arranged", false, "copiedFromTimeline", true, "launcherFocus", 1, "focus", 2),
                   SelfCheckTest.pick(SelfCheckTest.map(app.waitFor(arrange(1, 0, 0, BAR_5)).get("result")),
                                      "arranged", "copiedFromTimeline", "launcherFocus", "focus"));
        assertEquals(List.of("selectChannel", "selectSlot", "invoke", "copy", "playStart", "invoke", "invoke"), calls());
        assertEquals(1, bitwig.state.arranged.size(), "nothing pasted");
    }

    @Test
    @DisplayName("copies from the launcher without Bitwig's launcher focus action when the launcher has the focus")
    void copiesWithoutTheLauncherFocusAction() {
        studioWithoutLauncherFocus(m -> m.selectFocuses = true);
        Object result = app.waitFor(arrange(1, 0, 0, BAR_5)).get("result");
        assertJson(obj("arranged", true, "copiedFromTimeline", false, "launcherFocus", 0, "focus", 1, "after", clip("Bass")),
                   SelfCheckTest.pick(SelfCheckTest.map(result), "arranged", "copiedFromTimeline", "launcherFocus", "focus", "after"));
        assertEquals(List.of("selectChannel", "selectSlot", "copy", "playStart", "invoke", "paste"), calls());
    }

    @Test
    @DisplayName("says so when Bitwig pasted the clip into the launcher instead")
    void saysWhenBitwigPastedIntoTheLauncher() {
        studio(m -> m.pasteInLauncher = true);
        assertJson(obj("arranged", false, "copiedFromTimeline", false, "at", 16, "layout", "ARRANGE", "launcher", shown(true, true), "launcherFocus", 1,
                       "timeline", shown(true, true), "focus", 1, "before", clip(null), "beforePaste", clip(null), "after", clip(null),
                       "launcherClipsBefore", 1, "launcherClipsAfter", 2),
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
        assertEquals(List.of("selectChannel", "selectSlot", "invoke", "copy", "playStart", "invoke", "paste"), calls());
        // The play start stays where the link put it: only test projects are sent this.
        assertEquals(16.0, bitwig.playStart.v);
    }

    @Test
    @DisplayName("stops at the step Bitwig doesn't go along with, saying which, and pastes nothing")
    void stopsAtAStepBitwigIgnores() {
        // Each step, with what the link asked of Bitwig by then: nothing is copied before the clip is selected (with the
        // launcher showing, and in front), and nothing is pasted unless every step went through.
        fails(m -> {
            m.layout = Application.PANEL_LAYOUT_MIX;
            m.layoutIgnored = true;
        }, "Bitwig didn't switch to its Arrange layout.", "setPanelLayout");
        fails(m -> {
            m.launcher = false;
            m.launcherIgnored = true;
        }, "Bitwig didn't show the clip launcher in its Arrange layout.", "showLauncher");
        fails(m -> m.selectIgnored = true, "Bitwig didn't select the clip in slot 0 of track 0.", "selectChannel", "selectSlot");
        fails(m -> {
            m.selectFocuses = true;
            m.focusOnlyHides.add(Link.FOCUS_LAUNCHER);
        }, "Bitwig hid its clip launcher, which was in front already, and didn't show it again.", "selectChannel", "selectSlot", "invoke", "invoke");
        fails(m -> m.playStartIgnored = true, "Bitwig didn't move the play start to beat 16.", "selectChannel", "selectSlot", "invoke", "copy", "playStart");
        fails(m -> {
            m.timeline = false;
            m.focusIgnored.add(Link.FOCUS_ARRANGER);
        }, "Bitwig didn't show its arranger timeline.", "selectChannel", "selectSlot", "invoke", "copy", "playStart", "invoke");
        fails(m -> {
            m.focus = FakeBitwig.TIMELINE;
            m.focusIgnored.add(Link.FOCUS_LAUNCHER);
            m.focusOnlyHides.add(Link.FOCUS_ARRANGER);
        }, "Bitwig hid its arranger timeline, which was in front already, and didn't show it again.",
              "selectChannel", "selectSlot", "invoke", "copy", "playStart", "invoke", "invoke");
    }

    /** Runs arrange in a studio set up by `setup`, which fails with `error` once the link has asked Bitwig for `calls`, and no more. */
    private void fails(Consumer<Model> setup, String error, String... calls) {
        studio(setup);
        assertJson(obj("id", 1, "ok", false, "error", error), app.waitFor(arrange(1, 0, 0, BAR_5)));
        assertEquals(List.of(calls), calls(), error);
        bitwig.clock.advance(10000);
        assertEquals(List.of(calls), calls(), "and nothing more afterwards: " + error);
        assertEquals(List.of(), bitwig.state.arranged, error);
        assertEquals(List.of("C:\\MIDI\\Bass.mid"), bitwig.state.tracks.get(0).slots, error);
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
        bitwig.switchDuring("arrange", () -> calls().contains("selectSlot"), "My Song");
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
