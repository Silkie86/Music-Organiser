"""
Runs the FL Studio script in a stand-in FL Studio (fake_fl.py) and talks to it the way the app does:
a request file in, a response file out.  python -m unittest discover -s extensions/fl-studio/test
"""
import io
import json
import os
import shutil
import tempfile
import unittest
from contextlib import redirect_stdout

from fake_fl import SCRIPT, Clock, compile_script, load_script, night_drive


class Link:
    """The script in a folder of its own, as installed, with a clock the checks move on."""

    def __init__(self, studio, changes=False):
        self.studio = studio
        self.installed = tempfile.mkdtemp()   # the script's folder in FL Studio's Settings/Hardware
        self.folder = tempfile.mkdtemp()      # the link's folder, where requests and answers go
        self.printed = io.StringIO()
        with redirect_stdout(self.printed):
            self.script = load_script(studio, self.installed, self.folder)
        self.script.LET_THE_APP_MAKE_CHANGES = changes
        self.clock = Clock()
        self.script.time = self.clock
        self.last = 0

    def close(self):
        shutil.rmtree(self.installed, ignore_errors=True)
        shutil.rmtree(self.folder, ignore_errors=True)

    def send(self, request, raw=None):
        path = os.path.join(self.folder, "request.json")
        with open(path, "wb") as f:
            f.write(raw if raw is not None else json.dumps(request).encode("utf-8"))

    def idle(self, seconds=None):
        """FL Studio's idle call, once the script's polling time has come round."""
        self.clock.now += self.script.POLL_SECONDS if seconds is None else seconds
        self.script.OnIdle()

    def answers(self):
        return sorted(n for n in os.listdir(self.folder) if n.startswith("response-"))

    def ask(self, request):
        """Sends a request and returns the one reply to it."""
        self.last += 1
        request = dict({"id": self.last, "sent": self.clock.now}, **request)
        self.send(request)
        self.idle()
        path = os.path.join(self.folder, "response-" + str(request["id"]) + ".json")
        with open(path, "rb") as f:
            data = f.read()
        os.remove(path)
        self.assert_ascii(data)
        reply = json.loads(data)
        assert reply["id"] == request["id"]
        return reply

    @staticmethod
    def assert_ascii(data):
        assert all(b < 128 for b in data), "replies should be plain ASCII"

    def result(self, request):
        reply = self.ask(request)
        assert reply["ok"], reply
        return reply["result"]

    def error(self, request):
        reply = self.ask(request)
        assert not reply["ok"], reply
        return reply["error"]


class LinkFolder(unittest.TestCase):
    """The script and the app agree on the link's folder, wherever FL Studio keeps its own files."""

    def setUp(self):
        self.script = compile_script(night_drive())

    def test_uses_local_app_data_on_windows(self):
        local = "C:\\Users\\Solomon\\AppData\\Local"
        self.assertEqual(self.script.link_folder("win32", {"LOCALAPPDATA": local}, "C:\\Users\\Solomon"),
                         os.path.join(local, "MusicOrganiser", "FL Studio link"))

    def test_uses_application_support_on_a_mac(self):
        self.assertEqual(self.script.link_folder("darwin", {}, "/Users/solomon"),
                         os.path.join("/Users/solomon", "Library", "Application Support", "MusicOrganiser", "FL Studio link"))


class ScriptFile(unittest.TestCase):
    def test_starts_with_the_name_fl_studio_lists(self):
        with open(SCRIPT, encoding="utf-8") as f:
            lines = f.read().splitlines()
        self.assertEqual(lines[0], "# name=Music Organiser")
        self.assertIn("# supportedDevices=Music Organiser", lines[:5])
        self.assertTrue(os.path.basename(SCRIPT).startswith("device_"))

    def test_changes_are_off_as_shipped(self):
        with open(SCRIPT, encoding="utf-8") as f:
            self.assertIn("\nLET_THE_APP_MAKE_CHANGES = False\n", f.read())

    def test_is_plain_ascii(self):
        # FL Studio reads the header lines itself, so the file stays readable in any encoding.
        with open(SCRIPT, "rb") as f:
            f.read().decode("ascii")


