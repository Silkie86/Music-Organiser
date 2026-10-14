@echo off
rem Builds the plugin scanner (mo-scan.exe) into native\out\app\bin with Visual Studio's C++ compiler.
rem Building the app runs this when the scanner's code changes; you can also run it by hand.
rem It needs Visual Studio 2022 or later with the "Desktop development with C++" workload.
setlocal
set "HERE=%~dp0"
set "OUT=%HERE%out\app"
set "LOG=%OUT%\build.log"
if not exist "%OUT%" mkdir "%OUT%"
call :build > "%LOG%" 2>&1
if errorlevel 1 goto failed
exit /b 0

:failed
rem Outside brackets, so a folder name with ( or ) in it can't break the echo.
echo The plugin scanner wasn't built. From %LOG%:
type "%LOG%"
exit /b 1

:build
set "VSWHERE=%ProgramFiles(x86)%\Microsoft Visual Studio\Installer\vswhere.exe"
if not exist "%VSWHERE%" (
  echo Visual Studio isn't installed: vswhere.exe wasn't found.
  exit /b 1
)
"%VSWHERE%" -latest -prerelease -products * -requires Microsoft.VisualStudio.Component.VC.Tools.x86.x64 -property installationPath > "%OUT%\vs.txt"
set "VS="
set /p VS=<"%OUT%\vs.txt"
if not defined VS (
  echo Visual Studio has no C++ compiler. In Visual Studio Installer, add the "Desktop development with C++" workload.
  exit /b 1
)
echo Using %VS%
rem 64-bit, to load 64-bit plugins (on Windows on Arm too, where most plugins are still x64).
call "%VS%\VC\Auxiliary\Build\vcvars64.bat"
if errorlevel 1 exit /b 1
where cmake
if errorlevel 1 (
  echo CMake wasn't found. In Visual Studio Installer, add "C++ CMake tools for Windows".
  exit /b 1
)
set "GENERATOR=NMake Makefiles"
where ninja >nul 2>nul && set "GENERATOR=Ninja"
rem Set up the build once. A setup that stopped part way (before the C++ workload was added, say) leaves
rem CMake's cache without the build files, so that one starts again from scratch.
if not exist "%OUT%\build.ninja" if not exist "%OUT%\Makefile" (
  if exist "%OUT%\CMakeCache.txt" del "%OUT%\CMakeCache.txt"
  if exist "%OUT%\CMakeFiles" rmdir /s /q "%OUT%\CMakeFiles"
  cmake -S "%HERE%." -B "%OUT%" -G "%GENERATOR%" -DCMAKE_BUILD_TYPE=Release -DBUILD_TESTING=OFF
  if errorlevel 1 exit /b 1
)
cmake --build "%OUT%" --target mo-scan
if errorlevel 1 exit /b 1
exit /b 0
