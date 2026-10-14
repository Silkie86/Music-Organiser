// A stand-in for the parts of Bitwig's controller API the Music Organiser script uses, so the script
// can be run and checked without Bitwig. Like Bitwig, reading a value that init() didn't mark as
// interested fails.
"use strict";

const fs = require("node:fs");
const path = require("node:path");
const vm = require("node:vm");

const SCRIPT = path.join(__dirname, "..", "MusicOrganiser.control.js");

class Value {
  constructor(v) {
    this.v = v;
    this.interested = false;
  }
  markInterested() {
    this.interested = true;
  }
  get() {
    if (!this.interested) throw new Error("read a value that wasn't marked as interested");
    return this.v;
  }
  set(v) {
    this.v = v;
  }
}

class Parameter extends Value {
  constructor(v, display) {
    super(v);
    this.display = new Value(display);
  }
  getRaw() {
    return this.get();
  }
  displayedValue() {
    return this.display;
  }
}

class Colour {
  constructor(r, g, b) {
    Object.assign(this, { r, g, b, interested: false });
  }
  markInterested() {
    this.interested = true;
  }
  check() {
    if (!this.interested) throw new Error("read a colour that wasn't marked as interested");
  }
  red() { this.check(); return this.r; }
  green() { this.check(); return this.g; }
  blue() { this.check(); return this.b; }
}

class Bank {
  constructor(items) {
    this.items = items;
    this.count = new Value(0);
  }
  getItemAt(i) {
    return this.items[i];
  }
  itemCount() {
    return this.count;
  }
}

class Device {
  constructor() {
    this.values = {
      exists: new Value(false), name: new Value(""), presetName: new Value(""),
      isPlugin: new Value(false), deviceType: new Value(""), isEnabled: new Value(true),
    };
  }
  load(spec) {
    const v = this.values;
    v.exists.set(!!spec);
    v.name.set(spec ? spec.name : "");
    v.presetName.set(spec ? spec.preset || "" : "");
    v.isPlugin.set(!!(spec && spec.plugin));
    v.deviceType.set(spec ? spec.kind || "audio_to_audio" : "");
    v.isEnabled.set(!spec || spec.enabled !== false);
  }
  exists() { return this.values.exists; }
  name() { return this.values.name; }
  presetName() { return this.values.presetName; }
  isPlugin() { return this.values.isPlugin; }
  deviceType() { return this.values.deviceType; }
  isEnabled() { return this.values.isEnabled; }
}

/** The device selected in Bitwig, with Bitwig's "direct parameter" observers. */
class CursorDevice extends Device {
  constructor(clock) {
    super();
    this.clock = clock;
    this.params = [];
    this.observed = undefined;
    this.observedHistory = [];
    this.selected = [];
    this.setCalls = [];
  }
  addDirectParameterIdObserver(cb) { this.idObserver = cb; }
  addDirectParameterNameObserver(maxChars, cb) { this.nameObserver = cb; }
  addDirectParameterNormalizedValueObserver(cb) { this.valueObserver = cb; }
  addDirectParameterValueDisplayObserver(maxChars, cb) {
    this.displayObserver = cb;
    const self = this;
    return {
      setObservedParameterIds(ids) {
        self.observed = ids;
        self.observedHistory.push(ids);
        if (!ids) return;
        // Bitwig sends the texts a moment later; parameters without one never answer.
        self.clock.schedule(() => {
          if (self.observed !== ids) return;
          for (const p of self.params)
            if (p.display !== undefined && ids.indexOf(p.id) >= 0) self.displayObserver(p.id, p.display);
        }, 30);
      },
    };
  }
  /** Selects a device in "Bitwig": its parameters arrive through the observers. */
  show(spec, params) {
    this.load(spec);
    this.params = params || [];
    this.idObserver(this.params.map((p) => p.id));
    for (const p of this.params) {
      this.nameObserver(p.id, p.name);
      this.valueObserver(p.id, p.value);
    }
  }
  selectDevice(device) { this.selected.push(device); }
  setDirectParameterValueNormalized(id, value, resolution) { this.setCalls.push([id, value, resolution]); }
}

