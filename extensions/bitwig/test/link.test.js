// Runs the Bitwig controller script in a stand-in Bitwig (fake-bitwig.js) and talks to it the way
// the app does.  npm test
"use strict";

const test = require("node:test");
const assert = require("node:assert/strict");
const fs = require("node:fs");
const acorn = require("acorn");
const { startBitwig, SCRIPT } = require("./fake-bitwig");

const CHANGES_OFF = /Changes are turned off/;

const song = {
  project: "Night Drive",
  tempo: 124,
  timeSignature: "7/8",
  selectedTrack: "Bass",
  tracks: [
    { name: "Drums", type: "Group", group: true, devices: [] },
    {
      name: "Bass", type: "Instrument", colour: [1, 0.5, 0], volume: [0.6, "-6.0 dB"],
      devices: [
        { name: "Serum", preset: "Reese", plugin: true, kind: "note_to_audio" },
        { name: "EQ+", plugin: false, enabled: false },
      ],
    },
    { name: "Master", type: "Master", devices: [{ name: "Peak Limiter" }] },
  ],
  actions: [
    { id: "Save", name: "Save", category: "File" },
    { id: "toggle_metronome", name: "Toggle Metronome", category: "Transport" },
  ],
};

test("it's plain ES5, which every Bitwig script engine runs", () => {
  acorn.parse(fs.readFileSync(SCRIPT, "utf8"), { ecmaVersion: 5 });
});

test("it's plain ASCII, so it reads the same in any encoding", () => {
  assert.doesNotMatch(fs.readFileSync(SCRIPT, "latin1"), /[^\x00-\x7f]/);
});

test("says hello when the app connects", () => {
  const bitwig = startBitwig(song);
  assert.equal(bitwig.api, 18);
  const [vendor, model, , id] = bitwig.defined;
  assert.deepEqual([vendor, model], ["Music Organiser", "DAW link"]);
  assert.match(id, /^[0-9a-f]{8}-[0-9a-f]{4}-4[0-9a-f]{3}-[89ab][0-9a-f]{3}-[0-9a-f]{12}$/);

  const app = bitwig.connect();
  const [hello] = app.take();
  assert.equal(hello.event, "hello");
  assert.deepEqual(hello.result, {
    link: 1, product: "Bitwig Studio", version: "6.1.2", api: 25, platform: "WINDOWS", port: 19791,
    project: "Night Drive", modified: false, engine: true, changes: false,
  });
  assert.deepEqual(app.ask({ id: 1, cmd: "hello" }), { id: 1, ok: true, result: hello.result });
  assert.deepEqual(bitwig.popups, []);
});

test("warns in Bitwig when its port is taken", () => {
  const bitwig = startBitwig({ portInUse: 50123 });
  assert.match(bitwig.popups[0], /port 19791 is taken/);
});

test("lists the project's tracks and their devices", () => {
  const bitwig = startBitwig(song);
  const app = bitwig.connect();
  app.take();
  const reply = app.ask({ id: "p", cmd: "project" });
  assert.equal(reply.ok, true);
  const p = reply.result;
  assert.deepEqual([p.name, p.tempo, p.timeSignature, p.trackCount], ["Night Drive", 124, "7/8", 3]);
  assert.deepEqual(p.tracks.map((t) => t.name), ["Drums", "Bass", "Master"]);
  const bass = p.tracks[1];
  assert.deepEqual(
    { index: bass.index, type: bass.type, group: bass.group, color: bass.color, volume: bass.volume, volumeValue: bass.volumeValue },
    { index: 1, type: "Instrument", group: false, color: "#ff8000", volume: "-6.0 dB", volumeValue: 0.6 });
  assert.deepEqual(bass.devices, [
    { index: 0, name: "Serum", preset: "Reese", kind: "note_to_audio", plugin: true, enabled: true },
    { index: 1, name: "EQ+", preset: "", kind: "audio_to_audio", plugin: false, enabled: false },
  ]);
  assert.equal(bass.deviceCount, 2);
  assert.equal(p.tracks[0].group, true);
});

