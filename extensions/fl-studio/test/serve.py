"""
Runs the FL Studio script in the stand-in FL Studio (fake_fl.py) for the app's own tests: it answers requests
in the link folder given, as FL Studio would. Changes are allowed. Prints "ready" once it's watching, and
stops when its input closes.

    python serve.py <link folder>
"""
import contextlib
import io
import os
import shutil
import sys
import tempfile
import threading
import time

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))

from fake_fl import load_script, night_drive  # noqa: E402


def main():
    link = sys.argv[1]
    installed = tempfile.mkdtemp()   # stands in for FL Studio's Settings/Hardware/Music Organiser
    with contextlib.redirect_stdout(io.StringIO()):   # what the script prints in FL Studio's script window
        script = load_script(night_drive(), installed, link)
    script.LET_THE_APP_MAKE_CHANGES = True
    done = threading.Event()

    def wait_for_input_to_close():
        sys.stdin.read()
        done.set()

    threading.Thread(target=wait_for_input_to_close, daemon=True).start()
    print("ready", flush=True)
    while not done.is_set():
        script.OnIdle()   # FL Studio calls it about every 20 ms
        time.sleep(0.02)
    shutil.rmtree(installed, ignore_errors=True)


if __name__ == "__main__":
    main()
