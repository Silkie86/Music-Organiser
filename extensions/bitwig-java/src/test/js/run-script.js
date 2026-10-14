// Runs the JavaScript controller script in its own stand-in Bitwig (extensions/bitwig/test/fake-bitwig.js)
// through steps read from stdin, and prints every message that came back after each step, so ParityTest can
// check the Java extension answers exactly as the script does.
//
//   node run-script.js <path to fake-bitwig.js>  < [{ "model": {...}, "steps": [...] }, ...]
//
// A step is one of: { "send": <request, sent as JSON> }, { "raw": "<text, sent as it is>" },
// { "advance": <ms> }, { "allowChanges": true }, { "show": { "device": {...}, "knobs": [...] } }, { "close": true }.
"use strict";

const { startBitwig } = require(process.argv[2]);

let input = "";
process.stdin.setEncoding("utf8");
process.stdin.on("data", (chunk) => { input += chunk; });
process.stdin.on("end", () => {
  const runs = JSON.parse(input).map(({ model, steps }) => {
    // As in Bitwig, where the script makes a java.util.UUID for a Bitwig device's ID.
    const bitwig = startBitwig({ ...model, java: true });
    const app = bitwig.connect();
    const out = [app.take()];
    for (const step of steps) {
      if ("send" in step) app.send(JSON.stringify(step.send));
      else if ("raw" in step) app.send(step.raw);
      else if ("advance" in step) bitwig.clock.advance(step.advance);
      else if ("allowChanges" in step) bitwig.allowChanges(step.allowChanges);
      else if ("show" in step) bitwig.cursorDevice.show(step.show.device, step.show.knobs);
      else if ("close" in step) app.close();
      else throw new Error("Unknown step " + JSON.stringify(step));
      out.push(app.take());
    }
    return out;
  });
  process.stdout.write(JSON.stringify(runs));
});
