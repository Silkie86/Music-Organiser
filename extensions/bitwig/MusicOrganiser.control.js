// Music Organiser link for Bitwig Studio.
//
// Lets the Music Organiser app see what's in the open Bitwig project: its tracks and devices, the
// selected device's parameters (with the IDs Bitwig saves them under) and Bitwig's actions. With
// "Let the app make changes" turned on in this controller's settings, the app can also set a
// parameter, insert a plugin, device, preset or sample, and run an action such as Save. That's how it
// learns Bitwig's file format: change one thing, save, and compare the files.
//
// Installing: Settings in the app copies this file into Bitwig's Controller Scripts folder. Then in
// Bitwig: Settings > Controllers > Add controller > Music Organiser > DAW link.
//
// The app connects to port 19791 on this computer and sends JSON requests, one per message; each
// message has a 4-byte big-endian length in front (Bitwig's remote connections work that way).
// The commands are described in extensions/README.md.
//
// Plain ES5 (var and function only), which every Bitwig version's script engine runs.
// @ts-check

loadAPI(18);
host.defineController("Music Organiser", "DAW link", "1.0", "ed35e3c7-c544-49ed-936d-c236b0c9b73b", "Music Organiser");
host.defineMidiPorts(0, 0);

var PORT = 19791;
var LINK_VERSION = 1;
/** Tracks the project listing covers (a flat list: tracks inside groups, effect tracks and the master). */
var MAX_TRACKS = 128;
/** Devices listed per track: the top level of its device chain. */
var MAX_DEVICES = 16;
/** How long to wait for the selected device's parameter values to arrive, in milliseconds. */
var DEVICE_WAIT = 2000;
var CHANGES_OFF = "Changes are turned off. To let the app change things, turn on \"Let the app make changes\" " +
                  "in Bitwig: Settings > Controllers > Music Organiser.";

/** @type {API.Application} */ var app;
/** @type {API.Project} */ var project;
/** @type {API.Transport} */ var transport;
/** @type {API.TrackBank} */ var tracks;
/** @type {API.DeviceBank[]} */ var trackDevices = [];
/** @type {API.CursorTrack} */ var cursorTrack;
/** @type {API.PinnableCursorDevice} */ var cursorDevice;
/** @type {API.DirectParameterValueDisplayObserver} */ var displayObserver;
/** @type {API.SettableBooleanValue} */ var changesSetting;
var port = PORT;

/**
 * The selected device's parameters, as Bitwig reports them. Display texts are only asked for while a
 * request waits for them, since Bitwig advises against watching them all the time.
 * @type {{ ids: string[], names: Object<string, string>, values: Object<string, number>, displays: Object<string, string> }}
 */
var params = { ids: [], names: {}, values: {}, displays: {} };

/** @typedef {{ connection: API.RemoteConnection, open: boolean }} Client */
/** @type {Client[]} */ var clients = [];
/** Requests waiting for the selected device's parameter values. @type {{ client: Client, id: any }[]} */
var deviceWaiters = [];
var deviceWaitStart = 0;

