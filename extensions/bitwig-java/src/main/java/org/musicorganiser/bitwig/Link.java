package org.musicorganiser.bitwig;

import com.bitwig.extension.controller.api.Action;
import com.bitwig.extension.controller.api.ActionCategory;
import com.bitwig.extension.controller.api.Application;
import com.bitwig.extension.controller.api.Arranger;
import com.bitwig.extension.controller.api.BooleanValue;
import com.bitwig.extension.controller.api.Clip;
import com.bitwig.extension.controller.api.ClipLauncherSlot;
import com.bitwig.extension.controller.api.ClipLauncherSlotBank;
import com.bitwig.extension.controller.api.ControllerHost;
import com.bitwig.extension.controller.api.CursorRemoteControlsPage;
import com.bitwig.extension.controller.api.CursorTrack;
import com.bitwig.extension.controller.api.Device;
import com.bitwig.extension.controller.api.DeviceBank;
import com.bitwig.extension.controller.api.DirectParameterValueDisplayObserver;
import com.bitwig.extension.controller.api.DrumPad;
import com.bitwig.extension.controller.api.DrumPadBank;
import com.bitwig.extension.controller.api.InsertionPoint;
import com.bitwig.extension.controller.api.LastClickedParameter;
import com.bitwig.extension.controller.api.MasterRecorder;
import com.bitwig.extension.controller.api.Parameter;
import com.bitwig.extension.controller.api.PinnableCursorDevice;
import com.bitwig.extension.controller.api.PopupBrowser;
import com.bitwig.extension.controller.api.Project;
import com.bitwig.extension.controller.api.RemoteConnection;
import com.bitwig.extension.controller.api.RemoteSocket;
import com.bitwig.extension.controller.api.SettableBooleanValue;
import com.bitwig.extension.controller.api.SettableColorValue;
import com.bitwig.extension.controller.api.Track;
import com.bitwig.extension.controller.api.TrackBank;
import com.bitwig.extension.controller.api.Transport;
import java.io.PrintWriter;
import java.io.StringWriter;
import java.net.URI;
import java.net.URISyntaxException;
import java.net.URL;
import java.nio.file.Files;
import java.nio.file.Path;
import java.security.CodeSource;
import java.time.Clock;
import java.util.ArrayDeque;
import java.util.ArrayList;
import java.util.Arrays;
import java.util.Deque;
import java.util.HashMap;
import java.util.HashSet;
import java.util.LinkedHashMap;
import java.util.List;
import java.util.Map;
import java.util.Set;
import java.util.UUID;
import java.util.function.BooleanSupplier;
import java.util.function.Consumer;
import java.util.function.IntConsumer;
import java.util.function.Predicate;
import java.util.function.Supplier;
import java.util.regex.Matcher;
import java.util.regex.Pattern;
import java.util.stream.Collectors;

/**
 * The Music Organiser link for Bitwig Studio.
 *
 * Lets the Music Organiser app see what's in the open Bitwig project: its tracks and devices, the selected
 * device's parameters (with the IDs Bitwig saves them under) and Bitwig's actions. With "Let the app make
 * changes" turned on in this controller's settings, the app can also add tracks, set a parameter, insert a
 * plugin, device, preset or sample, put a MIDI file in a clip slot, fill a Drum Machine's pads, run an action
 * such as Save, start and stop Bitwig's master recorder, and (an experiment) copy a launcher clip into the
 * arranger. That's how the app sends an FL Studio channel or a kit into Bitwig, and how it learns Bitwig's file
 * format: change one thing, save, and compare.
 *
 * The app connects to port 19791 on this computer and sends JSON requests, one per message (see {@link
 * Framing}). The commands are described in extensions/README.md. This is a port of the JavaScript controller
 * script (extensions/bitwig/MusicOrganiser.control.js), function by function, so the app talks to either the
 * same way; it adds what only a Java extension can do (newer API, and reports written to files: see {@link
 * Reports}).
 *
 * Everything here runs on Bitwig's controller thread: in init, in Bitwig's callbacks and in tasks given to
 * host.scheduleTask. Bitwig's objects can only be made in init, and values reach the extension a moment after
 * Bitwig changes them, so every value read later is marked as interesting in init, and a change waits for
 * Bitwig to report it.
 */
final class Link {
    static final int PORT = 19791;
    /** The protocol's version: 3 adds the newer API's fields and the clicked, recorder and selftest commands. */
    static final int LINK_VERSION = 3;
    /** Tracks the project listing covers (a flat list: tracks inside groups, effect tracks and the master). */
    static final int MAX_TRACKS = 128;
    /** Devices listed per track: the top level of its device chain. */
    static final int MAX_DEVICES = 16;
    /** Clip launcher slots per track the app can fill: those of the first scenes. */
    static final int SCENES = 8;
    /** A Drum Machine's pads: one for each MIDI note. */
    static final int DRUM_PADS = 128;
    /** How many of each pad's devices the link sees: enough to tell an empty Sampler with effects after it from a pad that plays. */
    static final int PAD_DEVICES = 4;
    /** Devices at the start of each track whose pitch the app can set (a Sampler or a Note Transpose). */
    static final int TUNED_DEVICES = 2;
    /** The selected device's remote controls the link reads steps, origins and automation from (one page). */
    static final int CONTROLS = 8;
    /** How long to wait for the selected device's parameter values to arrive, in milliseconds. */
    static final long DEVICE_WAIT = 2000;
    /** How long to wait for Bitwig to make a change the app asked for (a track, a device, a clip). */
    static final long CHANGE_WAIT = 5000;
    /** When to write the first self-check: once Bitwig has sent the values the link watches. */
    static final long SELF_CHECK_DELAY = 1000;
    /** Bitwig's own devices the app tunes, by the IDs Bitwig saves them under. */
    static final String SAMPLER = "468bc14b-b2e7-45a1-9666-e83117fe404e";
    static final String NOTE_TRANSPOSE = "0815cd9e-3a31-4429-a268-dabd952a3b68";
    /** Bitwig's action that brings the arranger timeline to the front, or hides it when it's in front already. */
    static final String FOCUS_ARRANGER = "focus_or_toggle_arranger";
    /**
     * The same for the clip launcher, which arrange runs before it copies when Bitwig has an action by this ID: Bitwig
     * copies from whichever panel has the focus. Nobody has seen it in Bitwig's list of actions yet, so arrange does
     * without it when it isn't there.
     */
    static final String FOCUS_LAUNCHER = "focus_or_toggle_clip_launcher";
    /** How long to wait to see whether such an action hid its panel, rather than bringing it to the front. */
    static final long TOGGLE_WAIT = 500;
    static final String CHANGES_OFF = "Changes are turned off. To let the app change things, turn on \"Let the app make changes\" "
                                      + "in Bitwig: Settings > Controllers > Music Organiser.";

    private static final Pattern COLOUR = Pattern.compile("^#([0-9a-fA-F]{2})([0-9a-fA-F]{2})([0-9a-fA-F]{2})$");

    private final ControllerHost host;
    private final Reports reports;
    private final Clock clock;

    private Application app;
    private Project project;
    private Transport transport;
    private TrackBank tracks;
    private final Track[] trackItems = new Track[MAX_TRACKS];
    private final DeviceBank[] trackDevices = new DeviceBank[MAX_TRACKS];
    /** Where things go on each track: the start and end of its device chain. */
    private final InsertionPoint[] trackStarts = new InsertionPoint[MAX_TRACKS];
    private final InsertionPoint[] trackEnds = new InsertionPoint[MAX_TRACKS];
    /** Each track's clip launcher slots, and what replaces their contents. */
    private final ClipLauncherSlot[][] trackSlots = new ClipLauncherSlot[MAX_TRACKS][];
    private final InsertionPoint[][] slotInserts = new InsertionPoint[MAX_TRACKS][];
    /** The pitch of the first devices on each track, for when they're a Sampler or a Note Transpose. */
    private final Tuning[][] trackTunings = new Tuning[MAX_TRACKS][];
    /** Whether each track is the selected one. */
    private final BooleanValue[] trackSelected = new BooleanValue[MAX_TRACKS];
    private CursorTrack cursorTrack;
    private InsertionPoint cursorEnd;
    private PinnableCursorDevice cursorDevice;
    private DirectParameterValueDisplayObserver displayObserver;
    /** The selected device's drum pads, when it's a Drum Machine. */
    private DrumPadBank drumPads;
    private final DrumPad[] padItems = new DrumPad[DRUM_PADS];
    /** Each pad's devices, the first of them, and where to put a sample in place of that one. */
    private final DeviceBank[] padDevices = new DeviceBank[DRUM_PADS];
    private final Device[] padFirst = new Device[DRUM_PADS];
    private final InsertionPoint[] padInserts = new InsertionPoint[DRUM_PADS];
    private final InsertionPoint[] padReplace = new InsertionPoint[DRUM_PADS];
    private PopupBrowser popupBrowser;
    private SettableBooleanValue changesSetting;
    private RemoteSocket socket;
    /** The port Bitwig gave the link; 0 or less while Bitwig hasn't said (see {@link #knownPort}). */
    private int port = PORT;
    private boolean listening;
    /** Whether Bitwig has been told the port is taken (once is enough). */
    private boolean portWarned;
    /** What most often takes the port: the link itself, loaded twice. */
    static final String TWICE = "If Music Organiser is listed twice in Bitwig's Settings > Controllers, remove one; "
                                + "otherwise close what's using the port. Then restart Bitwig.";

    // Newer API (version 20 to 23), each null when Bitwig wouldn't make it.
    private CursorRemoteControlsPage remoteControls;
    private Parameter[] controls;
    private LastClickedParameter lastClicked;
    private Parameter clickedParameter;
    private MasterRecorder recorder;
    /** Bitwig's arranger, for the arrange experiment: whether its timeline shows, and the clip selected there, on its track. */
    private Arranger arranger;
    private Clip arrangerClip;
    private Track arrangerClipTrack;
    /** Whether parameters carry their steps, origin and automation (marked as interesting in init). */
    private boolean details;
    /** Whether tracks carry their IDs. */
    private boolean trackIds;

    /**
     * The selected device's parameters, as Bitwig reports them. Display texts are only asked for while a request
     * waits for them, since Bitwig advises against watching them all the time.
     */
    private final Params params = new Params();

    private final List<Client> clients = new ArrayList<>();
    /** Requests waiting for the selected device's parameter values. */
    private final List<Ask> deviceWaiters = new ArrayList<>();
    private long deviceWaitStart;

    /** Changes that wait for Bitwig, run one at a time in the order they were asked for. */
    private final Deque<Job> jobs = new ArrayDeque<>();
    /** The change running now, if any: each of its steps after a wait checks it may still go ahead. */
    private Job currentJob;

