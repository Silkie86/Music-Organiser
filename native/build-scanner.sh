#!/bin/sh
# Builds the plugin scanner (mo-scan) into native/out/app/bin. Building the app runs this when the
# scanner's code changes; you can also run it by hand. It needs CMake and a C++ compiler: on a Mac,
# Xcode's command line tools (xcode-select --install) and CMake (brew install cmake).
here=$(cd "$(dirname "$0")" && pwd)
out="$here/out/app"
log="$out/build.log"
mkdir -p "$out"

# Apps started from the Dock or an IDE don't always have Homebrew on their PATH.
cmake=$(command -v cmake)
for c in /opt/homebrew/bin/cmake /usr/local/bin/cmake /Applications/CMake.app/Contents/bin/cmake; do
  if [ -z "$cmake" ] && [ -x "$c" ]; then cmake=$c; fi
done
if [ -z "$cmake" ]; then
  echo "CMake wasn't found." | tee "$log"
  exit 1
fi

(
  if [ ! -f "$out/CMakeCache.txt" ]; then
    "$cmake" -S "$here" -B "$out" -DCMAKE_BUILD_TYPE=Release -DBUILD_TESTING=OFF || exit 1
  fi
  "$cmake" --build "$out" --target mo-scan --parallel
) > "$log" 2>&1
status=$?
if [ $status -ne 0 ]; then
  echo "The plugin scanner wasn't built. The end of $log:"
  tail -n 30 "$log"
fi
exit $status