function init() {
    app = host.createApplication();
    app.projectName().markInterested();
    app.hasActiveEngine().markInterested();
    project = host.getProject();
    project.isModified().markInterested();

    transport = host.createTransport();
    transport.tempo().markInterested();
    transport.timeSignature().markInterested();
    transport.isPlaying().markInterested();
    transport.getPosition().markInterested();

    tracks = host.createTrackBank(MAX_TRACKS, 0, 0, true);
    tracks.itemCount().markInterested();
    for (var t = 0; t < MAX_TRACKS; t++) {
        var track = tracks.getItemAt(t);
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
        var devices = track.createDeviceBank(MAX_DEVICES);
        devices.itemCount().markInterested();
        for (var d = 0; d < MAX_DEVICES; d++) watchDevice(devices.getItemAt(d));
        trackDevices.push(devices);
    }

    // The track and device selected in Bitwig.
    cursorTrack = host.createCursorTrack("MO_LINK", "Music Organiser", 0, 0, true);
    cursorTrack.exists().markInterested();
    cursorTrack.name().markInterested();
    cursorDevice = cursorTrack.createCursorDevice();
    watchDevice(cursorDevice);
    cursorDevice.addDirectParameterIdObserver(function (ids) {
        var list = [];
        for (var i = 0; i < ids.length; i++) list.push(String(ids[i]));
        params.ids = list;
        params.names = keep(params.names, list);
        params.values = keep(params.values, list);
        params.displays = keep(params.displays, list);
    });
    cursorDevice.addDirectParameterNameObserver(256, function (id, name) { params.names[String(id)] = str(name); });
    cursorDevice.addDirectParameterNormalizedValueObserver(function (id, value) { params.values[String(id)] = Number(value); });
    displayObserver = cursorDevice.addDirectParameterValueDisplayObserver(64, function (id, text) {
        params.displays[String(id)] = str(text);
    });

    changesSetting = host.getPreferences().getBooleanSetting("Let the app make changes", "Music Organiser", false);
    changesSetting.markInterested();

    var socket = host.createRemoteConnection("Music Organiser", PORT);
    socket.setClientConnectCallback(connected);
    port = socket.getPort();
    println("Music Organiser: waiting for the app on port " + port + ".");
    if (port !== PORT)
        host.showPopupNotification("Music Organiser: port " + PORT + " is taken, so the app can't connect. " +
                                   "Close what's using it and restart Bitwig.");
}

function flush() {}

function exit() {
    for (var i = 0; i < clients.length; i++) clients[i].open = false;
    clients = [];
}

/** @param {API.Device} device */
function watchDevice(device) {
    device.exists().markInterested();
    device.name().markInterested();
    device.presetName().markInterested();
    device.isPlugin().markInterested();
    device.deviceType().markInterested();
    device.isEnabled().markInterested();
}

// ---- Talking to the app ----

/** @param {API.RemoteConnection} connection */
function connected(connection) {
    /** @type {Client} */
    var client = { connection: connection, open: true };
    clients.push(client);
    connection.setDisconnectCallback(function () {
        client.open = false;
        remove(clients, client);
    });
    // data is a Java byte[] (the version 18 type definitions call it a number).
    connection.setReceiveCallback(function (/** @type {any} */ data) { handle(client, decodeUtf8(data)); });
    send(client, { event: "hello", result: hello() });
}

/**
 * The commands the app can send. Those marked `changes` only run when the setting allows it.
 * @type {Object<string, { changes?: boolean, run: function(Client, any, any): void }>}
 */
var commands = {
    hello: { run: function (client, id) { reply(client, id, hello()); } },
    project: { run: function (client, id) { reply(client, id, describeProject()); } },
    device: { run: requestDevice },
    actions: { run: function (client, id) { reply(client, id, listActions()); } },
    select: { run: select },
    set: { changes: true, run: setParameter },
    insert: { changes: true, run: insert },
    action: { changes: true, run: runAction }
};

/**
 * @param {Client} client
 * @param {string} text
 */
function handle(client, text) {
    var request;
    try {
        request = JSON.parse(text);
    } catch (e) {
        send(client, { id: null, ok: false, error: "The request isn't JSON: " + text.slice(0, 80) });
        return;
    }
    var id = request && request.id !== undefined ? request.id : null;
    try {
        if (!request || typeof request.cmd !== "string" || !has(commands, request.cmd))
            throw new LinkError("Unknown command: " + (request ? request.cmd : request));
        var command = commands[request.cmd];
        if (command.changes && !changesSetting.get()) throw new LinkError(CHANGES_OFF);
        command.run(client, id, request);
    } catch (e) {
        reply(client, id, null, e instanceof LinkError ? e.message : "The Bitwig script failed: " + e);
    }
}

/**
 * @param {Client} client
 * @param {any} id
 * @param {any} result
 * @param {string=} error
 */
function reply(client, id, result, error) {
    send(client, error ? { id: id, ok: false, error: error } : { id: id, ok: true, result: result });
}

/**
 * @param {Client} client
 * @param {Object} message
 */