    /** What init made of each part of Bitwig's API, for the self-check. */
    private final List<Feature> features = new ArrayList<>();

    /**
     * The commands the app can send. Those marked `changes` only run when the setting allows it. A job waits
     * for Bitwig to make the change, so jobs run one at a time and answer when they're done.
     */
    private final Map<String, Command> commands = new HashMap<>();

    /**
     * The file Bitwig loaded the extension from, or "" when that can't be told. Bitwig may load extensions from another
     * Bitwig Studio folder than the one holding the projects (Documents, while the projects are on another drive), so
     * the app needs this to put a new version where Bitwig looks.
     */
    String loadedFrom = loadedFrom(Link.class);

    Link(ControllerHost host, Reports reports, Clock clock) {
        this.host = host;
        this.reports = reports;
        this.clock = clock;
        commands.put("hello", Command.answer(false, (ask, request) -> reply(ask, hello(), null)));
        commands.put("project", Command.answer(false, (ask, request) -> reply(ask, describeProject(), null)));
        commands.put("device", Command.answer(false, this::requestDevice));
        commands.put("actions", Command.answer(false, (ask, request) -> reply(ask, listActions(), null)));
        commands.put("select", Command.answer(false, this::select));
        commands.put("set", Command.answer(true, this::setParameter));
        commands.put("insert", Command.job(this::insert));
        commands.put("newtrack", Command.job(this::newTrack));
        commands.put("clip", Command.job(this::addClip));
        commands.put("pads", Command.job(this::fillPads));
        commands.put("transpose", Command.job(this::transpose));
        commands.put("action", Command.answer(true, this::runAction));
        // Link version 3.
        commands.put("clicked", Command.answer(false, this::clicked));
        commands.put("recorder", Command.job(this::useRecorder));
        commands.put("selftest", Command.answer(false, (ask, request) -> reply(ask, selfTest("asked"), null)));
        // Extension 3.6: an experiment, which only the live tests send.
        commands.put("arrange", Command.job(this::arrange));
    }

    // ---- Starting and stopping ----

    void init() {
        reports.tidy();

        feature("application", () -> {
            app = host.createApplication();
            app.projectName().markInterested();
            app.hasActiveEngine().markInterested();
        }, () -> "Project \"" + str(app.projectName().get()) + "\", audio engine " + (app.hasActiveEngine().get() ? "on." : "off."));

        feature("project", () -> {
            project = host.getProject();
            project.isModified().markInterested();
        }, () -> project.isModified().get() ? "It has unsaved changes." : "No unsaved changes.");

        feature("transport", () -> {
            transport = host.createTransport();
            transport.tempo().markInterested();
            transport.timeSignature().markInterested();
            transport.isPlaying().markInterested();
            transport.getPosition().markInterested();
        }, () -> Json.number(transport.tempo().getRaw()) + " BPM in " + str(transport.timeSignature().get())
                 + (transport.isPlaying().get() ? ", playing." : ", stopped."));

        // The track and device selected in Bitwig.
        feature("cursor track", () -> {
            cursorTrack = host.createCursorTrack("MO_LINK", "Music Organiser", 0, 0, true);
            cursorTrack.exists().markInterested();
            cursorTrack.name().markInterested();
            cursorEnd = cursorTrack.endOfDeviceChainInsertionPoint();
        }, () -> cursorTrack.exists().get() ? "Track \"" + str(cursorTrack.name().get()) + "\" is selected." : "No track is selected.");

        feature("cursor device", () -> {
            cursorDevice = cursorTrack.createCursorDevice();
            watchDevice(cursorDevice);
            cursorDevice.position().markInterested();
            cursorDevice.hasDrumPads().markInterested();
            cursorDevice.addDirectParameterIdObserver(ids -> parameterIds(ids));
            cursorDevice.addDirectParameterNameObserver(256, (id, name) -> params.names.put(String.valueOf(id), str(name)));
            cursorDevice.addDirectParameterNormalizedValueObserver((id, value) -> params.values.put(String.valueOf(id), value));
            displayObserver = cursorDevice.addDirectParameterValueDisplayObserver(64, (id, text) -> {
                params.displays.put(String.valueOf(id), str(text));
            });
        }, () -> cursorDevice.exists().get()
                 ? "Device \"" + str(cursorDevice.name().get()) + "\" is selected, with " + params.ids.size() + " parameters."
                 : "No device is selected.");

        // A Drum Machine's pads, when the selected device is one: pad n plays MIDI note n.
        feature("drum pads", () -> {
            drumPads = cursorDevice.createDrumPadBank(DRUM_PADS);
            drumPads.setIndication(false);
            drumPads.scrollPosition().markInterested();
            for (int p = 0; p < DRUM_PADS; p++) {
                DrumPad pad = (DrumPad) drumPads.getItemAt(p);
                pad.exists().markInterested();
                pad.name().markInterested();
                DeviceBank chain = pad.createDeviceBank(PAD_DEVICES);
                chain.itemCount().markInterested();
                Device first = (Device) chain.getItemAt(0);
                first.exists().markInterested();
                first.name().markInterested();
                first.sampleName().markInterested();
                for (int d = 1; d < PAD_DEVICES; d++) {
                    Device after = (Device) chain.getItemAt(d);
                    after.exists().markInterested();
                    after.deviceType().markInterested();
                }
                padItems[p] = pad;
                padDevices[p] = chain;
                padFirst[p] = first;
                padInserts[p] = pad.insertionPoint();
                padReplace[p] = first.replaceDeviceInsertionPoint();
            }
        }, () -> cursorDevice.hasDrumPads().get()
                 ? "The selected device has drum pads, shown from note " + drumPads.scrollPosition().get() + "."
                 : "The selected device has no drum pads.");

        feature("track bank", () -> {
            tracks = host.createTrackBank(MAX_TRACKS, 0, SCENES, true);
            tracks.itemCount().markInterested();
            for (int t = 0; t < MAX_TRACKS; t++) {
                Track track = (Track) tracks.getItemAt(t);
                trackItems[t] = track;
                track.exists().markInterested();
                track.name().markInterested();
                track.trackType().markInterested();
                track.isGroup().markInterested();
                track.color().markInterested();
                track.isActivated().markInterested();
                track.mute().markInterested();
                track.solo().markInterested();
                track.arm().markInterested();
                track.volume().markInterested();
                track.volume().displayedValue().markInterested();
                track.pan().markInterested();
                track.pan().displayedValue().markInterested();
                BooleanValue selected = track.createEqualsValue(cursorTrack);
                selected.markInterested();
                trackSelected[t] = selected;

                DeviceBank devices = track.createDeviceBank(MAX_DEVICES);
                devices.itemCount().markInterested();
                for (int d = 0; d < MAX_DEVICES; d++) watchDevice((Device) devices.getItemAt(d));
                trackDevices[t] = devices;
                trackStarts[t] = track.startOfDeviceChainInsertionPoint();
                trackEnds[t] = track.endOfDeviceChainInsertionPoint();

                ClipLauncherSlotBank slots = track.clipLauncherSlotBank();
                trackSlots[t] = new ClipLauncherSlot[SCENES];
                slotInserts[t] = new InsertionPoint[SCENES];
                for (int s = 0; s < SCENES; s++) {
                    ClipLauncherSlot slot = (ClipLauncherSlot) slots.getItemAt(s);
                    slot.exists().markInterested();
                    slot.hasContent().markInterested();
                    trackSlots[t][s] = slot;
                    slotInserts[t][s] = slot.replaceInsertionPoint();
                }
            }
        }, () -> tracks.itemCount().get() + " tracks in Bitwig's list; the link sees the first " + MAX_TRACKS + ", with "
                 + MAX_DEVICES + " devices and " + SCENES + " clip slots each.");

        feature("Sampler and Note Transpose pitch", () -> {
            for (int t = 0; t < MAX_TRACKS; t++) {
                Tuning[] tunings = new Tuning[TUNED_DEVICES];
                for (int n = 0; n < TUNED_DEVICES; n++) {
                    Device device = (Device) trackDevices[t].getItemAt(n);
                    tunings[n] = new Tuning(tuning(device, SAMPLER, "TRANSPOSE"), tuning(device, NOTE_TRANSPOSE, "COARSE"));
                }
                trackTunings[t] = tunings;
            }
        }, () -> "Known on the first " + TUNED_DEVICES + " devices of each track.");

        // Newer API, as additions: the app works without them.
        feature("track IDs", () -> {
            for (int t = 0; t < MAX_TRACKS; t++) trackItems[t].channelId().markInterested();
            trackIds = true;
        }, () -> {
            if (!trackItems[0].exists().get()) return "No tracks to show an ID for.";
            String id = str(trackItems[0].channelId().get());
            return id.isEmpty() ? "Bitwig hasn't said the first track's ID." : "The first track's ID is " + id + ".";
        });

        feature("popup browser", () -> {
            popupBrowser = host.createPopupBrowser();
            popupBrowser.exists().markInterested();
        }, () -> popupBrowser.exists().get() ? "Bitwig's browser is open." : "Bitwig's browser is closed.");

        feature("settings", () -> {
            changesSetting = host.getPreferences().getBooleanSetting("Let the app make changes", "Music Organiser", false);
            changesSetting.markInterested();
        }, () -> changesSetting.get() ? "The app may make changes." : "Changes are turned off.");

        feature("remote controls", () -> {
            CursorRemoteControlsPage page = cursorDevice.createCursorRemoteControlsPage(CONTROLS);
            page.getName().markInterested();
            Parameter[] list = new Parameter[CONTROLS];
            for (int i = 0; i < CONTROLS; i++) {
                Parameter control = page.getParameter(i);
                control.exists().markInterested();
                control.name().markInterested();
                control.markInterested();
                control.displayedValue().markInterested();
                list[i] = control;
            }
            controls = list;
            remoteControls = page;
        }, () -> "Page \"" + str(remoteControls.getName().get()) + "\" of the selected device, with "
                 + Arrays.stream(controls).filter(c -> c.exists().get()).count() + " controls.");

        feature("last clicked parameter", () -> {
            LastClickedParameter clicked = host.createLastClickedParameter("MO_CLICKED", "Music Organiser");
            Parameter parameter = clicked.parameter();
            parameter.exists().markInterested();
            parameter.name().markInterested();
            parameter.markInterested();
            parameter.displayedValue().markInterested();
            clicked.isLocked().markInterested();
            clickedParameter = parameter;
            lastClicked = clicked;
        }, () -> clickedParameter.exists().get() ? "\"" + str(clickedParameter.name().get()) + "\" was clicked last."
                                                 : "No parameter has been clicked yet.");

        feature("parameter steps, origins and automation", () -> {
            List<Parameter> all = new ArrayList<>();
            if (controls != null) all.addAll(Arrays.asList(controls));
            if (clickedParameter != null) all.add(clickedParameter);
            for (Parameter parameter : all) {
                parameter.discreteValueCount().markInterested();
                parameter.discreteValueNames().markInterested();
                parameter.getOrigin().markInterested();
                parameter.hasAutomation().markInterested();
            }
            details = !all.isEmpty();
        }, () -> {
            if (!details) throw new LinkError("Neither the remote controls nor the last clicked parameter were made.");
            for (Parameter parameter : controls == null ? new Parameter[0] : controls)
                if (parameter.exists().get())
                    return "\"" + str(parameter.name().get()) + "\": " + steps(parameter.discreteValueCount().get()) + ", origin "
                           + Json.number(parameter.getOrigin().get()) + (parameter.hasAutomation().get() ? ", automated." : ", not automated.");
            return "Read for the remote controls and the last clicked parameter.";
        });

        feature("master recorder", () -> {
            MasterRecorder master = host.createMasterRecorder();
            master.isActive().markInterested();
            master.duration().markInterested();
            recorder = master;
        }, () -> recorder.isActive().get() ? "Recording, for " + recorder.duration().get() + " ms so far." : "Not recording.");

        // For the arrange experiment: the arranger timeline and its clip launcher, the panel layout, the play start, the
        // clip selected in the arranger and which launcher slot is selected.
        feature("arranger", () -> {
            Arranger made = host.createArranger();
            made.isTimelineVisible().markInterested();
            made.isClipLauncherVisible().markInterested();
            app.panelLayout().markInterested();
            transport.playStartPosition().markInterested();
            // The clip's notes aren't read, so its grid can be as small as can be.
            Clip clip = host.createArrangerCursorClip(1, 1);
            clip.exists().markInterested();
            Track on = clip.getTrack();
            on.name().markInterested();
            for (ClipLauncherSlot[] slots : trackSlots) {
                if (slots == null) continue;   // the track bank wasn't made: arrange can't reach a track anyway
                for (ClipLauncherSlot slot : slots) slot.isSelected().markInterested();
            }
            arrangerClipTrack = on;
            arrangerClip = clip;
            arranger = made;
        }, () -> "Bitwig shows its " + str(app.panelLayout().get()) + " layout, with the arranger timeline "
                 + (arranger.isTimelineVisible().get() ? "shown" : "hidden") + " and the Arrange layout's clip launcher "
                 + (arranger.isClipLauncherVisible().get() ? "shown" : "hidden") + "; "
                 + (arrangerClip.exists().get() ? "a clip on \"" + str(arrangerClipTrack.name().get()) + "\" is selected in the arranger"
                                                : "no clip is selected in the arranger")
                 + ", and the play start is at beat " + Json.number(transport.playStartPosition().get()) + ".");

        feature("remote connection", () -> {
            socket = host.createRemoteConnection("Music Organiser", PORT);
            socket.setClientConnectCallback(this::connected);
            port = socket.getPort();
            listening = true;
        }, () -> {
            int now = knownPort();
            if (now > 0 && now != PORT)
                throw new LinkError("Port " + PORT + " is taken, so Bitwig gave the link port " + now + ", where the app doesn't look. " + TWICE);
            if (now <= 0) return "Waiting for the app on port " + PORT + ", though Bitwig hasn't said yet which port it gave the link.";
            return "Waiting for the app on port " + now + ", with " + clients.size() + " connected now.";
        });
        if (listening) {
            host.println("Music Organiser: waiting for the app on port " + (port > 0 ? port : PORT) + ".");
            warnIfPortTaken();
        } else {
            host.showPopupNotification("Music Organiser: Bitwig couldn't listen for the app. The self-check in " + reports.folder()
                                       + " says why.");
        }

        Map<String, Object> start = event("start");
        start.put("extension", MusicOrganiserExtensionDefinition.VERSION);
        if (!loadedFrom.isEmpty()) start.put("loadedFrom", loadedFrom);
        start.put("bitwig", str(host.getHostVersion()));
        start.put("api", host.getHostApiVersion());
        start.put("port", port);
        reports.log(start);
        host.scheduleTask(() -> {
            try {
                selfTest("start");
            } catch (RuntimeException | LinkageError e) {
                host.errorln("Music Organiser: the self-check failed (" + e + ").");
            }
        }, SELF_CHECK_DELAY);
    }