test("waits for the selected device's parameter texts", () => {
  const bitwig = startBitwig(song);
  bitwig.cursorDevice.show({ name: "Serum", preset: "Reese", plugin: true }, [
    { id: "CONTENTS/PID3e8", name: "Cutoff", value: 0.5, display: "440 Hz" },
    { id: "CONTENTS/PID3e9", name: "Mix", value: NaN, display: "50 %" },
  ]);
  const app = bitwig.connect();
  app.take();

  app.send({ id: 7, cmd: "device" });
  assert.deepEqual(app.take(), [], "answers only once the texts are in");
  // (Array.from: the script's arrays come from its own JavaScript realm.)
  assert.deepEqual(Array.from(bitwig.cursorDevice.observed), ["CONTENTS/PID3e8", "CONTENTS/PID3e9"]);
  bitwig.clock.advance(100);
  const [reply] = app.take();
  assert.equal(reply.id, 7);
  assert.equal(reply.result.track, "Bass");
  assert.equal(reply.result.device.name, "Serum");
  assert.deepEqual(reply.result.params, [
    { id: "CONTENTS/PID3e8", name: "Cutoff", value: 0.5, display: "440 Hz" },
    { id: "CONTENTS/PID3e9", name: "Mix", value: null, display: "50 %" },
  ]);
  assert.equal(bitwig.cursorDevice.observed, null, "stops watching the texts afterwards");
});

test("gives up on parameter texts that never come", () => {
  const bitwig = startBitwig(song);
  bitwig.cursorDevice.show({ name: "Odd Plugin", plugin: true }, [
    { id: "a", name: "Talks", value: 1, display: "on" },
    { id: "b", name: "Silent", value: 0 },
  ]);
  const app = bitwig.connect();
  app.take();
  app.send({ id: 1, cmd: "device" });
  app.send({ id: 2, cmd: "device" });   // asked twice while waiting: both get the answer
  bitwig.clock.advance(1900);
  assert.deepEqual(app.take(), []);
  bitwig.clock.advance(200);
  const replies = app.take();
  assert.deepEqual(replies.map((r) => r.id), [1, 2]);
  assert.deepEqual(replies[0].result.params.map((p) => p.display), ["on", null]);
});

test("says when no device is selected", () => {
  const bitwig = startBitwig(song);
  const app = bitwig.connect();
  app.take();
  assert.deepEqual(app.ask({ id: 3, cmd: "device" }).result, { track: "Bass", device: null, params: [] });
});

test("forgets parameters of the previous device", () => {
  const bitwig = startBitwig(song);
  bitwig.cursorDevice.show({ name: "One" }, [{ id: "x", name: "Old", value: 0.1, display: "1" }]);
  bitwig.cursorDevice.show({ name: "Two" }, [{ id: "y", name: "New", value: 0.2, display: "2" }]);
  const app = bitwig.connect();
  app.take();
  app.send({ id: 1, cmd: "device" });
  bitwig.clock.advance(100);
  assert.deepEqual(app.take()[0].result.params, [{ id: "y", name: "New", value: 0.2, display: "2" }]);
});

test("changes nothing until changes are allowed in Bitwig", () => {
  const bitwig = startBitwig(song);
  bitwig.cursorDevice.show({ name: "Serum" }, [{ id: "p", name: "Cutoff", value: 0 }]);
  const app = bitwig.connect();
  app.take();
  for (const request of [
    { id: 1, cmd: "set", param: "p", value: 0.5 },
    { id: 2, cmd: "insert", kind: "vst3", plugin: "x" },
    { id: 3, cmd: "action", action: "Save" },
  ]) {
    const reply = app.ask(request);
    assert.equal(reply.ok, false);
    assert.match(reply.error, CHANGES_OFF);
  }
  assert.deepEqual(bitwig.calls, []);
  assert.deepEqual(bitwig.cursorDevice.setCalls, []);
});

test("sets the selected device's parameters", () => {
  const bitwig = startBitwig(song);
  bitwig.allowChanges(true);
  bitwig.cursorDevice.show({ name: "Serum" }, [{ id: "CONTENTS/PID3e8", name: "Cutoff", value: 0 }]);
  const app = bitwig.connect();
  assert.equal(app.take()[0].result.changes, true);

  assert.equal(app.ask({ id: 1, cmd: "set", param: "CONTENTS/PID3e8", value: 0.25 }).ok, true);
  assert.deepEqual(bitwig.cursorDevice.setCalls, [["CONTENTS/PID3e8", 250000, 1000001]]);
  assert.match(app.ask({ id: 2, cmd: "set", param: "nope", value: 0.25 }).error, /no parameter nope/);
  assert.match(app.ask({ id: 3, cmd: "set", param: "CONTENTS/PID3e8", value: 2 }).error, /from 0 to 1/);
  assert.match(app.ask({ id: 4, cmd: "set", param: "CONTENTS/PID3e8", value: "0.5" }).error, /from 0 to 1/);
});

