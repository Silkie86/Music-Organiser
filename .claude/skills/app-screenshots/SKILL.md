---
name: app-screenshots
description: Photograph every page of the Music Organiser Avalonia app in a cloud session (virtual screen, demo library) to show Solomon UI changes or check a layout. Use when asked for screenshots, before/after pictures, or to see how a screen looks.
---

# App screenshots

Solomon often follows from a phone, away from the PC, so pictures are the only way they see the app.
`tools/screenshots/shoot.sh` builds the Avalonia app, starts it on a virtual screen (Xvfb, Linux only)
with a throwaway library, and saves one PNG per page.

```bash
tools/screenshots/shoot.sh /tmp/shots                 # Solomon's test projects from /mnt/project-files/bitwig-tests
tools/screenshots/shoot.sh /tmp/shots path/to/projects
SCREEN=1920x1080 WAIT=15 NO_BUILD=1 tools/screenshots/shoot.sh /tmp/shots
MIDI=path/to/mid/files tools/screenshots/shoot.sh /tmp/shots   # adds them as a MIDI library folder for the MIDI page
```

Output: `1-projects`, `2-project-selected`, `3-sounds`, `4-plugins`, `5-midi`, `6-settings`, then `7-` and `8-settings-more` further down (.png), plus `app.log`.

How it works, so you can extend it:
- The library lives in a temp folder (HOME and XDG_DATA_HOME point there). The project folder is copied and nine
  made-up WAVs are generated with ffmpeg, so nothing real is touched. Folders are added by writing `roots` and the
  `sample_folders` setting into `library.db` with sqlite3 after a first run creates it.
- Pages are reached by clicking sidebar items (36 px apart from y=91). All clicks use fixed coordinates
  (the window is moved to 0,0 and is 1440 × 900 because there's no window manager to maximise it). Look at a
  shot before trusting a new click position.
- Missing tools (xvfb, xdotool, imagemagick, sqlite3, ffmpeg) are installed with apt on first use.

Showing Solomon:
- Copy the PNGs into `/mnt/project-files/music-organiser-notes/screens/<topic>/` and attach them to the reply.
- For before/after, shoot on both commits and stack them (before on top): 
  `convert before.png after.png -resize 1200x -append compare.png`
- Read each image yourself first. MP3/FLAC rows have no waveform off Windows; that's a known gap, not a bug.