    void flush() {}

    void exit() {
        for (Client client : clients) client.open = false;
        clients.clear();
        jobs.clear();
        reports.log(event("stop"));
    }

    private void watchDevice(Device device) {
        device.exists().markInterested();
        device.name().markInterested();
        device.presetName().markInterested();
        device.isPlugin().markInterested();
        device.deviceType().markInterested();
        device.isEnabled().markInterested();
    }

    /** One of a Bitwig device's parameters, for whenever `device` is that device (by the ID Bitwig saves it under). */
    private Parameter tuning(Device device, String id, String param) {
        Parameter parameter = device.createSpecificBitwigDevice(UUID.fromString(id)).createParameter(param);
        parameter.exists().markInterested();
        parameter.markInterested();
        parameter.displayedValue().markInterested();
        return parameter;
    }

    /** The selected device's parameter IDs changed: forget what's known of parameters it no longer has. */
    private void parameterIds(Object ids) {
        // A String[]; Bitwig's own build of the API has no generic types, so the callback is handed an Object.
        Object[] array = ids instanceof Object[] ? (Object[]) ids : new Object[0];
        List<String> list = new ArrayList<>(array.length);
        for (Object id : array) list.add(String.valueOf(id));
        params.ids = list;
        Set<String> kept = new HashSet<>(list);
        params.names.keySet().retainAll(kept);
        params.values.keySet().retainAll(kept);
        params.displays.keySet().retainAll(kept);
    }

    // ---- Talking to the app ----

    private void connected(RemoteConnection connection) {
        Client client = new Client(connection);
        clients.add(client);
        knownPort();
        connection.setDisconnectCallback(() -> {
            client.open = false;
            clients.remove(client);
            reports.log(event("disconnected"));
        });
        connection.setReceiveCallback(data -> handle(client, Framing.text(data)));
        reports.log(event("connected"));
        Map<String, Object> hello = new LinkedHashMap<>();
        hello.put("event", "hello");
        hello.put("result", greeting());
        send(client, hello);
    }

    /**
     * The port Bitwig gave the link, asked again while Bitwig hasn't said: Bitwig 6.1.3 says -1 straight after making
     * the connection. The app connecting shows it's 19791, the only port the app tries. Warns in Bitwig when it's
     * another.
     */
    private int knownPort() {
        if (port <= 0 && socket != null) {
            port = socket.getPort();
            if (port <= 0 && !clients.isEmpty()) port = PORT;
        }
        warnIfPortTaken();
        return port;
    }

    private void warnIfPortTaken() {
        if (port <= 0 || port == PORT || portWarned) return;
        portWarned = true;
        host.showPopupNotification("Music Organiser: port " + PORT + " is taken, so the app can't connect. " + TWICE);
    }

    /** Handles one request from the app. */
    void handle(Client client, String text) {
        long started = clock.millis();
        Object request;
        try {
            request = Json.parse(text);
        } catch (Json.JsonException e) {
            reply(new Ask(client, null, null, started), null, "The request isn't JSON: " + text.substring(0, Math.min(80, text.length())));
            return;
        }
        Map<String, Object> map = asMap(request);
        Object id = map != null && map.get("id") != null ? map.get("id") : null;
        // What JavaScript's request.cmd was, for the message: undefined unless the request is an object.
        Object cmd = map != null ? field(map, "cmd") : Json.truthy(request) ? Json.UNDEFINED : request;
        Ask ask = new Ask(client, id, logName(cmd), started);
        try {
            Command command = cmd instanceof String ? commands.get(cmd) : null;
            if (map == null || command == null) throw new LinkError("Unknown command: " + Json.js(cmd));
            checkAllowed(map, command.changes());
            if (command.job() != null) {
                jobs.add(new Job(ask, map, command.job(), command.changes()));
                if (currentJob == null) nextJob();
            } else {
                command.run().run(ask, map);
            }
        } catch (RuntimeException | LinkageError e) {
            reply(ask, null, failure(e));
        }
    }

    /**
     * Refuses a request that may not go ahead now. A request that carries `onlyIn` (a project's name) only goes ahead
     * while Bitwig has that project open, and a change only while "Let the app make changes" is on. This is checked
     * when a request arrives, when a change starts (it may have waited behind others) and before each step of a change
     * after a wait, since the track bank, the cursor track and the cursor device follow whichever project is in front,
     * and someone may bring their own to the front, or turn changes off, meanwhile.
     */
    private void checkAllowed(Map<String, Object> request, boolean changes) {
        Object only = field(request, "onlyIn");
        if (!absent(only)) {
            if (!(only instanceof String)) throw new LinkError("onlyIn has to be a project's name.");
            String open = str(app.projectName().get());
            if (!open.equals(only))
                throw new LinkError("Bitwig has \"" + open + "\" open, not \"" + only + "\", so the link stopped before changing anything there.");
        }
        if (changes && !changesSetting.get()) throw new LinkError(CHANGES_OFF);
    }

    /** Runs the next change waiting, if any. */
    private void nextJob() {
        Job job = jobs.poll();
        currentJob = job;
        if (job == null) return;
        Finish finish = new Finish() {
            private boolean finished;

            @Override
            public void done(Object result, String error) {
                if (finished) return;
                finished = true;
                reply(job.ask(), result, error);
                nextJob();
            }
        };
        attempt(() -> {
            checkAllowed(job.request(), job.changes());
            job.run().run(job.request(), finish);
        }, finish);
    }

    /**
     * Before a step of the change running now that comes after a wait: stops the change (with an error for `attempt`
     * to hand to its finish) unless it may still go ahead. Every step after a wait goes through waitFor or settle,
     * which call this, so nothing a change does after a wait can reach another project.
     */
    private void carryOn(Job job) {
        if (job != null) checkAllowed(job.request(), job.changes());
    }

    /** The change running now, or null (the tests' stand-in Bitwig watches for it). */
    Job runningJob() {
        return currentJob;
    }

    /** Runs one step of a change, ending the change with an error if the step fails. */
    private void attempt(Runnable step, Finish finish) {
        try {
            step.run();
        } catch (RuntimeException | LinkageError e) {
            finish.done(null, failure(e));
        }
    }

    private String failure(Throwable e) {
        if (e instanceof LinkError) return e.getMessage();
        StringWriter trace = new StringWriter();
        e.printStackTrace(new PrintWriter(trace));
        host.errorln("Music Organiser: " + trace);
        return "The Bitwig extension failed: " + e;
    }

