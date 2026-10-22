---
name: playground-check
description: Check the Music Organiser Playground (mini DAW) after a change: run its tests, photograph it with demo MIDI in the cloud, and list what still needs Solomon's PC.
---

# Playground check

Use after any change to the Playground page (PlaygroundView, PlaygroundViewModel, TimelineView, PianoRollEditor,
SectionMatcher, shortcuts), before telling Solomon it's ready. Solomon is a beginner and often on his phone, so the
result he sees is pictures plus a short list, never a wall of test output.

## 1. Tests (about a minute)

```bash
dotnet test tests/MusicOrganiser.Core.Tests --filter "FullyQualifiedName~Playground"
dotnet test tests/MusicOrganiser.Avalonia.Tests --filter "FullyQualifiedName~Timeline|FullyQualifiedName~Shortcut|FullyQualifiedName~PianoRoll|FullyQualifiedName~Playground"
```

The Avalonia ones are headless click-through tests (no screen needed). A UI bug that was found by hand gets a
headless test on the real page (PlaygroundPageTests) that fails before the fix.
Keys show as Cmd on macOS in CI: compare with `ShortcutsViewModel.Display(...)`.
Before pushing, run both full test projects, not just the filter.

## 2. Pictures with demo MIDI

Demo files live in the shared folder: /mnt/project-files/music-organiser-notes/test-files/playground-demo
(`midi/` = small made-up gospel/jazz chord, bass, melody and drum parts; `playground.json` = a 16-bar idea
"Sunday sketch" with chords, walking bass and a swing kit; `DemoGen.cs.txt` = the test that made them, to make more).
Never use Solomon's own MIDI packs here.

Write the clicks to a steps file (xdotool lines plus `shot <name> <seconds to wait>`), then:

```bash
D=/mnt/project-files/music-organiser-notes/test-files/playground-demo
SCREEN=1440x900 MIDI=$D/midi PLAYGROUND=$D/playground.json PLAYGROUND_STEPS=steps.sh \
  tools/screenshots/shoot.sh <scratchpad>/check
```

It opens the MIDI page first (so parts are read for Suggest), then the Playground (`5b-playground`), then your
steps. Positions that worked at 1440x900 (check a shot before trusting a new one):
- `?` key: shortcut cheat sheet; Escape closes it.
- Suggest's Find button 1371,380; side tabs along y=240 (Suggest 1058, Write 1124, Groove 1188, Reharm 1259, Song 1324).
- Timeline tracks start at y=360, 46 px apart; ruler bar numbers at y=341; piano roll below y=690.
- Move the mouse to 1430,892 before each shot so no hover hides anything.

Read every picture yourself before sending it. Put the pictures on the Playground Artifact page
(https://claude.ai/artifact/HvD3dHocBgXdhi3aehSwjc, a new section, same URL), not as loose attachments.
Off Windows there's no real audio device, so Play shows but isn't heard.

## 3. What only his PC can check

End with a short list of what the cloud can't check: sound (playing, General MIDI sounds), real keyboard and
mouse feel (drags, Shift for no snap, scroll zoom), dragging into Bitwig/FL Studio, speed with his full library
(16,728 projects, 357,528 sounds). Send that list to the "Testing and merge plan" thread, which runs home testing.
When he's at the PC, computer use can run the list for him (he taps Allow once for the app); check list_devices first.
