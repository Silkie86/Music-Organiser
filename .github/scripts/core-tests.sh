#!/usr/bin/env bash
# Runs one group of the Core tests, for the core job in build.yml: core-tests.sh links|live|viewmodels|sounds|rest
#
# Why groups: on macOS the runner has sometimes been lost a few minutes into these tests, and then GitHub keeps no log.
# It does keep which step was running, so with each group in its own step the next loss says which group it was.
# "rest" is every test the other groups don't name, so a new test class is never left out.
set -eo pipefail
group=$1

links="BitwigLinkTests FlLinkTests DawWatchTests BitwigSendTests DawScriptTests"
live="BitwigLiveTestsTests LiveRunnerTests LiveTestsViewModelTests MoProbeLiveTests ProbeKnobCheckTests PluginScannerTests"
viewmodels="ViewModelTests"
sounds="ReferenceTests MusicTheoryTests SoundAnalyzerTests LearningTests AudioFileTests SoundOutputSettingTests MidiRenderTests MidiDragExportTests"

# A test's full name is MusicOrganiser.Core.Tests.<class>.<test>; the dot after the class keeps
# LiveTestsTests from matching BitwigLiveTestsTests.
any() { local f=""; for c in $1; do f="$f${f:+|}FullyQualifiedName~MusicOrganiser.Core.Tests.$c."; done; echo "$f"; }
none() { local f=""; for c in $1; do f="$f${f:+&}FullyQualifiedName!~MusicOrganiser.Core.Tests.$c."; done; echo "$f"; }

case $group in
  links) filter=$(any "$links") ;;
  live) filter=$(any "$live") ;;
  viewmodels) filter=$(any "$viewmodels") ;;
  sounds) filter=$(any "$sounds") ;;
  rest) filter=$(none "$links $live $viewmodels $sounds") ;;
  *) echo "Unknown group: $group" >&2; exit 2 ;;
esac

# On macOS, also say how much memory the biggest program in the run took at its peak.
measure=()
if [ "$RUNNER_OS" = "macOS" ]; then measure=(/usr/bin/time -l); fi

# A test that hangs fails after 5 minutes and is named; each test is named as it passes.
${measure[@]+"${measure[@]}"} dotnet test tests/MusicOrganiser.Core.Tests -c Release --no-build --filter "$filter" \
  --blame-hang-timeout 5m --blame-hang-dump-type none \
  --logger "console;verbosity=normal" --results-directory "TestResults/$group"