    /**
     * Bitwig makes changes after the extension returns, and tells it a moment later, so a change waits: `test`
     * is checked every 50 ms until it's true or `ms` have passed, and `then` gets whether it came true. Each time,
     * the change stops instead if it may no longer go ahead (see carryOn).
     */
    private void waitFor(BooleanSupplier test, long ms, Then then, Finish finish) {
        long start = clock.millis();
        Job job = currentJob;
        Runnable check = new Runnable() {
            @Override
            public void run() {
                if (job != currentJob) return;   // a step left over from a change that has ended does nothing
                attempt(() -> {
                    carryOn(job);
                    boolean ok = test.getAsBoolean();
                    if (ok || clock.millis() - start >= ms) then.run(ok);
                    else host.scheduleTask(this, 50);
                }, finish);
            }
        };
        host.scheduleTask(check, 50);
    }

    /**
     * Waits until what `look` sees stops changing (the same three times running), since Bitwig reports a change
     * bit by bit, then hands it to `then`. Each time, the change stops instead if it may no longer go ahead (see
     * carryOn).
     */
    private <T> void settle(Supplier<T> look, Consumer<T> then, Finish finish) {
        long start = clock.millis();
        Job job = currentJob;
        Runnable check = new Runnable() {
            private String last;
            private int same;

            @Override
            public void run() {
                if (job != currentJob) return;   // a step left over from a change that has ended does nothing
                attempt(() -> {
                    carryOn(job);
                    T seen = look.get();
                    String text = Json.write(seen);
                    same = text.equals(last) ? same + 1 : 0;
                    last = text;
                    if (same >= 2 || clock.millis() - start >= CHANGE_WAIT) then.accept(seen);
                    else host.scheduleTask(this, 50);
                }, finish);
            }
        };
        host.scheduleTask(check, 50);
    }

    /**
     * Answers a request: with its result, or with an error message for the app. Each answer goes in the log, apart from
     * a hello that went well: the app says hello every couple of seconds to see Bitwig is there, which would fill the
     * log with tens of thousands of lines a day.
     */
    private void reply(Ask ask, Object result, String error) {
        Map<String, Object> message = new LinkedHashMap<>();
        message.put("id", ask.id());
        message.put("ok", error == null);
        if (error != null) message.put("error", error);
        else message.put("result", result);
        send(ask.client(), message);
        if (error == null && "hello".equals(ask.cmd())) return;

        Map<String, Object> line = new LinkedHashMap<>();
        line.put("cmd", ask.cmd());
        line.put("ms", clock.millis() - ask.started());
        line.put("ok", error == null);
        if (error != null) line.put("error", error);
        reports.log(line);
    }

    private void send(Client client, Map<String, Object> message) {
        if (!client.open) return;
        try {
            client.connection.send(Framing.message(Json.write(message)));
        } catch (Exception e) {   // Exception: some builds of the API declare an IOException here
            client.open = false;
            clients.remove(client);
            host.errorln("Music Organiser: lost the connection to the app (" + e + ").");
        }
    }

    private static Map<String, Object> event(String name) {
        Map<String, Object> line = new LinkedHashMap<>();
        line.put("event", name);
        return line;
    }

    /** The command as the log shows it: short, and only what the app sent. */
    private static String logName(Object cmd) {
        if (cmd == Json.UNDEFINED) return null;
        String name = cmd instanceof String ? (String) cmd : Json.js(cmd);
        return name.length() > 40 ? name.substring(0, 40) : name;
    }

    // ---- What the app can ask for ----

    private Map<String, Object> hello() {
        Map<String, Object> m = new LinkedHashMap<>();
        m.put("link", LINK_VERSION);
        m.put("product", str(host.getHostProduct()));
        m.put("version", str(host.getHostVersion()));
        m.put("api", host.getHostApiVersion());
        m.put("platform", str(host.getPlatformType()));
        m.put("port", port);
        m.put("project", str(app.projectName().get()));
        m.put("modified", project.isModified().get());
        m.put("engine", app.hasActiveEngine().get());
        m.put("changes", changesSetting.get());
        // This link honours a request's onlyIn (see checkAllowed).
        m.put("onlyIn", true);
        // Additions in link version 3. ("engine" already says whether Bitwig's audio engine is on.)
        m.put("runtime", "java");
        m.put("extension", MusicOrganiserExtensionDefinition.VERSION);
        if (!loadedFrom.isEmpty()) m.put("loadedFrom", loadedFrom);
        return m;
    }

    /** The file a class was loaded from, when it's a file (an archive such as the .bwextension), else "". */
    static String loadedFrom(Class<?> type) {
        try {
            CodeSource source = type.getProtectionDomain().getCodeSource();
            if (source == null || source.getLocation() == null) return "";
            Path file = Path.of(uri(source.getLocation()));
            return Files.isRegularFile(file) ? file.toString() : "";
        } catch (URISyntaxException | RuntimeException e) {
            // Not a file: URL, or Bitwig doesn't allow asking.
            return "";
        }
    }

    /**
     * A URL as a URI. Bitwig makes its extensions' URLs from their files without escaping them (File.toURL), so one in a
     * folder with a space in its name ("Bitwig Studio") isn't a URI as it stands: its parts are escaped here instead.
     */
    static URI uri(URL url) throws URISyntaxException {
        try {
            return url.toURI();
        } catch (URISyntaxException e) {
            String authority = url.getAuthority();
            return new URI(url.getProtocol(), authority == null || authority.isEmpty() ? null : authority, url.getPath(), null, null);
        }
    }

    /** The greeting for a new connection: what's known, even when part of Bitwig's API didn't start. */
    private Map<String, Object> greeting() {
        try {
            return hello();
        } catch (RuntimeException | LinkageError e) {
            Map<String, Object> m = new LinkedHashMap<>();
            m.put("link", LINK_VERSION);
            m.put("product", str(host.getHostProduct()));
            m.put("version", str(host.getHostVersion()));
            m.put("api", host.getHostApiVersion());
            m.put("platform", str(host.getPlatformType()));
            m.put("port", port);
            m.put("onlyIn", true);
            m.put("runtime", "java");
            m.put("extension", MusicOrganiserExtensionDefinition.VERSION);
            if (!loadedFrom.isEmpty()) m.put("loadedFrom", loadedFrom);
            m.put("error", failure(e) + " Ask for a selftest to see what's wrong.");
            return m;
        }
    }

    private Map<String, Object> describeProject() {
        List<Object> list = new ArrayList<>();
        for (int t = 0; t < MAX_TRACKS; t++) {
            Track track = trackItems[t];
            if (track.exists().get()) list.add(describeTrack(t, track, trackDevices[t]));
        }
        Map<String, Object> m = new LinkedHashMap<>();
        m.put("name", str(app.projectName().get()));
        m.put("modified", project.isModified().get());
        m.put("tempo", transport.tempo().getRaw());
        m.put("timeSignature", str(transport.timeSignature().get()));
        m.put("playing", transport.isPlaying().get());
        m.put("position", transport.getPosition().get());
        m.put("trackCount", tracks.itemCount().get());
        m.put("tracks", list);
        return m;
    }

    private Map<String, Object> describeTrack(int index, Track track, DeviceBank devices) {
        List<Object> list = new ArrayList<>();
        for (int d = 0; d < MAX_DEVICES; d++) {
            Device device = (Device) devices.getItemAt(d);
            if (device.exists().get()) list.add(describeDevice(d, device));
        }
        Map<String, Object> m = new LinkedHashMap<>();
        m.put("index", index);
        // The ID Bitwig keeps the track under (link version 3), when it has said.
        String id = trackIds ? str(track.channelId().get()) : "";
        if (!id.isEmpty()) m.put("id", id);
        m.put("name", str(track.name().get()));
        m.put("type", str(track.trackType().get()));
        m.put("group", track.isGroup().get());
        m.put("color", colour(track.color()));
        m.put("active", track.isActivated().get());
        m.put("mute", track.mute().get());
        m.put("solo", track.solo().get());
        m.put("arm", track.arm().get());
        m.put("volume", str(track.volume().displayedValue().get()));
        m.put("volumeValue", track.volume().get());
        m.put("pan", str(track.pan().displayedValue().get()));
        m.put("panValue", track.pan().get());
        m.put("deviceCount", devices.itemCount().get());
        m.put("devices", list);
        return m;
    }

    private static Map<String, Object> describeDevice(int index, Device device) {
        Map<String, Object> m = new LinkedHashMap<>();
        m.put("index", index);
        m.put("name", str(device.name().get()));
        m.put("preset", str(device.presetName().get()));
        m.put("kind", str(device.deviceType().get()));
        m.put("plugin", device.isPlugin().get());
        m.put("enabled", device.isEnabled().get());
        return m;
    }

    /**
     * The selected device with all its parameters. Bitwig sends the display texts ("-6.0 dB") only once they're
     * asked for, so the reply waits for them (up to DEVICE_WAIT).
     */
    private void requestDevice(Ask ask, Map<String, Object> request) {
        if (!cursorDevice.exists().get()) {
            Map<String, Object> m = new LinkedHashMap<>();
            m.put("track", str(cursorTrack.name().get()));
            m.put("device", null);
            m.put("params", List.of());
            m.put("drumPads", false);
            reply(ask, m, null);
            return;
        }
        deviceWaiters.add(ask);
        if (deviceWaiters.size() > 1) return;   // already waiting: this request gets the same answer
        try {
            params.displays.clear();
            displayObserver.setObservedParameterIds(params.ids.toArray(new String[0]));
            deviceWaitStart = clock.millis();
            host.scheduleTask(this::checkDevice, 50);
        } catch (RuntimeException | LinkageError e) {
            deviceWaiters.clear();   // so the next request doesn't wait behind this one for ever
            throw e;
        }
    }

    private void checkDevice() {
        int missing = 0;
        for (String id : params.ids)
            if (!params.displays.containsKey(id)) missing++;
        if (missing > 0 && clock.millis() - deviceWaitStart < DEVICE_WAIT) {
            host.scheduleTask(this::checkDevice, 50);
            return;
        }
        Map<String, Object> result = null;
        String error = null;
        try {
            result = describeSelectedDevice();
            displayObserver.setObservedParameterIds(null);
        } catch (RuntimeException | LinkageError e) {
            // Every waiting request still gets an answer, and the next one starts afresh.
            error = failure(e);
        }
        List<Ask> waiters = new ArrayList<>(deviceWaiters);
        deviceWaiters.clear();
        for (Ask waiter : waiters) reply(waiter, result, error);
    }