function send(client, message) {
    if (!client.open) return;
    try {
        client.connection.send(/** @type {any} */ (asciiBytes(JSON.stringify(message) + "\n")));
    } catch (e) {
        client.open = false;
        remove(clients, client);
        errorln("Music Organiser: lost the connection to the app (" + e + ").");
    }
}

/**
 * An error to report to the app as it is.
 * @constructor
 * @param {string} message
 */
function LinkError(message) {
    this.message = message;
}

// ---- What the app can ask for ----

function hello() {
    return {
        link: LINK_VERSION,
        product: str(host.getHostProduct()),
        version: str(host.getHostVersion()),
        api: Number(host.getHostApiVersion()),
        platform: str(host.getPlatformType()),
        port: port,
        project: str(app.projectName().get()),
        modified: !!project.isModified().get(),
        engine: !!app.hasActiveEngine().get(),
        changes: !!changesSetting.get()
    };
}

function describeProject() {
    var list = [];
    for (var t = 0; t < MAX_TRACKS; t++) {
        var track = tracks.getItemAt(t);
        if (track.exists().get()) list.push(describeTrack(t, track, trackDevices[t]));
    }
    return {
        name: str(app.projectName().get()),
        modified: !!project.isModified().get(),
        tempo: Number(transport.tempo().getRaw()),
        timeSignature: str(transport.timeSignature().get()),
        playing: !!transport.isPlaying().get(),
        position: Number(transport.getPosition().get()),
        trackCount: Number(tracks.itemCount().get()),
        tracks: list
    };
}

/**
 * @param {number} index
 * @param {API.Track} track
 * @param {API.DeviceBank} devices
 */
function describeTrack(index, track, devices) {
    var list = [];
    for (var d = 0; d < MAX_DEVICES; d++) {
        var device = devices.getItemAt(d);
        if (device.exists().get()) list.push(describeDevice(d, device));
    }
    return {
        index: index,
        name: str(track.name().get()),
        type: str(track.trackType().get()),
        group: !!track.isGroup().get(),
        color: colour(track.color()),
        active: !!track.isActivated().get(),
        mute: !!track.mute().get(),
        solo: !!track.solo().get(),
        arm: !!track.arm().get(),
        volume: str(track.volume().displayedValue().get()),
        volumeValue: Number(track.volume().get()),
        pan: str(track.pan().displayedValue().get()),
        panValue: Number(track.pan().get()),
        deviceCount: Number(devices.itemCount().get()),
        devices: list
    };
}

/**
 * @param {number} index
 * @param {API.Device} device
 */
function describeDevice(index, device) {
    return {
        index: index,
        name: str(device.name().get()),
        preset: str(device.presetName().get()),
        kind: str(device.deviceType().get()),
        plugin: !!device.isPlugin().get(),
        enabled: !!device.isEnabled().get()
    };
}

/**
 * The selected device with all its parameters. Bitwig sends the display texts ("-6.0 dB") only once
 * they're asked for, so the reply waits for them (up to DEVICE_WAIT).
 * @param {Client} client
 * @param {any} id
 */
function requestDevice(client, id) {
    if (!cursorDevice.exists().get()) {
        reply(client, id, { track: str(cursorTrack.name().get()), device: null, params: [] });
        return;
    }
    deviceWaiters.push({ client: client, id: id });
    if (deviceWaiters.length > 1) return;   // already waiting: this request gets the same answer
    params.displays = {};
    displayObserver.setObservedParameterIds(params.ids.slice());
    deviceWaitStart = Date.now();
    host.scheduleTask(checkDevice, 50);
}

function checkDevice() {
    var missing = 0;
    for (var i = 0; i < params.ids.length; i++)
        if (!has(params.displays, params.ids[i])) missing++;
    if (missing > 0 && Date.now() - deviceWaitStart < DEVICE_WAIT) {
        host.scheduleTask(checkDevice, 50);
        return;
    }
    var result = describeSelectedDevice();
    displayObserver.setObservedParameterIds(null);
    var waiters = deviceWaiters;
    deviceWaiters = [];
    for (var w = 0; w < waiters.length; w++) reply(waiters[w].client, waiters[w].id, result);
}