class Track {
  constructor() {
    this.values = {
      exists: new Value(false), name: new Value(""), trackType: new Value(""), isGroup: new Value(false),
      isActivated: new Value(true), mute: new Value(false), solo: new Value(false), arm: new Value(false),
    };
    this.colour = new Colour(0, 0, 0);
    this.vol = new Parameter(0.79, "0.0 dB");
    this.panning = new Parameter(0.5, "C");
    this.devices = null;
  }
  load(spec) {
    const v = this.values;
    v.exists.set(!!spec);
    if (!spec) return;
    v.name.set(spec.name);
    v.trackType.set(spec.type || "Instrument");
    v.isGroup.set(!!spec.group);
    v.mute.set(!!spec.mute);
    if (spec.colour) [this.colour.r, this.colour.g, this.colour.b] = spec.colour;
    if (spec.volume) [this.vol.v, this.vol.display.v] = spec.volume;
    if (!this.devices) return;
    const devices = spec.devices || [];
    this.devices.items.forEach((d, i) => d.load(devices[i]));
    this.devices.count.set(devices.length);
  }
  exists() { return this.values.exists; }
  name() { return this.values.name; }
  trackType() { return this.values.trackType; }
  isGroup() { return this.values.isGroup; }
  isActivated() { return this.values.isActivated; }
  mute() { return this.values.mute; }
  solo() { return this.values.solo; }
  arm() { return this.values.arm; }
  color() { return this.colour; }
  volume() { return this.vol; }
  pan() { return this.panning; }
  createDeviceBank(n) {
    this.devices = new Bank(Array.from({ length: n }, () => new Device()));
    return this.devices;
  }
}

