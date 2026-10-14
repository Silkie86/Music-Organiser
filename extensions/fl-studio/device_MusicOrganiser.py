# name=Music Organiser
# supportedDevices=Music Organiser
"""
The Music Organiser link for FL Studio.

Lets the Music Organiser app ask FL Studio what it shows for the open project: the tempo, the channels,
the mixer and the plugins, and each plugin's parameters. The app uses this to check how it reads .flp
files and to learn what plugin settings mean.

FL Studio runs the script once it's the controller type of a MIDI input (Options > MIDI settings). A MIDI
input called "Music Organiser" (for example a loopMIDI port) gets it by itself. The app and the script talk
through files in the app's data folder (MusicOrganiser/FL Studio link, in AppData/Local on Windows and
Library/Application Support on a Mac): the app writes request.json, and the script answers in
response-<id>.json.

Nothing in the project changes unless LET_THE_APP_MAKE_CHANGES below is True.
"""

# True lets the app change plugin parameters. The app keeps this setting when it updates the script.
LET_THE_APP_MAKE_CHANGES = False

import channels
import general
import mixer
import plugins
import ui

try:
    import json
    import os
    import sys
    import time
except ImportError:  # a Python without its standard library can't read or write the request files
    json = None

LINK_VERSION = 1
SCRIPT = "device_MusicOrganiser.py"
POLL_SECONDS = 0.2    # how often to look for a request
STALE_SECONDS = 15    # a request older than this is one the app gave up on
PAGE_SECONDS = 0.04   # how long one list of parameters may take, so FL Studio never stalls
SLOTS = 10            # effect slots on each mixer track
VST_PARAMS = 4096     # VST plugins list 4240 parameters: their own 4096, then MIDI CC and aftertouch
CHANGES_OFF = ("Changes are off. To let the app change FL Studio, set LET_THE_APP_MAKE_CHANGES = True in "
               "device_MusicOrganiser.py (in FL Studio's Settings, Hardware, Music Organiser folder), then "
               "reload the script.")


class LinkError(Exception):
    """A problem with a request, told to the app in these words."""


_link = None     # the folder requests and answers go through, while the link is on
_script = None   # the folder FL Studio loaded the script from
_next_poll = 0.0


def OnInit():
    global _link, _script
    if json is None:
        print("Music Organiser: this FL Studio's Python can't read or write files, which the link needs.")
        return
    _script = script_folder()
    _link = link_folder()
    print("Music Organiser link " + str(LINK_VERSION) + " is on, through " + _link)


def OnDeInit():
    global _link
    _link = None


def OnIdle():
    global _next_poll
    if _link is None:
        return
    now = time.time()
    if now < _next_poll:
        return
    _next_poll = now + POLL_SECONDS
    poll(now)


def link_folder(platform=None, environ=None, home=None):
    """The app's data folder for the link, the same wherever FL Studio keeps its own files."""
    platform = platform or sys.platform
    environ = os.environ if environ is None else environ
    home = home or os.path.expanduser("~")
    if platform == "win32":
        base = environ.get("LOCALAPPDATA") or os.path.join(home, "AppData", "Local")
    elif platform == "darwin":
        base = os.path.join(home, "Library", "Application Support")
    else:
        base = environ.get("XDG_DATA_HOME") or os.path.join(home, ".local", "share")
    return os.path.join(base, "MusicOrganiser", "FL Studio link")


def script_folder():
    """
    The folder FL Studio loaded this script from, or None if it can't tell. FL Studio puts the folder on the
    module path, and scripts there don't get __file__.
    """
    try:
        here = os.path.dirname(os.path.abspath(__file__))
        if os.path.isfile(os.path.join(here, SCRIPT)):
            return here
    except NameError:
        pass
    for folder in sys.path:
        if isinstance(folder, str) and folder and os.path.isfile(os.path.join(folder, SCRIPT)):
            return os.path.abspath(folder)
    return None


def poll(now):
    """Answers the app's request, if there is one."""
    path = os.path.join(_link, "request.json")
    if not os.path.exists(path):
        return
    try:
        with open(path, "rb") as f:
            data = f.read()
        os.remove(path)
    except OSError:  # gone already: the app stopped waiting and took it back
        return
    reply = answer(data, now)
    if reply is None:
        return
    try:
        write(reply)
    except OSError as e:
        print("Music Organiser: couldn't answer the app (" + str(e) + ")")


def answer(data, now):
    """The reply to one request, or None for something that isn't a request (or is too old to answer)."""
    try:
        request = json.loads(data.decode("utf-8-sig"))
    except ValueError:
        return None
    if not isinstance(request, dict) or not is_whole(request.get("id")):
        return None
    sent = request.get("sent")
    if is_number(sent) and now - sent > STALE_SECONDS:
        return None
    reply = {"id": request["id"]}
    try:
        command = COMMANDS.get(request.get("cmd"))
        if command is None:
            raise LinkError("Unknown command: " + str(request.get("cmd")))
        reply["result"] = command(request)
        reply["ok"] = True
    except LinkError as e:
        reply["ok"] = False
        reply["error"] = str(e)
    except Exception as e:  # FL Studio turned a call down; the app shows why
        reply["ok"] = False
        reply["error"] = "FL Studio couldn't answer (" + type(e).__name__ + ": " + str(e) + ")"
    return reply


def write(reply):
    """Writes a reply where the app looks for it, all at once."""
    name = os.path.join(_link, "response-" + str(reply["id"]) + ".json")
    try:
        text = json.dumps(reply, ensure_ascii=True, allow_nan=False)
    except ValueError as e:
        text = json.dumps({"id": reply["id"], "ok": False, "error": "FL Studio's answer couldn't be sent: " + str(e)})
    with open(name + ".tmp", "w", encoding="ascii") as f:
        f.write(text)
    os.replace(name + ".tmp", name)