test("inserts plugins, Bitwig devices, presets and samples", () => {
  const bitwig = startBitwig({ ...song, java: true });
  bitwig.allowChanges(true);
  const app = bitwig.connect();
  app.take();
  const ok = (request) => assert.equal(app.ask(request).ok, true, JSON.stringify(request));
  ok({ id: 1, cmd: "insert", kind: "vst3", plugin: "805ECA49563E43EA8119369F0ED6096D" });
  ok({ id: 2, cmd: "insert", kind: "clap", plugin: "org.musicorganiser.probe.effect" });
  ok({ id: 3, cmd: "insert", kind: "vst2", plugin: 1299137074 });
  ok({ id: 4, cmd: "insert", kind: "bitwig", device: "c9a8b2f5-4f50-4f5c-98f2-a7b3c1e0d1aa" });
  ok({ id: 5, cmd: "insert", kind: "file", path: "C:\\Samples\\Café\\Kick ß.wav" });
  assert.deepEqual(bitwig.calls, [
    ["insertVST3Device", "805ECA49563E43EA8119369F0ED6096D"],
    ["insertCLAPDevice", "org.musicorganiser.probe.effect"],
    ["insertVST2Device", 1299137074],
    ["insertBitwigDevice", { uuid: "c9a8b2f5-4f50-4f5c-98f2-a7b3c1e0d1aa", javaType: "java.util.UUID" }],
    ["insertFile", "C:\\Samples\\Café\\Kick ß.wav"],
  ]);
  assert.match(app.ask({ id: 6, cmd: "insert", kind: "vst2", plugin: "MoB2" }).error, /is a number/);
  assert.match(app.ask({ id: 7, cmd: "insert", kind: "aax", plugin: "x" }).error, /use vst3, clap, vst2, bitwig or file/);
  assert.match(app.ask({ id: 9, cmd: "insert", kind: "vst3" }).error, /No plugin ID given/);
  assert.match(app.ask({ id: 8, cmd: "insert", kind: "file", path: "" }).error, /No file/);
});

test("lists and runs Bitwig's actions", () => {
  const bitwig = startBitwig(song);
  const app = bitwig.connect();
  app.take();
  assert.deepEqual(app.ask({ id: 1, cmd: "actions" }).result, [
    { id: "Save", name: "Save", category: "File", menu: "Save" },
    { id: "toggle_metronome", name: "Toggle Metronome", category: "Transport", menu: "Toggle Metronome" },
  ]);
  bitwig.allowChanges(true);
  assert.equal(app.ask({ id: 2, cmd: "action", action: "Save" }).ok, true);
  assert.deepEqual(bitwig.calls, [["invoke", "Save"]]);
  assert.match(app.ask({ id: 3, cmd: "action", action: "Format C:" }).error, /no action Format C:/);
});

test("selects a track and one of its devices", () => {
  const bitwig = startBitwig(song);
  const app = bitwig.connect();
  app.take();
  assert.equal(app.ask({ id: 1, cmd: "select", track: 1, device: 1 }).ok, true);
  assert.equal(bitwig.calls[0][0], "selectChannel");
  assert.equal(bitwig.calls[0][1], bitwig.tracks.getItemAt(1));
  assert.deepEqual(bitwig.cursorDevice.selected, [bitwig.tracks.getItemAt(1).devices.getItemAt(1)]);
  assert.match(app.ask({ id: 2, cmd: "select", track: 3 }).error, /no track 3/);
  assert.match(app.ask({ id: 3, cmd: "select", track: 0.5 }).error, /no track 0.5/);
  assert.match(app.ask({ id: 4, cmd: "select", track: 1, device: 2 }).error, /has no device 2/);
});

test("answers requests it can't follow with an error", () => {
  const bitwig = startBitwig(song);
  const app = bitwig.connect();
  app.take();
  assert.deepEqual(app.ask("{not json"), { id: null, ok: false, error: "The request isn't JSON: {not json" });
  assert.deepEqual(app.ask({ id: 2, cmd: "explode" }), { id: 2, ok: false, error: "Unknown command: explode" });
  assert.equal(app.ask({ id: 3, cmd: "toString" }).error, "Unknown command: toString");
  assert.equal(app.ask({ id: 4 }).error, "Unknown command: undefined");
  assert.equal(app.ask("null").error, "Unknown command: null");
});

test("keeps text outside ASCII intact", () => {
  const name = "Café ☕ 𝄞 Ünïcödé";
  const bitwig = startBitwig({ ...song, tracks: [{ name, devices: [] }] });
  const app = bitwig.connect();
  app.take();
  assert.equal(app.ask({ id: 1, cmd: "project" }).result.tracks[0].name, name);
});

test("stops sending to an app that disconnected", () => {
  const bitwig = startBitwig(song);
  bitwig.cursorDevice.show({ name: "Serum" }, [{ id: "p", name: "Cutoff", value: 0, display: "0" }]);
  const app = bitwig.connect();
  app.take();
  app.send({ id: 1, cmd: "device" });
  app.close();
  bitwig.clock.advance(100);
  assert.deepEqual(app.take(), []);
  assert.deepEqual(bitwig.errors, []);
});
