#!/usr/bin/env bash
# Photographs every page of the Avalonia app on a virtual screen, so Claude can show Solomon
# the app from a cloud session. Linux only (Xvfb). See .claude/skills/app-screenshots/SKILL.md.
#
# Usage: tools/screenshots/shoot.sh <output folder> [projects folder]
#   The projects folder defaults to /mnt/project-files/bitwig-tests (Solomon's test projects).
#   Env: SCREEN=1600x1000  WAIT=10 (seconds for the first scan)  NO_BUILD=1 (skip dotnet build)
#        MIDI=<folder of .mid files> (copied in and added as a MIDI library folder, for the MIDI page)
#        PLAYGROUND=<a playground idea .json> (opened on the Playground page)
set -euo pipefail

OUT=$(realpath -m "${1:?output folder}")
PROJECTS=${2:-/mnt/project-files/bitwig-tests}
SCREEN=${SCREEN:-1600x1000}
REPO=$(cd "$(dirname "$0")/../.." && pwd)
WORK=$(mktemp -d)
trap 'kill ${APP_PID:-} ${XVFB_PID:-} 2>/dev/null || true; rm -rf "$WORK"' EXIT
mkdir -p "$OUT"

# Tools: a virtual screen, a mouse/keyboard driver, a camera, the database CLI and a tone generator.
need=()
for t in Xvfb:xvfb xdotool:xdotool import:imagemagick sqlite3:sqlite3 ffmpeg:ffmpeg; do
  command -v "${t%%:*}" >/dev/null || need+=("${t##*:}")
