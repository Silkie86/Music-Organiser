"""
Runs the FL Studio script in the stand-in FL Studio (fake_fl.py) for the app's own tests: it answers requests
in the link folder given, as FL Studio would. Changes are allowed. Prints "ready" once it's watching, and
stops when its input closes. Each line of input changes the stand-in, as if done in FL Studio:
{"title": "Other Song - FL Studio 2025", "modified": 1} opens another project or edits this one,
{"channels": [["Lead", "Serum"], ["Kick", null]]} puts these channels in the channel rack (plugins it has
keep their knobs), and {"knob": [channel, param, value]} turns a knob. It prints "ok" once the change is made.

    python serve.py <link folder>
"""
import contextlib
import io
import json
import os
import shutil
import sys
import tempfile
import threading
import time

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))

from fake_fl import Channel, Plugin, load_script, night_drive  # noqa: E402


def main():
    link = sys.argv[1]
    installed = tempfile.mkdtemp()   # stands in for FL Studio's Settings/Hardware/Music Organiser
    studio = night_drive()
    with contextlib.redirect_stdout(io.StringIO()):   # what the script prints in FL Studio's script window
        script = load_script(studio, installed, link)
    script.LET_THE_APP_MAKE_CHANGES = True
    done = threading.Event()
    lock = threading.Lock()

    def follow_input():
        for line in sys.stdin:
            if not line.strip():
                continue
            change = json.loads(line)
            with lock:
                if "title" in change:
                    studio.title = change["title"]
                if "modified" in change:
                    studio.modified = change["modified"]
                if "channels" in change:
                    known = {c.plugin.name: c.plugin for c in studio.channels if c.plugin}
                    studio.channels = [
                        Channel(name, kind=2 if plugin else 0, mixer=i + 1,
                                plugin=(known.get(plugin) or Plugin(plugin)) if plugin else None)
                        for i, (name, plugin) in enumerate(change["channels"])]
                    studio.group = list(range(len(studio.channels)))
                if "knob" in change:
                    channel, param, value = change["knob"]
                    studio.channels[channel].plugin.params[param][1] = value
            print("ok", flush=True)
        done.set()

    threading.Thread(target=follow_input, daemon=True).start()
    print("ready", flush=True)
    while not done.is_set():
        with lock:
            script.OnIdle()   # FL Studio calls it about every 20 ms
        time.sleep(0.02)
    shutil.rmtree(installed, ignore_errors=True)


if __name__ == "__main__":
    main()