function describeSelectedDevice() {
    var list = [];
    for (var i = 0; i < params.ids.length; i++) {
        var id = params.ids[i];
        list.push({
            id: id,
            name: has(params.names, id) ? params.names[id] : "",
            value: has(params.values, id) ? params.values[id] : null,   // NaN (not readable) becomes null in JSON
            display: has(params.displays, id) ? params.displays[id] : null
        });
    }
    return {
        track: str(cursorTrack.name().get()),
        device: cursorDevice.exists().get() ? describeDevice(-1, cursorDevice) : null,
        params: list
    };
}

function listActions() {
    var all = app.getActions();
    var list = [];
    for (var i = 0; i < all.length; i++) {
        var action = all[i];
        var category = action.getCategory();
        list.push({
            id: str(action.getId()),
            name: str(action.getName()),
            category: category ? str(category.getName()) : "",
            menu: str(action.getMenuItemText())
        });
    }
    return list;
}

/**
 * Selects a track from the project listing (by its index there) and, optionally, one of its devices.
 * @param {Client} client
 * @param {any} id
 * @param {{ track: number, device?: number }} request
 */
function select(client, id, request) {
    var t = request.track;
    if (!isIndex(t, MAX_TRACKS) || !tracks.getItemAt(t).exists().get())
        throw new LinkError("There's no track " + t + " in the project listing.");
    var device = null;
    if (request.device !== undefined && request.device !== null) {
        var d = request.device;
        if (!isIndex(d, MAX_DEVICES) || !trackDevices[t].getItemAt(d).exists().get())
            throw new LinkError("Track " + t + " has no device " + d + ".");
        device = trackDevices[t].getItemAt(d);
    }
    cursorTrack.selectChannel(tracks.getItemAt(t));
    if (device) cursorDevice.selectDevice(device);
    reply(client, id, {});
}

/**
 * Sets one of the selected device's parameters, by its ID, to a value from 0 to 1.
 * @param {Client} client
 * @param {any} id
 * @param {{ param: string, value: number }} request
 */
function setParameter(client, id, request) {
    if (!cursorDevice.exists().get()) throw new LinkError("No device is selected in Bitwig.");
    if (typeof request.param !== "string" || params.ids.indexOf(request.param) < 0)
        throw new LinkError("The selected device has no parameter " + request.param + ".");
    var value = request.value;
    if (typeof value !== "number" || !(value >= 0 && value <= 1))
        throw new LinkError("The value has to be a number from 0 to 1.");
    // Bitwig takes the value as a step out of (resolution - 1).
    cursorDevice.setDirectParameterValueNormalized(request.param, Math.round(value * 1000000), 1000001);
    reply(client, id, {});
}

/**
 * Inserts something at the end of the selected track's device chain: a plugin by its ID (as the app's
 * plugin scanner reads it), a Bitwig device by its ID, or a file (a preset or a sample).
 * @param {Client} client
 * @param {any} id
 * @param {{ kind: string, plugin?: any, device?: any, path?: any }} request
 */
function insert(client, id, request) {
    if (!cursorTrack.exists().get()) throw new LinkError("No track is selected in Bitwig.");
    var point = cursorTrack.endOfDeviceChainInsertionPoint();
    switch (request.kind) {
        case "vst3": point.insertVST3Device(given(request.plugin, "plugin ID")); break;
        case "clap": point.insertCLAPDevice(given(request.plugin, "plugin ID")); break;
        case "vst2":
            if (typeof request.plugin !== "number") throw new LinkError("A VST2 plugin's ID is a number.");
            point.insertVST2Device(request.plugin);
            break;
        case "bitwig": point.insertBitwigDevice(uuid(given(request.device, "device ID"))); break;
        case "file": point.insertFile(given(request.path, "file")); break;
        default: throw new LinkError("Can't insert a \"" + request.kind + "\": use vst3, clap, vst2, bitwig or file.");
    }
    // Bitwig quietly does nothing for an ID it doesn't know; the app checks the device list afterwards.
    reply(client, id, {});
}