/** Runs the script in a fake Bitwig with the given project, and returns handles to poke at it. */
function startBitwig(model = {}) {
  const clock = {
    now: 0,
    tasks: [],
    schedule(fn, ms) { this.tasks.push({ at: this.now + ms, fn }); },
    advance(ms) {
      const end = this.now + ms;
      for (;;) {
        this.tasks.sort((a, b) => a.at - b.at);
        const next = this.tasks[0];
        if (!next || next.at > end) break;
        this.tasks.shift();
        this.now = next.at;
        next.fn();
      }
      this.now = end;
    },
  };
  const calls = [];
  const errors = [];
  const popups = [];
  const actions = (model.actions || []).map((a) => ({
    getId: () => a.id, getName: () => a.name, getMenuItemText: () => a.menu || a.name,
    getCategory: () => (a.category ? { getName: () => a.category } : null),
    invoke: () => calls.push(["invoke", a.id]),
  }));
  const app = {
    name: new Value(model.project || "Untitled"),
    engine: new Value(true),
    projectName() { return this.name; },
    hasActiveEngine() { return this.engine; },
    getActions: () => actions,
    getAction: (id) => actions.find((a) => a.getId() === id) || null,
  };
  const project = { modified: new Value(!!model.modified), isModified() { return this.modified; } };
  const transport = {
    tempoValue: new Parameter(model.tempo || 120, ""),
    signature: new Value(model.timeSignature || "4/4"),
    playing: new Value(false),
    position: new Value(0),
    tempo() { return this.tempoValue; },
    timeSignature() { return this.signature; },
    isPlaying() { return this.playing; },
    getPosition() { return this.position; },
  };
  const insertionPoint = {};
  for (const m of ["insertVST3Device", "insertVST2Device", "insertCLAPDevice", "insertBitwigDevice", "insertFile"])
    insertionPoint[m] = (arg) => calls.push([m, arg]);
  const cursorDevice = new CursorDevice(clock);
  const cursorTrack = new Track();
  cursorTrack.selectChannel = (track) => calls.push(["selectChannel", track]);
  cursorTrack.createCursorDevice = () => cursorDevice;
  cursorTrack.endOfDeviceChainInsertionPoint = () => insertionPoint;
  let tracks = null;
  let changes = null;
  let connect = null;
  let defined = null;
  let api = null;

  const host = {
    defineController: (...args) => { defined = args; },
    defineMidiPorts: () => {},
    createApplication: () => app,
    getProject: () => project,
    createTransport: () => transport,
    createTrackBank(n, sends, scenes, flat) {
      if (!flat) throw new Error("expected a flat track list");
      tracks = new Bank(Array.from({ length: n }, () => new Track()));
      return tracks;
    },
    createCursorTrack: (id, name, sends, scenes, follow) => {
      if (!follow) throw new Error("expected a cursor that follows the selection");
      return cursorTrack;
    },
    getPreferences: () => ({
      getBooleanSetting(label, category, initial) {
        changes = new Value(initial);
        return changes;
      },
    }),
    createRemoteConnection: (name, port) => ({
      setClientConnectCallback(cb) { connect = cb; },
      getPort: () => model.portInUse || port,
    }),
    showPopupNotification: (text) => popups.push(text),
    getHostProduct: () => "Bitwig Studio",
    getHostVersion: () => "6.1.2",
    getHostApiVersion: () => 25,
    getPlatformType: () => "WINDOWS",
    scheduleTask: (fn, ms) => clock.schedule(fn, ms),
  };

  const sandbox = {
    host,
    loadAPI: (v) => { api = v; },
    println: () => {},
    errorln: (text) => errors.push(text),
    Date: { now: () => clock.now },
  };
  if (model.java) sandbox.Java = { type: (name) => ({ fromString: (s) => ({ uuid: s, javaType: name }) }) };
  const context = vm.createContext(sandbox);
  vm.runInContext(fs.readFileSync(SCRIPT, "utf8"), context, { filename: "MusicOrganiser.control.js" });
  context.init();

  // Fill in the project after init(), as Bitwig does.
  const trackSpecs = model.tracks || [];
  tracks.items.forEach((t, i) => t.load(trackSpecs[i]));
  tracks.count.set(trackSpecs.length);
  cursorTrack.load({ name: model.selectedTrack || "", devices: [] });
  cursorTrack.values.exists.set(model.selectedTrack !== undefined);

  return {
    clock, calls, errors, popups, tracks, cursorTrack, cursorDevice, app, project, context,
    get api() { return api; },
    get defined() { return defined; },
    allowChanges(on) { changes.set(on); },
    /** Hands the script a connection, as Bitwig does when the app connects. */
    attach(connection) { connect(connection); },
    /** Connects as the app would, and returns a way to send requests and read what comes back. */
    connect() {
      const received = [];
      let receive = null;
      let disconnect = null;
      connect({
        setReceiveCallback: (cb) => { receive = cb; },
        setDisconnectCallback: (cb) => { disconnect = cb; },
        send(bytes) {
          if (!Array.isArray(bytes)) throw new Error("expected an array of bytes");
          for (const b of bytes)
            if (!Number.isInteger(b) || b < 0 || b > 127) throw new Error(`expected ASCII bytes, got ${b}`);
          received.push(JSON.parse(Buffer.from(bytes).toString("ascii")));
        },
      });
      let read = 0;
      return {
        received,
        /** Messages that arrived since the last call. */
        take() {
          const fresh = received.slice(read);
          read = received.length;
          return fresh;
        },
        /** Sends a request (an object, or raw text) as Bitwig would hand it over: signed bytes. */
        send(request) {
          const text = typeof request === "string" ? request : JSON.stringify(request);
          receive(Array.from(Buffer.from(text, "utf8"), (b) => (b > 127 ? b - 256 : b)));
        },
        /** Sends a request and returns the one reply to it. */
        ask(request) {
          this.send(request);
          const fresh = this.take();
          if (fresh.length !== 1) throw new Error(`expected one reply, got ${JSON.stringify(fresh)}`);
          return fresh[0];
        },
        close() { disconnect(); },
      };
    },
  };
}

module.exports = { startBitwig, SCRIPT };