# Commands

def hello(request):
    return {
        "link": LINK_VERSION,
        "product": "FL Studio",
        "version": ui.getVersion(4),
        "api": general.getVersion(),
        "title": ui.getProgTitle(),
        "changes": LET_THE_APP_MAKE_CHANGES,
        "platform": sys.platform,
        "script": _script,
    }


def project(request):
    api = needs_api(26)
    return {
        "title": ui.getProgTitle(),
        "tempo": tempo(),
        "modified": general.getChangedFlag() != 0,
        # Before scripting version 33, FL Studio only lists the channels in the group the channel rack shows.
        "grouped": api < 33,
        "channels": [channel(ref, index) for ref, index in channel_list(api)],
        # The last mixer track is FL Studio's "current" track, not one of the project's.
        "mixer": [mixer_track(t) for t in range(mixer.trackCount() - 1)],
    }


def params(request):
    """A plugin's named parameters, from "from" on: as many as fit in PAGE_SECONDS, with where to go on."""
    needs_api(26)
    target, _ = plugin_at(request)
    count = plugins.getParamCount(*target)
    end = VST_PARAMS if count == VST_PARAMS + 144 and request.get("midi") is not True else count
    start = request.get("from", 0)
    if not is_whole(start) or start < 0:
        raise LinkError('"from" must be a parameter number.')
    found = []
    stop = time.time() + PAGE_SECONDS
    p = start
    while p < end:
        name = plugins.getParamName(p, *target)
        if name:
            found.append(param(p, name, target))
        p += 1
        if time.time() > stop:
            break
    return {
        "plugin": plugins.getPluginName(target[0], target[1], False, target[2]),
        "count": count,
        "params": found,
        "next": p if p < end else None,
    }


def set_param(request):
    """Sets a parameter (0 to 1), and says what it became."""
    if not LET_THE_APP_MAKE_CHANGES:
        raise LinkError(CHANGES_OFF)
    needs_api(26)
    target, where = plugin_at(request)
    p = request.get("param")
    if not is_whole(p) or p < 0 or p >= plugins.getParamCount(*target) or not plugins.getParamName(p, *target):
        raise LinkError("There's no parameter " + str(p) + " on " + where + ".")
    value = request.get("value")
    if not is_number(value) or value < 0 or value > 1:
        raise LinkError('"value" must be a number from 0 to 1.')
    plugins.setParamValue(float(value), p, target[0], target[1], 0, target[2])
    return param(p, plugins.getParamName(p, *target), target)


COMMANDS = {"hello": hello, "project": project, "params": params, "set": set_param}


# What FL Studio shows

def channel_list(api):
    """Each channel as (its index for the channels module, its global index), in channel rack order."""
    if api >= 33:
        return [((i, True), i) for i in range(channels.channelCount(True))]
    return [((i,), channels.getChannelIndex(i)) for i in range(channels.channelCount())]


def channel(ref, index):
    info = {
        "index": index,
        "name": channels.getChannelName(*ref),
        "type": channels.getChannelType(*ref),
        "mixer": channels.getTargetFxTrack(*ref),
        "plugin": None,
        "params": 0,
    }
    if plugins.isValid(index, -1, True):
        info["plugin"] = plugins.getPluginName(index, -1, False, True)
        info["params"] = plugins.getParamCount(index, -1, True)
    return info


def mixer_track(t):
    slots = []
    for s in range(SLOTS):
        if mixer.isTrackPluginValid(t, s):
            slots.append({
                "slot": s,
                "name": plugins.getPluginName(t, s),
                "label": plugins.getPluginName(t, s, True),
                "params": plugins.getParamCount(t, s),
            })
    return {"index": t, "name": mixer.getTrackName(t), "plugins": slots}


def param(p, name, target):
    index, slot, by_global = target
    return {
        "index": p,
        "name": name,
        "value": num(plugins.getParamValue(p, index, slot, by_global)),
        "text": plugins.getParamValueString(p, index, slot, 0, by_global),
    }


def plugin_at(request):
    """The plugin a request names, as (index, slot, useGlobalIndex) for the plugins module, and in words."""
    if "channel" in request:
        c = request["channel"]
        if not is_whole(c) or c < 0 or c >= channels.channelCount(True):
            raise LinkError("There's no channel " + str(c) + ".")
        target, where = (c, -1, True), "channel " + str(c)
    elif "track" in request:
        t, s = request["track"], request.get("slot")
        if not is_whole(t) or t < 0 or t >= mixer.trackCount() - 1 or not is_whole(s) or s < 0 or s >= SLOTS:
            raise LinkError("There's no mixer track " + str(t) + " with a slot " + str(s) + ".")
        target, where = (t, s, False), "mixer track " + str(t) + ", slot " + str(s)
    else:
        raise LinkError('Say which plugin: a "channel", or a mixer "track" and "slot".')
    if not plugins.isValid(*target):
        raise LinkError("There's no plugin on " + where + ".")
    return target, where


def tempo():
    t = mixer.getCurrentTempo()
    if not is_number(t):
        return None
    return t / 1000.0 if t > 1000 else float(t)  # some versions give thousandths of a BPM


def needs_api(least):
    api = general.getVersion()
    if api < least:
        raise LinkError("This needs a newer FL Studio: its scripting version is " + str(api) + ", and the link needs "
                        + str(least) + " or later.")
    return api


def is_whole(v):
    return isinstance(v, int) and not isinstance(v, bool)


def is_number(v):
    return isinstance(v, (int, float)) and not isinstance(v, bool) and v == v and abs(v) != float("inf")


def num(v):
    """A number JSON can carry, or None."""
    return float(v) if is_number(v) else None