/**
 * Runs one of Bitwig's actions by its ID (as listed by the actions command), e.g. saving the project.
 * @param {Client} client
 * @param {any} id
 * @param {{ action: string }} request
 */
function runAction(client, id, request) {
    var action = typeof request.action === "string" ? app.getAction(request.action) : null;
    if (!action) throw new LinkError("Bitwig has no action " + request.action + ".");
    action.invoke();
    reply(client, id, {});
}

// ---- Helpers ----

/**
 * A java.util.UUID for insertBitwigDevice, where the script engine can make one.
 * @param {string} text
 * @returns {any}
 */
function uuid(text) {
    if (typeof Java !== "undefined" && Java.type) return Java.type("java.util.UUID").fromString(text);
    return text;
}

/**
 * @param {any} value
 * @param {number} count
 */
function isIndex(value, count) {
    return typeof value === "number" && value >= 0 && value < count && Math.floor(value) === value;
}

/**
 * @param {any} value
 * @param {string} what
 * @returns {string}
 */
function given(value, what) {
    if (typeof value !== "string" || value.length === 0) throw new LinkError("No " + what + " given.");
    return value;
}

/** @param {any} value */
function str(value) {
    return value === null || value === undefined ? "" : String(value);
}

/** @param {API.ColorValue} c */
function colour(c) {
    function hex(v) {
        var n = Math.max(0, Math.min(255, Math.round(Number(v) * 255)));
        return (n < 16 ? "0" : "") + n.toString(16);
    }
    return "#" + hex(c.red()) + hex(c.green()) + hex(c.blue());
}

/**
 * @param {Object} map
 * @param {string} key
 */
function has(map, key) {
    return Object.prototype.hasOwnProperty.call(map, key);
}

/**
 * The entries of `map` whose keys are in `keys`.
 * @param {Object<string, any>} map
 * @param {string[]} keys
 * @returns {Object<string, any>}
 */
function keep(map, keys) {
    var kept = {};
    for (var i = 0; i < keys.length; i++)
        if (has(map, keys[i])) kept[keys[i]] = map[keys[i]];
    return kept;
}

/**
 * @param {any[]} list
 * @param {any} item
 */
function remove(list, item) {
    var i = list.indexOf(item);
    if (i >= 0) list.splice(i, 1);
}

/**
 * A request's bytes as text. Bitwig hands them over as signed bytes.
 * @param {ArrayLike<number>} bytes
 */
function decodeUtf8(bytes) {
    var out = [];
    for (var i = 0; i < bytes.length;) {
        var c = bytes[i++] & 0xff;
        if (c < 0x80) {
            out.push(c);
        } else if (c < 0xe0) {
            out.push(((c & 0x1f) << 6) | (bytes[i++] & 0x3f));
        } else if (c < 0xf0) {
            out.push(((c & 0x0f) << 12) | ((bytes[i++] & 0x3f) << 6) | (bytes[i++] & 0x3f));
        } else {
            var cp = (((c & 0x07) << 18) | ((bytes[i++] & 0x3f) << 12) | ((bytes[i++] & 0x3f) << 6) | (bytes[i++] & 0x3f)) - 0x10000;
            out.push(0xd800 + (cp >> 10), 0xdc00 + (cp & 0x3ff));
        }
    }
    var text = "";
    for (var j = 0; j < out.length; j += 4096) text += String.fromCharCode.apply(null, out.slice(j, j + 4096));
    return text;
}

/**
 * JSON as bytes for Bitwig to send. Anything outside ASCII is written as a \u escape (it can only be
 * inside a JSON string), so every character is one byte.
 * @param {string} json
 */
function asciiBytes(json) {
    var escaped = json.replace(/[\u007f-\uffff]/g, function (c) {
        return "\\u" + ("000" + c.charCodeAt(0).toString(16)).slice(-4);
    });
    var bytes = new Array(escaped.length);
    for (var i = 0; i < escaped.length; i++) bytes[i] = escaped.charCodeAt(i);
    return bytes;
}