    private Map<String, Object> describeSelectedDevice() {
        // Which remote control (if any) is each parameter: Bitwig's API tells steps, origins and automation only
        // for those, and doesn't say which parameter a control is, so a control is matched to the one parameter
        // with its name and value.
        Map<String, Parameter> controlOf = new HashMap<>();
        Map<Parameter, String> paramOf = new HashMap<>();
        if (controls != null) {
            for (Parameter control : controls) {
                if (!control.exists().get()) continue;
                String match = null;
                int found = 0;
                for (String id : params.ids) {
                    if (!str(control.name().get()).equals(params.names.get(id))) continue;
                    Double value = params.values.get(id);
                    double v = control.get();
                    if (value == null || !(Math.abs(value - v) < 1e-4 || (value.isNaN() && Double.isNaN(v)))) continue;
                    match = id;
                    found++;
                }
                if (found == 1 && !controlOf.containsKey(match)) {
                    controlOf.put(match, control);
                    paramOf.put(control, match);
                }
            }
        }

        List<Object> list = new ArrayList<>();
        for (String id : params.ids) {
            Map<String, Object> p = new LinkedHashMap<>();
            p.put("id", id);
            p.put("name", params.names.getOrDefault(id, ""));
            p.put("value", params.values.get(id));   // NaN (not readable) becomes null in JSON
            p.put("display", params.displays.get(id));
            Parameter control = controlOf.get(id);
            if (control != null) addDetails(p, control);
            list.add(p);
        }
        boolean exists = cursorDevice.exists().get();
        Map<String, Object> m = new LinkedHashMap<>();
        m.put("track", str(cursorTrack.name().get()));
        m.put("device", exists ? describeDevice(-1, cursorDevice) : null);
        m.put("params", list);
        m.put("drumPads", exists && cursorDevice.hasDrumPads().get());
        if (remoteControls != null) {
            // Link version 3: the device's remote controls on the page Bitwig shows, with what's known of each.
            List<Object> shown = new ArrayList<>();
            for (int i = 0; i < CONTROLS; i++) {
                Parameter control = controls[i];
                if (!control.exists().get()) continue;
                Map<String, Object> c = new LinkedHashMap<>();
                c.put("index", i);
                c.put("name", str(control.name().get()));
                c.put("value", control.get());
                c.put("display", str(control.displayedValue().get()));
                addDetails(c, control);
                c.put("param", paramOf.get(control));
                shown.add(c);
            }
            m.put("page", str(remoteControls.getName().get()));
            m.put("controls", shown);
        }
        return m;
    }

    /** A parameter's steps (-1 when it's continuous), their names, its origin and whether it's automated. */
    private void addDetails(Map<String, Object> m, Parameter parameter) {
        if (!details) return;
        try {
            int steps = parameter.discreteValueCount().get();
            String[] names = parameter.discreteValueNames().get();
            double origin = parameter.getOrigin().get();
            boolean automated = parameter.hasAutomation().get();
            m.put("steps", steps);
            if (steps > 0 && names != null && names.length > 0) m.put("stepNames", Arrays.asList(names));
            m.put("origin", origin);
            m.put("automated", automated);
        } catch (RuntimeException e) {
            // Best effort: the rest of the answer matters more.
        }
    }

    private List<Object> listActions() {
        Action[] all = app.getActions();
        List<Object> list = new ArrayList<>();
        for (Action action : all) {
            ActionCategory category = action.getCategory();
            Map<String, Object> m = new LinkedHashMap<>();
            m.put("id", str(action.getId()));
            m.put("name", str(action.getName()));
            m.put("category", category != null ? str(category.getName()) : "");
            m.put("menu", str(action.getMenuItemText()));
            list.add(m);
        }
        return list;
    }

    /** Selects a track from the project listing (by its index there) and, optionally, one of its devices. */
    private void select(Ask ask, Map<String, Object> request) {
        int t = checkTrack(field(request, "track"));
        Object d = field(request, "device");
        Device device = absent(d) ? null : trackDevice(t, d);
        cursorTrack.selectChannel(trackItems[t]);
        if (device != null) cursorDevice.selectDevice(device);
        reply(ask, Map.of(), null);
    }

    /** Sets one of the selected device's parameters, by its ID, to a value from 0 to 1. */
    private void setParameter(Ask ask, Map<String, Object> request) {
        if (!cursorDevice.exists().get()) throw new LinkError("No device is selected in Bitwig.");
        Object param = field(request, "param");
        if (!(param instanceof String) || !params.ids.contains(param))
            throw new LinkError("The selected device has no parameter " + Json.js(param) + ".");
        Object value = field(request, "value");
        if (!(value instanceof Double) || !((Double) value >= 0 && (Double) value <= 1))
            throw new LinkError("The value has to be a number from 0 to 1.");
        // Bitwig takes the value as a step out of (resolution - 1).
        cursorDevice.setDirectParameterValueNormalized((String) param, Math.round((Double) value * 1000000), 1000001);
        reply(ask, Map.of(), null);
    }

    /** Runs one of Bitwig's actions by its ID (as listed by the actions command), e.g. saving the project. */
    private void runAction(Ask ask, Map<String, Object> request) {
        Object id = field(request, "action");
        Action action = id instanceof String ? app.getAction((String) id) : null;
        if (action == null) throw new LinkError("Bitwig has no action " + Json.js(id) + ".");
        action.invoke();
        reply(ask, Map.of(), null);
    }

    /** The parameter last clicked in Bitwig (link version 3), with what Bitwig says of it. */
    private void clicked(Ask ask, Map<String, Object> request) {
        if (lastClicked == null)
            throw new LinkError("This Bitwig didn't let the link follow the last clicked parameter. A selftest says why.");
        Parameter p = clickedParameter;
        Map<String, Object> m = new LinkedHashMap<>();
        boolean exists = p.exists().get();
        m.put("exists", exists);
        if (exists) {
            m.put("name", str(p.name().get()));
            m.put("value", p.get());
            m.put("display", str(p.displayedValue().get()));
            addDetails(m, p);
        }
        m.put("locked", lastClicked.isLocked().get());
        reply(ask, m, null);
    }

    /** Runs a self-check, writes it to the reports folder and returns it, with the file it went in. */
    private Map<String, Object> selfTest(String why) {
        Map<String, Object> report = selfCheck(why);
        Path file = reports.writeSelfCheck(report);
        report.put("file", file == null ? null : file.toString());
        return report;
    }

    // ---- Changes that wait for Bitwig ----

    /**
     * Inserts something into a device chain: a plugin by its ID (as the app's plugin scanner reads it), a Bitwig
     * device by its ID, or a file (a preset, or a sample, which Bitwig loads into a Sampler). It goes at the end
     * of the selected track's chain, or of `track`'s (at its start with `at: "start"`). With a track, the answer
     * waits to say whether Bitwig added a device, since it quietly ignores what it can't use.
     */
    private void insert(Map<String, Object> request, Finish finish) {
        Object t = field(request, "track");
        Object at = field(request, "at");
        boolean start = "start".equals(at);
        if (!absent(at) && !start && !"end".equals(at)) throw new LinkError("Insert at the \"start\" or the \"end\" of the chain.");
        InsertionPoint point;
        DeviceBank chain = null;
        int before = 0;
        if (absent(t)) {
            if (start) throw new LinkError("Say which track's chain to insert at the start of.");
            if (!cursorTrack.exists().get()) throw new LinkError("No track is selected in Bitwig.");
            point = cursorEnd;
        } else {
            int track = checkTrack(t);
            chain = trackDevices[track];
            before = chain.itemCount().get();
            point = start ? trackStarts[track] : trackEnds[track];
        }
        Object kind = field(request, "kind");
        switch (kind instanceof String ? (String) kind : "\u0000") {
            case "vst3":
                point.insertVST3Device(given(field(request, "plugin"), "plugin ID"));
                break;
            case "clap":
                point.insertCLAPDevice(given(field(request, "plugin"), "plugin ID"));
                break;
            case "vst2":
                point.insertVST2Device(vst2Id(field(request, "plugin")));
                break;
            case "bitwig":
                point.insertBitwigDevice(uuid(given(field(request, "device"), "device ID")));
                break;
            case "file":
                point.insertFile(given(field(request, "path"), "file"));
                break;
            default:
                throw new LinkError("Can't insert a \"" + Json.js(kind) + "\": use vst3, clap, vst2, bitwig or file.");
        }
        if (chain == null) {
            // Bitwig quietly does nothing for an ID it doesn't know; the app checks the device list afterwards.
            finish.ok(Map.of());
            return;
        }
        DeviceBank devices = chain;
        int count = before;
        int index = start ? 0 : before;
        waitFor(() -> devices.itemCount().get() > count, CHANGE_WAIT, added -> {
            if (!added) {
                Map<String, Object> m = new LinkedHashMap<>();
                m.put("added", false);
                m.put("index", null);
                m.put("device", null);
                finish.ok(m);
                return;
            }
            settle(() -> index < MAX_DEVICES && ((Device) devices.getItemAt(index)).exists().get()
                         ? describeDevice(index, (Device) devices.getItemAt(index)) : null,
                   device -> {
                       Map<String, Object> m = new LinkedHashMap<>();
                       m.put("added", true);
                       m.put("index", index);
                       m.put("device", device);
                       finish.ok(m);
                   }, finish);
        }, finish);
    }

    /**
     * Adds an instrument track at the end of the project's tracks (before the effect tracks), names it and
     * selects it, and answers with its index in the project listing.
     */
    private void newTrack(Map<String, Object> request, Finish finish) {
        Object given = field(request, "name");
        if (!absent(given) && !(given instanceof String)) throw new LinkError("A track's name is text.");
        String name = absent(given) ? "" : (String) given;
        Object colour = field(request, "color");
        float[] rgb = absent(colour) ? null : parseColour(colour);
        int count = tracks.itemCount().get();
        List<String> before = trackList();
        app.createInstrumentTrack(-1);
        // The new track shows in the listing where the tracks first differ: Bitwig adds it after the last main
        // track, so the effect tracks and the master move down one.
        waitFor(() -> tracks.itemCount().get() > count && firstDifference(before, trackList()) >= 0, CHANGE_WAIT, shown -> {
            if (!shown && tracks.itemCount().get() > count)
                throw new LinkError("Bitwig added the track past the first " + MAX_TRACKS + " tracks, where the app can't reach it.");
            if (!shown) throw new LinkError("Bitwig didn't add the track.");
            settle(this::trackList, after -> {
                int t = firstDifference(before, after);
                if (t < 0 || !trackItems[t].exists().get()) throw new LinkError("Bitwig didn't add the track.");
                Track track = trackItems[t];
                // In case adding a track opened Bitwig's browser to pick an instrument: the app brings its own.
                if (popupBrowser.exists().get()) popupBrowser.cancel();
                if (!name.isEmpty()) track.name().set(name);
                if (rgb != null) track.color().set(rgb[0], rgb[1], rgb[2]);
                cursorTrack.selectChannel(track);
                waitFor(() -> name.isEmpty() || str(track.name().get()).equals(name), 1000, named -> {
                    Map<String, Object> m = new LinkedHashMap<>();
                    m.put("index", t);
                    m.put("name", str(track.name().get()));
                    finish.ok(m);
                }, finish);
            }, finish);
        }, finish);
    }