class LinkTests(unittest.TestCase):
    def link(self, studio=None, changes=False):
        link = Link(studio or night_drive(), changes)
        self.addCleanup(link.close)
        return link

    def test_says_where_fl_studio_loaded_it_from_without_file(self):
        link = self.link()
        self.assertEqual(link.result({"cmd": "hello"})["script"], os.path.abspath(link.installed))
        self.assertIn("Music Organiser link 1 is on, through " + link.folder, link.printed.getvalue())

    def test_says_hello(self):
        link = self.link()
        hello = link.result({"cmd": "hello"})
        self.assertEqual(hello["product"], "FL Studio")
        self.assertEqual((hello["link"], hello["api"], hello["changes"]), (1, 37, False))
        self.assertEqual(hello["version"], "Producer Edition v25.1.6 [build 4997]")
        self.assertEqual(hello["title"], "Night Drive - FL Studio 2025")
        self.assertEqual(hello["modified"], False)

    def test_says_when_the_project_has_unsaved_changes(self):
        # The app watches this to re-read a project as soon as it's saved.
        studio = night_drive(modified=1)
        link = self.link(studio)
        self.assertEqual(link.result({"cmd": "hello"})["modified"], True)
        studio.modified = 0
        self.assertEqual(link.result({"cmd": "hello"})["modified"], False)

    def test_lists_the_channels_and_the_mixer(self):
        link = self.link()
        p = link.result({"cmd": "project"})
        self.assertEqual((p["tempo"], p["modified"], p["grouped"]), (124.0, False, False))
        self.assertEqual([c["name"] for c in p["channels"]], ["Kick", "Bass", "Chops", "Café pad"])
        kick, bass = p["channels"][0], p["channels"][1]
        self.assertEqual(kick, {"index": 0, "name": "Kick", "type": 0, "mixer": 1, "plugin": None, "params": 0})
        self.assertEqual((bass["plugin"], bass["params"], bass["type"], bass["mixer"]), ("Serum", 4240, 2, 2))
        self.assertEqual([t["name"] for t in p["mixer"]], ["Master", "Kick", "Bass"])
        self.assertEqual(p["mixer"][2]["plugins"], [
            {"slot": 0, "name": "Fruity Parametric EQ 2", "label": "Fruity Parametric EQ 2", "params": 1},
            {"slot": 3, "name": "Fruity Reeverb 2", "label": "Room", "params": 1},
        ])

    def test_reads_the_tempo_either_way_fl_studio_gives_it(self):
        self.assertEqual(self.link(night_drive(tempo=93.5)).result({"cmd": "project"})["tempo"], 93.5)
        self.assertEqual(self.link(night_drive(tempo=140000)).result({"cmd": "project"})["tempo"], 140.0)

    def test_older_fl_studio_lists_the_channels_the_rack_shows(self):
        link = self.link(night_drive(api=30, group=[1, 3]))
        p = link.result({"cmd": "project"})
        self.assertTrue(p["grouped"])
        self.assertEqual([(c["index"], c["name"], c["plugin"]) for c in p["channels"]],
                         [(1, "Bass", "Serum"), (3, "Café pad", "FLEX")])

    def test_needs_a_scripting_version_with_global_plugin_indexes(self):
        link = self.link(night_drive(api=25))
        self.assertIn("needs a newer FL Studio", link.error({"cmd": "project"}))
        self.assertEqual(link.result({"cmd": "hello"})["api"], 25)

    def test_lists_a_plugins_parameters_in_pages(self):
        link = self.link()
        link.clock.step = 0.001   # each call takes a millisecond, so a list runs over several requests
        found, pages, start = [], 0, 0
        while start is not None:
            page = link.result({"cmd": "params", "channel": 1, "from": start})
            self.assertEqual((page["plugin"], page["count"]), ("Serum", 4240))
            found += page["params"]
            pages += 1
            start = page["next"]
        self.assertGreater(pages, 1)
        self.assertEqual(found, [
            {"index": 0, "name": "Master Volume", "value": 0.7, "text": "-3.1 dB"},
            {"index": 1, "name": "A Octave", "value": 0.5, "text": "0"},
            {"index": 3, "name": "Filter Cutoff", "value": 0.25, "text": "440 Hz"},
        ])

    def test_lists_midi_cc_parameters_when_asked(self):
        link = self.link()
        page = link.result({"cmd": "params", "channel": 1, "from": 4000, "midi": True})
        while page["next"] is not None:
            more = link.result({"cmd": "params", "channel": 1, "from": page["next"], "midi": True})
            page["params"] += more["params"]
            page["next"] = more["next"]
        self.assertEqual(len(page["params"]), 128)
        self.assertEqual(page["params"][0]["name"], "MIDI CC #0")

    def test_lists_a_mixer_plugins_parameters(self):
        link = self.link()
        page = link.result({"cmd": "params", "track": 2, "slot": 3})
        self.assertEqual(page, {"plugin": "Fruity Reeverb 2", "count": 1, "next": None,
                                "params": [{"index": 0, "name": "Mix", "value": 0.3, "text": "30%"}]})

    def test_changes_are_off_unless_turned_on(self):
        link = self.link()
        self.assertIn("Changes are off", link.error({"cmd": "set", "channel": 2, "param": 0, "value": 0.5}))
        self.assertEqual(link.studio.channels[2].plugin.sets, [])

    def test_sets_a_parameter_and_says_what_it_became(self):
        link = self.link(changes=True)
        self.assertTrue(link.result({"cmd": "hello"})["changes"])
        self.assertEqual(link.result({"cmd": "set", "channel": 2, "param": 0, "value": 0.25}),
                         {"index": 0, "name": "Main volume", "value": 0.25, "text": "25%"})
        self.assertEqual(link.result({"cmd": "set", "track": 0, "slot": 0, "param": 0, "value": 1}),
                         {"index": 0, "name": "Gain", "value": 1.0, "text": "100%"})
        self.assertEqual(link.studio.channels[2].plugin.sets, [(0, 0.25)])

    def test_says_what_is_wrong_with_a_request(self):
        link = self.link(changes=True)
        self.assertEqual(link.error({"cmd": "dance"}), "Unknown command: dance")
        self.assertIn("Say which plugin", link.error({"cmd": "params"}))
        self.assertEqual(link.error({"cmd": "params", "channel": 9}), "There's no channel 9.")
        self.assertEqual(link.error({"cmd": "params", "channel": 0}), "There's no plugin on channel 0.")
        self.assertEqual(link.error({"cmd": "params", "track": 1, "slot": 0}), "There's no plugin on mixer track 1, slot 0.")
        self.assertIn("There's no mixer track 3", link.error({"cmd": "params", "track": 3, "slot": 0}))
        self.assertIn("There's no mixer track 2 with a slot 10", link.error({"cmd": "params", "track": 2, "slot": 10}))
        self.assertIn("must be a parameter number", link.error({"cmd": "params", "channel": 1, "from": -1}))
        self.assertEqual(link.error({"cmd": "set", "channel": 1, "param": 2, "value": 0.5}),
                         "There's no parameter 2 on channel 1.")
        self.assertIn("from 0 to 1", link.error({"cmd": "set", "channel": 1, "param": 0, "value": 2}))
        self.assertIn("from 0 to 1", link.error({"cmd": "set", "channel": 1, "param": 0, "value": True}))

    def test_passes_on_fl_studios_own_errors(self):
        link = self.link()
        link.studio.broken = "getPluginName"
        self.assertEqual(link.error({"cmd": "project"}),
                         "FL Studio couldn't answer (TypeError: Operation unsafe at current time)")

    def test_ignores_what_isnt_a_request(self):
        link = self.link()
        for raw in [b"not json", b"[1, 2]", b'{"cmd": "hello"}', b'{"id": "seven", "cmd": "hello"}']:
            link.send(None, raw=raw)
            link.idle()
            self.assertFalse(os.path.exists(os.path.join(link.folder, "request.json")))
            self.assertEqual(link.answers(), [])

    def test_ignores_a_request_the_app_gave_up_on(self):
        link = self.link()
        link.send({"id": 5, "cmd": "hello", "sent": link.clock.now - 60})
        link.idle()
        self.assertEqual(link.answers(), [])
        self.assertEqual(link.ask({"cmd": "hello"})["ok"], True)

    def test_looks_for_requests_every_fifth_of_a_second(self):
        link = self.link()
        link.send({"id": 1, "cmd": "hello", "sent": link.clock.now})
        link.idle()
        link.send({"id": 2, "cmd": "hello", "sent": link.clock.now})
        link.idle(0.02)
        self.assertEqual(link.answers(), ["response-1.json"])
        link.idle(0.2)
        self.assertEqual(link.answers(), ["response-1.json", "response-2.json"])

    def test_keeps_names_intact_in_plain_ascii(self):
        link = self.link()
        p = link.result({"cmd": "project"})
        self.assertEqual(p["channels"][3]["name"], "Café pad")

    def test_answers_a_request_written_with_a_byte_order_mark(self):
        link = self.link()
        link.send(None, raw=b'\xef\xbb\xbf{"id": 3, "cmd": "hello"}')
        link.idle()
        self.assertEqual(link.answers(), ["response-3.json"])

    def test_stays_quiet_without_file_access(self):
        link = self.link()
        link.script.json = None
        link.script.OnDeInit()
        printed = io.StringIO()
        with redirect_stdout(printed):
            link.script.OnInit()
        self.assertIn("can't read or write files", printed.getvalue())
        link.send({"id": 1, "cmd": "hello", "sent": link.clock.now})
        link.idle()
        self.assertEqual(link.answers(), [])

    def test_does_nothing_once_fl_studio_lets_it_go(self):
        link = self.link()
        link.script.OnDeInit()
        link.send({"id": 1, "cmd": "hello", "sent": link.clock.now})
        link.idle()
        self.assertTrue(os.path.exists(os.path.join(link.folder, "request.json")))


if __name__ == "__main__":
    unittest.main()