done
if ((${#need[@]})); then apt-get install -y "${need[@]}" >/dev/null 2>&1 || { apt-get update >/dev/null && apt-get install -y "${need[@]}" >/dev/null; }; fi

if [[ -z ${NO_BUILD:-} ]]; then dotnet build "$REPO/src/MusicOrganiser.Avalonia" -v q -nologo >/dev/null; fi
APP="$REPO/src/MusicOrganiser.Avalonia/bin/Debug/net10.0/MusicOrganiser.Avalonia"

# A throwaway library: copies of the projects plus a few made-up sounds, so nothing real is touched.
mkdir -p "$WORK/Projects" "$WORK/Samples/Drums" "$WORK/Samples/Bass" "$WORK/Samples/FX" "$WORK/home"
[[ -d $PROJECTS ]] && cp -r "$PROJECTS"/. "$WORK/Projects/"
mkdir -p "$WORK/MIDI"; [[ -n ${MIDI:-} && -d $MIDI ]] && cp -r "$MIDI"/. "$WORK/MIDI/"
# Spread the copies over the years, so the list shows its time headings ("This week", "2025" …).
i=0; dates=(now "-2 days" "-2 months" "-5 months" "-11 months" "-3 years" "-7 years")
while IFS= read -r f; do touch -d "${dates[$((i % ${#dates[@]}))]}" "$f"; i=$((i + 1)); done \
  < <(find "$WORK/Projects" -type f \( -name '*.bwproject' -o -name '*.flp' -o -name '*.song' \) | sort)
tone() { ffmpeg -loglevel error -y -f lavfi -i "$1" ${2:+-af "$2"} "$WORK/Samples/$3"; }
tone "sine=f=55:d=0.5" "afade=t=out:st=0.05:d=0.45" "Drums/Kick 01.wav"
tone "sine=f=48:d=0.8" "afade=t=out:st=0.05:d=0.75" "Drums/Kick Deep.wav"
tone "anoisesrc=d=0.3:c=white" "afade=t=out:st=0.01:d=0.29" "Drums/Snare Tight.wav"
tone "anoisesrc=d=0.1:c=white" "highpass=f=7000,afade=t=out:st=0:d=0.1" "Drums/Hat Closed.wav"
tone "anoisesrc=d=0.5:c=pink" "highpass=f=5000,afade=t=out:st=0:d=0.5" "Drums/Hat Open.wav"
tone "sine=f=65.4:d=1.5" "" "Bass/Sub C1.wav"
tone "sine=f=110:d=1.2" "afade=t=out:st=0.3:d=0.9" "Bass/Reese A1.wav"
tone "anoisesrc=d=3:c=brown" "afade=t=in:d=2.5" "FX/Riser 01.wav"
tone "sine=f=880:d=2" "vibrato=f=6,afade=t=out:st=0.5:d=1.5" "FX/Sweep.wav"

export HOME="$WORK/home" XDG_DATA_HOME="$WORK/home/.local/share" DISPLAY=:99
Xvfb :99 -screen 0 "${SCREEN}x24" >/dev/null 2>&1 & XVFB_PID=$!
sleep 1

# First run creates the database; then point it at the throwaway folders.
"$APP" >/dev/null 2>&1 & APP_PID=$!; sleep 6; kill $APP_PID; wait $APP_PID 2>/dev/null || true
sqlite3 "$XDG_DATA_HOME/MusicOrganiser/library.db" \
  "INSERT OR IGNORE INTO roots(path) VALUES('$WORK/Projects');
   INSERT OR REPLACE INTO settings(key,value) VALUES('sample_folders','[\"$WORK/Samples\"]');
   INSERT OR REPLACE INTO settings(key,value) VALUES('midi_folders','[\"$WORK/MIDI\"]');"

if [[ -n ${PLAYGROUND:-} && -f $PLAYGROUND ]]; then
  mkdir -p "$XDG_DATA_HOME/MusicOrganiser/playground"; cp "$PLAYGROUND" "$XDG_DATA_HOME/MusicOrganiser/playground/current.json"
fi

"$APP" >"$OUT/app.log" 2>&1 & APP_PID=$!
sleep "${WAIT:-10}"
WIN=$(xdotool search --name "Music Organiser" | head -1)
xdotool windowmove "$WIN" 0 0; sleep 0.5

shot() { sleep "${2:-1.5}"; import -window root "$OUT/$1.png"; echo "$OUT/$1.png"; }
# Rail items are 62 px apart from y=106: Projects, Sounds, MIDI, Playground, Plugins (Settings sits at the bottom, y=750). Clicking moves the pointer away after, so no item looks hovered, but
# keeps it inside the window: with no window manager, keys go to whichever window the pointer is over.
nav() { local y=$((106 + $1 * 62)); [[ $1 == 5 ]] && y=750; xdotool mousemove 42 $y click 1 mousemove 1430 892; }
# The search box has the keyboard when the app opens: typing straight away searches; Down goes into the list.
xdotool type --delay 40 "drum"; xdotool key Down; shot 0-type-on-open
xdotool key ctrl+f; sleep 0.3; xdotool key Escape   # Ctrl+F back to the search box, Esc empties it
nav 0; shot 1-projects
xdotool mousemove 650 150 click 1; shot 2-project-selected
# Feedback: a rescan from the rail, mid-way (ring, spinner, activity card) and just after (the card's tick).
xdotool mousemove 42 863 click 1 mousemove 1430 892; shot 2a-scanning 0.05
shot 2b-scan-done 2.5
nav 1; shot 3-sounds 2
nav 4; shot 4-plugins 2
nav 2; shot 5-midi 4
nav 3; shot 5b-playground 3
# The playground's side panel: suggestions found, then melodies, the reharm ideas and writing parts.
xdotool mousemove 1371 380 click 1 mousemove 1430 892; shot 5c-playground-suggest 4
xdotool mousemove 1261 295 click 1 mousemove 1430 892; shot 5d-playground-melodies 4
xdotool mousemove 1206 244 click 1 mousemove 1430 892; shot 5e-playground-reharm 2
xdotool mousemove 1140 244 click 1 mousemove 1430 892; shot 5f-playground-write 1.5
nav 5; shot 6-settings 2
# The rest of Settings, a screen further down.
xdotool mousemove 800 500; for _ in $(seq 8); do xdotool click 5; done; xdotool mousemove 1430 892; shot 7-settings-more
xdotool mousemove 800 500; for _ in $(seq 8); do xdotool click 5; done; xdotool mousemove 1430 892; shot 8-settings-more
# The search box with text in it, showing its clear (X) button.
nav 0; sleep 1; xdotool key ctrl+f; sleep 0.3; xdotool type --delay 40 "test 2026"; xdotool mousemove 1430 892; shot 9-projects-search
xdotool key ctrl+a BackSpace
# Settings › Appearance: each theme (tiles four to a row), photographed on Projects; Studio again after.
theme() { local col=$(($1 % 4)) row=$(($1 / 4)); nav 5; sleep 1; xdotool mousemove $((240 + col * 210)) $((285 + row * 212)) click 1 mousemove 1430 892; }
shot 11-appearance 0.5
for t in 1 2 3 4 5 6 7 8; do
  theme $t; sleep 0.5; [[ $t == 4 ]] && shot "11-appearance-light" 0.5
  nav 0; sleep 1; xdotool mousemove 500 220 click 1 mousemove 1430 892; shot "12-theme-$t" 1
done
theme 0; nav 0; sleep 1
# A project's Presets window (a separate window, so it is photographed on its own), pointer on the first row.
xdotool mousemove 500 220 click 1; sleep 1; xdotool mousemove 1358 287 click 1; sleep 1
import -window root "$OUT/10a-more-menu.png"
xdotool key Down Down Return; sleep 3
PWIN=$(xdotool search --name "Presets" | tail -1)
if [[ -n $PWIN ]]; then
  xdotool windowmove "$PWIN" 0 0; sleep 0.5; xdotool mousemove 300 190; sleep 0.5
  import -window "$PWIN" "$OUT/10-presets.png"; echo "$OUT/10-presets.png"
fi