    /**
     * Puts a file (a MIDI file, or audio) in one of a track's clip launcher slots: the one given, or the first
     * empty one, and answers once Bitwig has made the clip.
     */
    private void addClip(Map<String, Object> request, Finish finish) {
        int t = checkTrack(field(request, "track"));
        String path = given(field(request, "path"), "file");
        ClipLauncherSlot[] slots = trackSlots[t];
        Object s = field(request, "slot");
        int index;
        if (!absent(s)) {
            if (!isIndex(s, SCENES))
                throw new LinkError("The app can reach the first " + SCENES + " clip slots on a track, not slot " + Json.js(s) + ".");
            index = index(s);
            if (slots[index].hasContent().get()) throw new LinkError("Slot " + index + " on that track already has a clip.");
        } else {
            index = emptySlot(slots);
            if (index < 0) throw new LinkError("The track's first " + SCENES + " clip slots are full.");
        }
        ClipLauncherSlot slot = slots[index];
        slotInserts[t][index].insertFile(path);
        waitFor(() -> slot.hasContent().get(), CHANGE_WAIT, made -> {
            if (!made) throw new LinkError("Bitwig didn't make a clip from " + path + ".");
            Map<String, Object> m = new LinkedHashMap<>();
            m.put("slot", index);
            finish.ok(m);
        }, finish);
    }

    /** The first empty slot in a scene that exists, else the first of the scenes still to come; -1 if none. */
    private static int emptySlot(ClipLauncherSlot[] slots) {
        for (int s = 0; s < slots.length; s++)
            if (slots[s].exists().get() && !slots[s].hasContent().get()) return s;
        for (int n = 0; n < slots.length; n++)
            if (!slots[n].exists().get()) return n;
        return -1;
    }

    /**
     * Puts samples on a Drum Machine's free pads (as if each were dropped on its pad, so each gets a Sampler),
     * leaving pads that have something on them alone. A pad that holds only what a Drum Machine preset put there,
     * a Sampler with no sample, is free too: the sample goes in that Sampler's place, keeping what comes after it.
     * The Drum Machine is `device` on `track` (which this selects), or the selected device.
     */
    private void fillPads(Map<String, Object> request, Finish finish) {
        Object given = field(request, "pads");
        if (!(given instanceof List) || ((List<?>) given).isEmpty()) throw new LinkError("No pads given.");
        List<Pad> list = new ArrayList<>();
        Set<Integer> taken = new HashSet<>();
        for (Object item : (List<?>) given) {
            Map<String, Object> pad = asMap(item);
            Object note = pad == null ? Json.UNDEFINED : field(pad, "note");
            if (pad == null || !isIndex(note, DRUM_PADS)) throw new LinkError("A pad's note is a MIDI note from 0 to 127.");
            if (!taken.add(index(note))) throw new LinkError("Two samples for note " + index(note) + ".");
            String path = given(field(pad, "path"), "sample");
            Object name = field(pad, "name");
            if (!absent(name) && !(name instanceof String)) throw new LinkError("A pad's name is text.");
            list.add(new Pad(index(note), path, absent(name) ? "" : (String) name));
        }
        Object t = field(request, "track");
        Object dGiven = field(request, "device");
        Object d = absent(dGiven) ? (Object) 0.0 : dGiven;
        boolean onTrack = !absent(t);
        int track = -1;
        int position = -1;
        if (onTrack) {
            track = checkTrack(t);
            Device device = trackDevice(track, d);
            position = index(d);
            cursorTrack.selectChannel(trackItems[track]);
            cursorDevice.selectDevice(device);
        } else if (!cursorDevice.exists().get()) {
            throw new LinkError("No device is selected in Bitwig. Select a Drum Machine and send the kit again.");
        }
        int ti = track;
        int di = position;
        waitFor(() -> cursorDevice.exists().get() && cursorDevice.hasDrumPads().get()
                      && (!onTrack || (trackSelected[ti].get() && cursorDevice.position().get() == di)),
                onTrack ? CHANGE_WAIT : 500, ready -> {
            if (!ready)
                throw new LinkError(onTrack ? "Device " + di + " on track " + ti + " isn't a Drum Machine."
                                            : "The selected device isn't a Drum Machine. Select one and send the kit again.");
            drumPads.scrollPosition().set(0);
            waitFor(() -> drumPads.scrollPosition().get() == 0, 2000, scrolled -> {
                if (!scrolled) throw new LinkError("Bitwig didn't show the Drum Machine's pads from the lowest note.");
                // The pads catch up with the selection a moment after it changes.
                settle(this::padList, pads -> fill(list, finish), finish);
            }, finish);
        }, finish);
    }

    /**
     * Drops each sample on its free pad, or in place of the pad's empty Sampler, then waits for the pads to take
     * them and names them. The answer says which samples went in place of an empty Sampler, and what's on each
     * pad it leaves alone.
     */
    private void fill(List<Pad> list, Finish finish) {
        List<Pad> filled = new ArrayList<>();
        Map<Integer, String> replaced = new LinkedHashMap<>();   // note -> the pad's name before
        List<Integer> skipped = new ArrayList<>();
        List<Map<String, Object>> taken = new ArrayList<>();
        for (Pad pad : list) {
            int n = pad.note();
            if (!padTaken(n)) {
                padInserts[n].insertFile(pad.path());
            } else if (emptySampler(n)) {
                replaced.put(n, str(padItems[n].name().get()));
                padReplace[n].insertFile(pad.path());
            } else {
                skipped.add(n);
                taken.add(padContents(n));
                continue;
            }
            filled.add(pad);
        }
        Predicate<Pad> took = pad -> replaced.containsKey(pad.note())
            ? !str(padFirst[pad.note()].sampleName().get()).isEmpty() || !str(padItems[pad.note()].name().get()).equals(replaced.get(pad.note()))
            : padTaken(pad.note());
        waitFor(() -> filled.stream().allMatch(took), CHANGE_WAIT, all -> {
            List<Integer> done = new ArrayList<>();
            List<Integer> failed = new ArrayList<>();
            for (Pad pad : filled) {
                if (!took.test(pad)) {
                    failed.add(pad.note());
                    continue;
                }
                if (!pad.name().isEmpty()) padItems[pad.note()].name().set(pad.name());
                done.add(pad.note());
            }
            Map<String, Object> m = new LinkedHashMap<>();
            m.put("filled", done);
            m.put("replaced", done.stream().filter(replaced::containsKey).collect(Collectors.toList()));
            m.put("skipped", skipped);
            m.put("failed", failed);
            m.put("taken", taken);
            finish.ok(m);
        }, finish);
    }

    /** Whether a pad has a device on it: the pad, its device count and its first device all show it. */
    private boolean padTaken(int note) {
        return padItems[note].exists().get() && padDevices[note].itemCount().get() > 0 && padFirst[note].exists().get();
    }

    /**
     * Whether a pad holds a Sampler with no sample, as a Drum Machine preset can put on every pad (Solomon's
     * default Drum Machine does), still has the name it came with, not a sample's, and has nothing after it but
     * effects: such a pad is free.
     */
    private boolean emptySampler(int note) {
        String first = str(padFirst[note].name().get());
        String name = str(padItems[note].name().get());
        return padTaken(note) && first.equals("Sampler") && str(padFirst[note].sampleName().get()).isEmpty()
               && (name.isEmpty() || name.equals(first)) && onlyEffectsAfter(note);
    }

    /**
     * Whether all a pad has after its first device are audio effects, so nothing else on it makes a sound. A pad with
     * more devices than the link sees ({@link #PAD_DEVICES}) doesn't count.
     */
    private boolean onlyEffectsAfter(int note) {
        int count = padDevices[note].itemCount().get();
        if (count > PAD_DEVICES) return false;
        for (int d = 1; d < count; d++) {
            Device after = (Device) padDevices[note].getItemAt(d);
            if (after.exists().get() && !"audio_to_audio".equals(str(after.deviceType().get()))) return false;
        }
        return true;
    }

    /** What's on a pad: its name, how many devices, the first one's name and its sample, as Bitwig shows them. */
    private Map<String, Object> padContents(int note) {
        Map<String, Object> m = new LinkedHashMap<>();
        m.put("note", note);
        m.put("name", str(padItems[note].name().get()));
        m.put("devices", padDevices[note].itemCount().get());
        m.put("first", str(padFirst[note].name().get()));
        m.put("sample", str(padFirst[note].sampleName().get()));
        return m;
    }

    /**
     * Sets the pitch of a Sampler (its Transpose, in semitones, which can be a fraction) or a Note Transpose
     * (whole semitones) among the first devices on a track, without selecting it, and answers with what Bitwig
     * shows.
     */
    private void transpose(Map<String, Object> request, Finish finish) {
        int t = checkTrack(field(request, "track"));
        Object d = field(request, "device");
        if (!isIndex(d, TUNED_DEVICES))
            throw new LinkError("The app can set the pitch of the first " + TUNED_DEVICES + " devices on a track, not device " + Json.js(d) + ".");
        Device device = trackDevice(t, d);
        Object given = field(request, "semitones");
        if (!(given instanceof Double) || !Double.isFinite((Double) given)) throw new LinkError("Say how many semitones.");
        double semitones = (Double) given;
        int di = index(d);
        Tuning tunings = trackTunings[t][di];
        // A device that was just added is known by its ID a moment after it appears.
        waitFor(() -> tunings.sampler().exists().get() || tunings.transpose().exists().get(), 2000, known -> {
            if (!known) throw new LinkError("Device " + di + " on track " + t + " is neither a Sampler nor a Note Transpose.");
            Parameter param = tunings.sampler().exists().get() ? tunings.sampler() : tunings.transpose();
            param.setRaw(semitones);
            waitFor(() -> Math.abs(param.getRaw() - semitones) < 0.001, 1000, set -> {
                Map<String, Object> m = new LinkedHashMap<>();
                m.put("device", str(device.name().get()));
                m.put("value", param.getRaw());
                m.put("display", str(param.displayedValue().get()));
                finish.ok(m);
            }, finish);
        }, finish);
    }

