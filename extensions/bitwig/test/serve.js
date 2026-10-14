// Serves the Bitwig script, running in the stand-in Bitwig, on a TCP port on this computer, with each
// message framed as Bitwig frames them (a 4-byte big-endian length in front). The app's tests use it
// to talk to the real script. Prints "port <number>" once it's listening; stops when stdin closes.
//
//   node serve.js [--raw]     --raw: send without the length in front, to check the app copes either way
"use strict";

const net = require("node:net");
const { startBitwig } = require("./fake-bitwig");

const raw = process.argv.includes("--raw");
const bitwig = startBitwig({
  project: "Night Drive",
  tempo: 124,
  // What the app can send in: any preset file, one plugin by its ID, and a Drum Machine (the stand-in's ID).
  presets: true,
  plugins: { "VST3:805ECA49563E43EA8119369F0ED6096D": { name: "Serum", plugin: true, kind: "note_to_audio" } },
  drumMachine: "d7d7d7d7-0000-4000-8000-000000000001",
  tracks: [
    { name: "Drums", type: "Group", group: true, devices: [] },
    { name: "Kick", type: "Audio", devices: [{ name: "EQ+" }] },
    {
      name: "Bass", type: "Instrument", colour: [1, 0.5, 0], volume: [0.6, "-6.0 dB"],
      devices: [{ name: "Serum", preset: "Reese", plugin: true, kind: "note_to_audio" }, { name: "Compressor" }],
    },
    { name: "Master", type: "Master", devices: [{ name: "Peak Limiter" }] },
  ],
  selectedTrack: "Bass",
  actions: [{ id: "Save", name: "Save", category: "File" }],
});
bitwig.allowChanges(true);
bitwig.cursorDevice.show({ name: "Serum", preset: "Reese", plugin: true, kind: "note_to_audio" }, [
  { id: "CONTENTS/PID0", name: "Master Volume", value: 0.7, display: "-3.1 dB" },
  { id: "CONTENTS/PID3e8", name: "Filter Cutoff", value: 0.25, display: "440 Hz" },
  { id: "CONTENTS/PID3e9", name: "Unreadable", value: NaN, display: "?" },
]);
// The stand-in's clock, in real time, so the device request's wait for texts ends.
setInterval(() => bitwig.clock.advance(20), 20).unref();

const server = net.createServer((socket) => {
  let pending = Buffer.alloc(0);
  let receive = () => {};
  let disconnect = () => {};
  bitwig.attach({
    setReceiveCallback: (cb) => { receive = cb; },
    setDisconnectCallback: (cb) => { disconnect = cb; },
    send(bytes) {
      const body = Buffer.from(bytes);
      if (raw) {
        socket.write(body);
      } else {
        const head = Buffer.alloc(4);
        head.writeUInt32BE(body.length);
        socket.write(Buffer.concat([head, body]));
      }
    },
  });
  socket.on("data", (chunk) => {
    pending = Buffer.concat([pending, chunk]);
    while (pending.length >= 4 && pending.length >= 4 + pending.readUInt32BE(0)) {
      const length = pending.readUInt32BE(0);
      const body = pending.subarray(4, 4 + length);
      pending = pending.subarray(4 + length);
      receive(Array.from(body, (b) => (b > 127 ? b - 256 : b)));
    }
  });
  socket.on("close", () => disconnect());
  socket.on("error", () => {});
});
server.listen(0, "127.0.0.1", () => console.log("port " + server.address().port));
process.stdin.on("end", () => process.exit(0));
process.stdin.resume();