    /**
     * Starts or stops Bitwig's master recorder, or says what it's doing (link version 3): `do` is "start", "stop"
     * or "status" (the default). Answers with `active` and `duration` (how long it has recorded, in ms).
     */
    private void useRecorder(Map<String, Object> request, Finish finish) {
        if (recorder == null)
            throw new LinkError("This Bitwig didn't let the link use its master recorder. A selftest says why.");
        Object what = field(request, "do");
        String action = absent(what) ? "status" : what instanceof String ? (String) what : "";
        switch (action) {
            case "status":
                finish.ok(recorderState());
                break;
            case "start":
            case "stop": {
                boolean on = action.equals("start");
                if (recorder.isActive().get() != on) {
                    if (on) recorder.start();
                    else recorder.stop();
                }
                waitFor(() -> recorder.isActive().get() == on, CHANGE_WAIT, done -> {
                    if (!done) throw new LinkError(on ? "Bitwig didn't start recording. Is its audio engine on?" : "Bitwig didn't stop recording.");
                    finish.ok(recorderState());
                }, finish);
                break;
            }
            default:
                throw new LinkError("Tell the recorder to \"start\", \"stop\" or give its \"status\", not " + Json.js(what) + ".");
        }
    }

    private Map<String, Object> recorderState() {
        Map<String, Object> m = new LinkedHashMap<>();
        m.put("active", recorder.isActive().get());
        m.put("duration", recorder.duration().get());
        return m;
    }

    /**
     * An experiment (extension 3.6): puts a copy of a launcher clip in Bitwig's arranger, on the clip's own track, `at`
     * beats from the start (where the play start is, unless given). Bitwig's API has no way to put a clip in the arranger
     * (only launcher slots have insertion points), so the link does what a person would: show the Arrange layout with its
     * clip launcher, select the clip, give the launcher the focus, copy, move the play start there, bring the arranger
     * timeline to the front and paste. Bitwig copies and pastes in whichever of its panels has the focus, which the API
     * can't read, so:
     * <ul>
     * <li>the link gives the clip launcher the focus with Bitwig's FOCUS_LAUNCHER action when Bitwig has one, and copies
     * from whichever panel has the focus when it hasn't (`launcherFocus` says how many times it ran, 0 without one);</li>
     * <li>when FOCUS_ARRANGER hid the arranger timeline rather than bringing it to the front (`focus` 2), the timeline had
     * the focus when the link copied, as nothing in between moves it. Bitwig copied from the timeline, not the launcher
     * clip, so the link doesn't paste, and says `copiedFromTimeline`.</li>
     * </ul>
     * Nobody knows yet whether Bitwig does all that for an extension, so once the steps are through, the answer says what
     * Bitwig showed before and after, whether or not it worked: `arranged` is whether the clip selected in the arranger
     * moved to the track after the paste, with the track's launcher clips as they were. Where the clip starts can't be
     * read, so only a look at Bitwig shows whether it went in at `at`. Only the live tests send it, in their own project,
     * so nothing is undone when it doesn't work; and nothing is pasted unless the clip was copied first.
     */
    private void arrange(Map<String, Object> request, Finish finish) {
        if (arranger == null) throw new LinkError("This Bitwig didn't let the link reach its arranger. A selftest says why.");
        int t = checkTrack(field(request, "track"));
        Object s = field(request, "slot");
        if (!isIndex(s, SCENES))
            throw new LinkError("The app can reach the first " + SCENES + " clip slots on a track, not slot " + Json.js(s) + ".");
        int index = index(s);
        Object given = field(request, "at");
        if (!absent(given) && !(given instanceof Double && Double.isFinite((Double) given) && (Double) given >= 0))
            throw new LinkError("Say where in the arranger as beats from the start (0 or more), not " + Json.js(given) + ".");
        ClipLauncherSlot slot = trackSlots[t][index];
        if (!slot.hasContent().get()) throw new LinkError("Slot " + index + " on that track has no clip to put in the arranger.");
        Action focus = app.getAction(FOCUS_ARRANGER);
        if (focus == null)
            throw new LinkError("Bitwig has no action \"" + FOCUS_ARRANGER + "\", which the link runs to bring the arranger timeline to the front.");
        Action launcherFocus = app.getAction(FOCUS_LAUNCHER);
        double at = absent(given) ? transport.playStartPosition().get() : (Double) given;
        Track track = trackItems[t];
        String name = str(track.name().get());
        SettableBooleanValue launcherShown = arranger.isClipLauncherVisible();

        // The answer, in the order the app reads it, filled in as the steps go: first what Bitwig shows before.
        Shown shown = new Shown(launcherShown.get(), arranger.isTimelineVisible().get());
        Map<String, Object> m = new LinkedHashMap<>();
        m.put("arranged", false);
        m.put("copiedFromTimeline", false);
        m.put("at", at);
        m.put("layout", str(app.panelLayout().get()));
        m.put("launcher", shown.launcher);
        m.put("launcherFocus", 0);
        m.put("timeline", shown.timeline);
        m.put("focus", 0);
        m.put("before", arrangerClipState());
        m.put("beforePaste", null);
        m.put("after", null);
        int launcherBefore = launcherClips(t);
        m.put("launcherClipsBefore", launcherBefore);
        m.put("launcherClipsAfter", null);

        // The Arrange layout, with its clip launcher showing.
        if (!arrangeLayout()) app.setPanelLayout(Application.PANEL_LAYOUT_ARRANGE);
        waitFor(this::arrangeLayout, CHANGE_WAIT, arranging -> {
            if (!arranging) throw new LinkError("Bitwig didn't switch to its Arrange layout.");
            if (!launcherShown.get()) launcherShown.set(true);
            waitFor(launcherShown::get, CHANGE_WAIT, showing -> {
                if (!showing) throw new LinkError("Bitwig didn't show the clip launcher in its Arrange layout.");
                // The clip, selected in the launcher, which has the focus as the link copies (as far as it can tell).
                cursorTrack.selectChannel(track);
                slot.select();
                waitFor(() -> trackSelected[t].get() && slot.isSelected().get(), CHANGE_WAIT, selected -> {
                    if (!selected) throw new LinkError("Bitwig didn't select the clip in slot " + index + " of track " + t + ".");
                    Runnable copy = () -> {
                        app.copy();
                        transport.playStartPosition().set(at);
                        waitFor(() -> Math.abs(transport.playStartPosition().get() - at) < 1e-6, CHANGE_WAIT, moved -> {
                            if (!moved) throw new LinkError("Bitwig didn't move the play start to beat " + Json.number(at) + ".");
                            focusPanel(focus, arranger.isTimelineVisible(), "arranger timeline", m, "focus", times -> {
                                if (times == 1) {
                                    paste(t, name, launcherBefore, m, shown, finish);
                                    return;
                                }
                                // It hid the timeline, which only happens when the timeline is in front: so it had the focus
                                // as the link copied, and Bitwig's clipboard doesn't hold the launcher clip.
                                m.put("copiedFromTimeline", true);
                                m.put("beforePaste", arrangerClipState());
                                answer(t, m, shown, finish);
                            }, finish);
                        }, finish);
                    };
                    if (launcherFocus == null) copy.run();
                    else focusPanel(launcherFocus, launcherShown, "clip launcher", m, "launcherFocus", times -> copy.run(), finish);
                }, finish);
            }, finish);
        }, finish);
    }

    /** Whether Bitwig showed its clip launcher (in the Arrange layout) and arranger timeline, before and after. */
    private static final class Shown {
        final Map<String, Object> launcher = new LinkedHashMap<>();
        final Map<String, Object> timeline = new LinkedHashMap<>();

        Shown(boolean launcherBefore, boolean timelineBefore) {
            launcher.put("before", launcherBefore);
            launcher.put("after", null);
            timeline.put("before", timelineBefore);
            timeline.put("after", null);
        }
    }

    private boolean arrangeLayout() {
        return Application.PANEL_LAYOUT_ARRANGE.equals(str(app.panelLayout().get()));
    }

    /**
     * Brings one of Bitwig's panels (`what`, which `visible` says is shown or not) to the front with Bitwig's
     * focus-or-toggle `action` for it, then hands `then` how many times the action ran, which the answer keeps under
     * `key`. Such an action hides its panel when that's in front already, so when it does, the link runs it again to
     * show the panel, in front.
     */
    private void focusPanel(Action action, BooleanValue visible, String what, Map<String, Object> m, String key, IntConsumer then,
                            Finish finish) {
        boolean shown = visible.get();
        action.invoke();
        m.put(key, 1);
        // Hidden, the panel comes into view. Shown, it stays (now in front) unless it was in front already: then it hides,
        // which the link gives it a moment to do.
        waitFor(() -> visible.get() != shown, shown ? TOGGLE_WAIT : CHANGE_WAIT, changed -> {
            if (!shown && !changed) throw new LinkError("Bitwig didn't show its " + what + ".");
            if (!shown || !changed) {
                then.accept(1);
                return;
            }
            action.invoke();
            m.put(key, 2);
            waitFor(visible::get, CHANGE_WAIT, again -> {
                if (!again) throw new LinkError("Bitwig hid its " + what + ", which was in front already, and didn't show it again.");
                then.accept(2);
            }, finish);
        }, finish);
    }

    /**
     * Pastes what the link copied, then waits for the clip selected in the arranger to show up on the track called `name`
     * (where it wasn't just before), or for the track's launcher clips to change (Bitwig pasted there instead), and
     * answers with what Bitwig shows once it settles. When neither happens, it answers after CHANGE_WAIT all the same.
     */
    private void paste(int t, String name, int launcherBefore, Map<String, Object> m, Shown shown, Finish finish) {
        m.put("beforePaste", arrangerClipState());
        boolean there = clipOn(name);
        app.paste();
        waitFor(() -> (!there && clipOn(name)) || launcherClips(t) != launcherBefore, CHANGE_WAIT, seen -> {
            // Bitwig reports a change bit by bit, so what it did is read once it stops changing.
            settle(() -> List.of(arrangerClipState(), launcherClips(t)), settled -> {
                m.put("arranged", !there && clipOn(name) && launcherClips(t) == launcherBefore);
                answer(t, m, shown, finish);
            }, finish);
        }, finish);
    }

    /** Answers arrange with what Bitwig shows now, after the paste or without one. */
    private void answer(int t, Map<String, Object> m, Shown shown, Finish finish) {
        shown.launcher.put("after", arranger.isClipLauncherVisible().get());
        shown.timeline.put("after", arranger.isTimelineVisible().get());
        m.put("after", arrangerClipState());
        m.put("launcherClipsAfter", launcherClips(t));
        finish.ok(m);
    }

    /** The clip selected in Bitwig's arranger: whether there is one, and the name of the track it's on (null without one). */
    private Map<String, Object> arrangerClipState() {
        boolean exists = arrangerClip.exists().get();
        Map<String, Object> m = new LinkedHashMap<>();
        m.put("exists", exists);
        m.put("track", exists ? str(arrangerClipTrack.name().get()) : null);
        return m;
    }

    /** Whether the clip selected in Bitwig's arranger is on the track called `name`. */
    private boolean clipOn(String name) {
        return arrangerClip.exists().get() && str(arrangerClipTrack.name().get()).equals(name);
    }

    /** How many of a track's launcher slots (those the link sees) have a clip. */
    private int launcherClips(int t) {
        int count = 0;
        for (ClipLauncherSlot slot : trackSlots[t])
            if (slot.hasContent().get()) count++;
        return count;
    }

    // ---- Self-check ----

    /**
     * Sets up one part of Bitwig's API in init, timing it and keeping whatever goes wrong, so one part that
     * fails (in a Bitwig that changed) leaves the others working. `probe` reads it back for the self-check.
     */
    private void feature(String name, Runnable create, Supplier<String> probe) {
        long start = clock.millis();
        String error = null;
        try {
            create.run();
        } catch (RuntimeException | LinkageError e) {
            error = String.valueOf(e);
            host.errorln("Music Organiser: couldn't set up the " + name + " (" + e + ").");
        }
        features.add(new Feature(name, error, clock.millis() - start, probe));
    }

    /** Checks every part of Bitwig's API the link uses or offers, each on its own, and says how it went. */
    Map<String, Object> selfCheck(String why) {
        List<Object> checks = new ArrayList<>();
        check(checks, "API version", 0, () -> {
            int api = host.getHostApiVersion();
            if (api < MusicOrganiserExtensionDefinition.API_VERSION)
                throw new LinkError("Bitwig offers API " + api + "; the link needs " + MusicOrganiserExtensionDefinition.API_VERSION + ".");
            return "Bitwig offers API " + api + "; the link uses " + MusicOrganiserExtensionDefinition.API_VERSION + ".";
        });
        for (Feature f : features) {
            if (f.error() != null) checks.add(entry(f.name(), false, "Couldn't set it up: " + f.error(), f.ms()));
            else check(checks, f.name(), f.ms(), f.probe());
        }
        check(checks, "actions", 0, () -> app.getActions().length + " actions.");
        check(checks, "reports folder", 0, () -> {
            String why2 = reports.checkWritable();
            if (why2 != null) throw new LinkError(why2);
            String problem = reports.problem();
            return "Writing to " + reports.folder() + (problem == null ? "." : ". Earlier: " + problem);
        });
        boolean ok = true;
        for (Object c : checks) ok &= Boolean.TRUE.equals(((Map<?, ?>) c).get("ok"));

        Map<String, Object> m = new LinkedHashMap<>();
        m.put("time", reports.now());
        m.put("why", why);
        m.put("ok", ok);
        m.put("extension", MusicOrganiserExtensionDefinition.VERSION);
        if (!loadedFrom.isEmpty()) m.put("loadedFrom", loadedFrom);
        m.put("link", LINK_VERSION);
        m.put("product", safely(() -> str(host.getHostProduct())));
        m.put("bitwig", safely(() -> str(host.getHostVersion())));
        m.put("api", safely(host::getHostApiVersion));
        m.put("apiUsed", MusicOrganiserExtensionDefinition.API_VERSION);
        m.put("java", System.getProperty("java.version"));
        m.put("javaVendor", System.getProperty("java.vendor"));
        m.put("os", System.getProperty("os.name") + " " + System.getProperty("os.version"));
        m.put("platform", safely(() -> str(host.getPlatformType())));
        m.put("changes", safely(() -> changesSetting.get()));
        m.put("port", port);
        m.put("connections", clients.size());
        m.put("folder", reports.folder().toString());
        m.put("checks", checks);
        return m;
    }

    private void check(List<Object> checks, String name, long setupMs, Supplier<String> probe) {
        long start = clock.millis();
        try {
            String detail = probe.get();
            checks.add(entry(name, true, detail, setupMs + clock.millis() - start));
        } catch (RuntimeException | LinkageError e) {
            checks.add(entry(name, false, e instanceof LinkError ? e.getMessage() : String.valueOf(e), setupMs + clock.millis() - start));
        }
    }

    private static Map<String, Object> entry(String name, boolean ok, String detail, long ms) {
        Map<String, Object> m = new LinkedHashMap<>();
        m.put("name", name);
        m.put("ok", ok);
        m.put("detail", detail);
        m.put("ms", ms);
        return m;
    }

    /** A value for the self-check, or null when reading it fails (the checks say why). */
    private static Object safely(Supplier<Object> read) {
        try {
            return read.get();
        } catch (RuntimeException | LinkageError e) {
            return null;
        }
    }

    // ---- Helpers ----

    private int checkTrack(Object t) {
        if (!isIndex(t, MAX_TRACKS) || !trackItems[index(t)].exists().get())
            throw new LinkError("There's no track " + Json.js(t) + " in the project listing.");
        return index(t);
    }

    private Device trackDevice(int t, Object d) {
        if (!isIndex(d, MAX_DEVICES) || !((Device) trackDevices[t].getItemAt(index(d))).exists().get())
            throw new LinkError("Track " + t + " has no device " + Json.js(d) + ".");
        return (Device) trackDevices[t].getItemAt(index(d));
    }

    /** Every track in the listing as "name|type" ("" where none), to tell which one is new. */
    private List<String> trackList() {
        List<String> list = new ArrayList<>(MAX_TRACKS);
        for (Track track : trackItems)
            list.add(track.exists().get() ? str(track.name().get()) + "|" + str(track.trackType().get()) : "");
        return list;
    }

    /**
     * Where the pad list starts, and what's on each pad (how many devices, less than 0 when the first is an empty
     * Sampler), to tell when it's caught up.
     */
    private List<Integer> padList() {
        List<Integer> list = new ArrayList<>(DRUM_PADS + 1);
        list.add(drumPads.scrollPosition().get());
        for (int p = 0; p < DRUM_PADS; p++)
            list.add(!padTaken(p) ? 0 : emptySampler(p) ? -padDevices[p].itemCount().get() : padDevices[p].itemCount().get());
        return list;
    }

    /** The first place two lists differ, or -1 if they don't. */
    static int firstDifference(List<?> a, List<?> b) {
        for (int i = 0; i < Math.max(a.size(), b.size()); i++) {
            Object x = i < a.size() ? a.get(i) : null;
            Object y = i < b.size() ? b.get(i) : null;
            if (x == null ? y != null : !x.equals(y)) return i;
        }
        return -1;
    }

    /** "#rrggbb" as red, green and blue from 0 to 1. */
    static float[] parseColour(Object text) {
        Matcher m = text instanceof String ? COLOUR.matcher((String) text) : null;
        if (m == null || !m.matches()) throw new LinkError("A colour is written #rrggbb.");
        return new float[] {
            Integer.parseInt(m.group(1), 16) / 255f, Integer.parseInt(m.group(2), 16) / 255f, Integer.parseInt(m.group(3), 16) / 255f
        };
    }

    private static UUID uuid(String text) {
        try {
            return UUID.fromString(text);
        } catch (IllegalArgumentException e) {
            throw new LinkError("A Bitwig device's ID is a UUID, such as " + SAMPLER + ", not " + text + ".");
        }
    }

    /** A VST2 plugin's ID: a number, written as Bitwig keeps it (a 32-bit whole number, as signed or not). */
    private static int vst2Id(Object plugin) {
        if (!(plugin instanceof Double)) throw new LinkError("A VST2 plugin's ID is a number.");
        double id = (Double) plugin;
        if (id != Math.rint(id) || id < Integer.MIN_VALUE || id > 0xFFFFFFFFL)
            throw new LinkError("A VST2 plugin's ID is a whole number that fits in 32 bits, not " + Json.js(plugin) + ".");
        return (int) (long) id;
    }

    static boolean isIndex(Object value, int count) {
        if (!(value instanceof Double)) return false;
        double v = (Double) value;
        return v >= 0 && v < count && Math.floor(v) == v;
    }

    /** An index that {@link #isIndex} has said is one. */
    private static int index(Object value) {
        return (int) (double) (Double) value;
    }

    private static String given(Object value, String what) {
        if (!(value instanceof String) || ((String) value).isEmpty()) throw new LinkError("No " + what + " given.");
        return (String) value;
    }

    /** A request's field, or {@link Json#UNDEFINED} if it doesn't have it. */
    private static Object field(Map<String, Object> map, String key) {
        return map.containsKey(key) ? map.get(key) : Json.UNDEFINED;
    }

    /** Whether a field is missing or null, which the link treats alike. */
    private static boolean absent(Object value) {
        return value == null || value == Json.UNDEFINED;
    }

    @SuppressWarnings("unchecked")
    private static Map<String, Object> asMap(Object value) {
        return value instanceof Map ? (Map<String, Object>) value : null;
    }

    static String str(Object value) {
        return value == null ? "" : String.valueOf(value);
    }

    private static String steps(int count) {
        return count < 0 ? "continuous" : count + " steps";
    }

    static String colour(SettableColorValue c) {
        return "#" + hex(c.red()) + hex(c.green()) + hex(c.blue());
    }

    private static String hex(float v) {
        int n = (int) Math.max(0, Math.min(255, Math.round(v * 255.0)));
        return (n < 16 ? "0" : "") + Integer.toHexString(n);
    }

    // ---- Types ----

    /** A connection from the app. */
    static final class Client {
        final RemoteConnection connection;
        boolean open = true;

        Client(RemoteConnection connection) {
            this.connection = connection;
        }
    }

    /** A request being answered: who asked, its id, and what and when, for the log. */
    record Ask(Client client, Object id, String cmd, long started) {}

    /** Ends a change: with its result, or with an error message for the app. */
    interface Finish {
        void done(Object result, String error);

        default void ok(Object result) {
            done(result, null);
        }
    }

    /** What a wait found: whether what it waited for came true. */
    interface Then {
        void run(boolean ok);
    }

    interface Run {
        void run(Ask ask, Map<String, Object> request);
    }

    interface JobRun {
        void run(Map<String, Object> request, Finish finish);
    }

    record Command(boolean changes, Run run, JobRun job) {
        /** A command answered straight away. */
        static Command answer(boolean changes, Run run) {
            return new Command(changes, run, null);
        }

        /** A change that waits for Bitwig, so it needs changes turned on. */
        static Command job(JobRun job) {
            return new Command(true, null, job);
        }
    }

    /** A change that waits for Bitwig, and whether it needs changes turned on. */
    record Job(Ask ask, Map<String, Object> request, JobRun run, boolean changes) {}

    record Tuning(Parameter sampler, Parameter transpose) {}

    record Pad(int note, String path, String name) {}

    record Feature(String name, String error, long ms, Supplier<String> probe) {}

    static final class Params {
        List<String> ids = List.of();
        final Map<String, String> names = new HashMap<>();
        final Map<String, Double> values = new HashMap<>();
        final Map<String, String> displays = new HashMap<>();
    }
}
