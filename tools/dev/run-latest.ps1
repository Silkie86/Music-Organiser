<#
  Runs the newest Music Organiser (the Avalonia app) straight from GitHub, without Visual Studio, and keeps it up
  to date while it runs.

  The first run sets itself up in %LOCALAPPDATA%\MusicOrganiser\run-latest: a copy of this script, its own copy of
  the code (separate from Visual Studio's, so it never touches your work there) and a "Music Organiser (latest)"
  shortcut on the Desktop. After that, double-click the shortcut.

  1. Asks which version to run: each open pull request (most recently changed first), or main. Enter picks the one
     used last time.
  2. Brings that version down from GitHub.
  3. Builds and starts the app with `dotnet watch`, in Debug, so:
       - a change to a screen (.axaml) shows in the open app within a second (HotAvalonia),
       - a change to the C# code applies to the open app, or restarts it when it can't (.NET Hot Reload).
  4. Every 20 seconds, checks GitHub for new pushes to that version and brings them in, so a change Claude pushes
     shows up in the app you have open, without you doing anything.

  Press Ctrl+C in its window, or close the window, to stop. Closing just the app leaves it waiting: the next change
  from GitHub builds and opens it again.

  Options:  -Branch <name>    run that branch without asking
            -PullEvery <s>    seconds between checks for new pushes (0 turns checking off)
            -NoWatch          build and run once, without dotnet watch (no live changes)
            -Repo <folder>    use this clone instead of the script's own (for testing the script)
#>
param(
    [string]$Branch,
    [int]$PullEvery = 20,
    [switch]$NoWatch,
    [string]$Repo
)

$ErrorActionPreference = 'Continue'   # git writes progress to stderr, which Windows PowerShell would treat as failure
$GitHubRepo = 'Silkie86/Music-Organiser'
$AppProject = 'src/MusicOrganiser.Avalonia'
$OnWindows = [Environment]::OSVersion.Platform -eq 'Win32NT'
$DataRoot = [Environment]::GetFolderPath('LocalApplicationData')
$Home_ = Join-Path (Join-Path $DataRoot 'MusicOrganiser') 'run-latest'
$Installed = Join-Path $Home_ 'run-latest.ps1'
$LastChoice = Join-Path $Home_ 'last-branch.txt'
if (-not $Repo) { $Repo = Join-Path $Home_ 'app' }

function Say([string]$Text, [string]$Colour = 'Gray') { Write-Host $Text -ForegroundColor $Colour }

function Stop-WithMessage([string]$Text) {
    Say ''
    Say $Text 'Red'
    exit 1
}

# --- The tools it needs -----------------------------------------------------------------------------------------
# Git: the one on the PATH, or the copy Visual Studio brings with it.
$GitExe = (Get-Command git -ErrorAction SilentlyContinue).Source
if (-not $GitExe -and $OnWindows) {
    $GitExe = Get-ChildItem -Path "$env:ProgramFiles\Microsoft Visual Studio\*\*\Common7\IDE\CommonExtensions\Microsoft\TeamFoundation\Team Explorer\Git\cmd\git.exe" -ErrorAction SilentlyContinue |
        Select-Object -First 1 -ExpandProperty FullName
}
if (-not $GitExe) {
    Stop-WithMessage 'Git isn''t installed. Get it from https://git-scm.com/download/win (the standard choices are fine), then run this again.'
}
if (-not (Get-Command dotnet -ErrorAction SilentlyContinue)) {
    Stop-WithMessage 'The .NET SDK isn''t installed. Visual Studio''s ".NET desktop development" workload has it, or get .NET 10 from https://dotnet.microsoft.com/download.'
}

function Git {
    & $GitExe -C $Repo @args
    if ($LASTEXITCODE -ne 0) { throw "git $($args -join ' ') didn't work (see above)." }
}

# --- First run: set up home -------------------------------------------------------------------------------------
New-Item -ItemType Directory -Force -Path $Home_ | Out-Null
if ($PSCommandPath -ne $Installed) {
    Copy-Item -Force $PSCommandPath $Installed
    $cmd = Join-Path $Home_ 'Run latest.cmd'
    Set-Content -Path $cmd -Encoding Ascii -Value "@echo off`r`npowershell -NoProfile -ExecutionPolicy Bypass -File `"%~dp0run-latest.ps1`" %*`r`nif errorlevel 1 pause"
    if ($OnWindows) {
        try {
            $shell = New-Object -ComObject WScript.Shell
            $link = $shell.CreateShortcut((Join-Path ([Environment]::GetFolderPath('Desktop')) 'Music Organiser (latest).lnk'))
            $link.TargetPath = $cmd
            $link.WorkingDirectory = $Home_
            $link.Description = 'Runs the newest Music Organiser from GitHub and keeps it up to date'
            $link.Save()
            Say 'Set up. From now on, double-click "Music Organiser (latest)" on your Desktop.' 'Green'
        }
        catch { Say "Set up in $Home_ (couldn't put a shortcut on the Desktop: $($_.Exception.Message))." 'Yellow' }
    }
}

if (-not (Test-Path (Join-Path $Repo '.git'))) {
    Say "Downloading the code from GitHub into $Repo (only the first time)..." 'Cyan'
    & $GitExe clone "https://github.com/$GitHubRepo.git" $Repo
    if ($LASTEXITCODE -ne 0) { Stop-WithMessage 'Couldn''t download the code from GitHub. Is the internet on?' }
}

# --- Which version --------------------------------------------------------------------------------------------
if (-not $Branch) {
    $remembered = if (Test-Path $LastChoice) { (Get-Content $LastChoice -Raw).Trim() } else { '' }
    $choices = @()
    try {
        $prs = Invoke-RestMethod -UseBasicParsing -TimeoutSec 15 -Headers @{ 'User-Agent' = 'music-organiser-run-latest' } `
            -Uri "https://api.github.com/repos/$GitHubRepo/pulls?state=open&sort=updated&direction=desc&per_page=15"
        foreach ($pr in $prs) {
            $choices += [pscustomobject]@{ Branch = $pr.head.ref; Label = "#$($pr.number)  $($pr.title)"; Updated = [datetime]$pr.updated_at }
        }
    }
    catch {
        Say "Couldn't ask GitHub for the open pull requests ($($_.Exception.Message))." 'Yellow'
    }
    $choices += [pscustomobject]@{ Branch = 'main'; Label = 'main  (only work that has been merged)'; Updated = $null }

    $default = 1
    for ($i = 0; $i -lt $choices.Count; $i++) { if ($choices[$i].Branch -eq $remembered) { $default = $i + 1 } }

    Say ''
    Say 'Which version of the app?' 'Cyan'
    for ($i = 0; $i -lt $choices.Count; $i++) {
        $c = $choices[$i]
        $when = ''
        if ($c.Updated) {
            $ago = (Get-Date).ToUniversalTime() - $c.Updated.ToUniversalTime()
            $when = if ($ago.TotalHours -lt 1) { "  (changed $([int]$ago.TotalMinutes) min ago)" }
                    elseif ($ago.TotalDays -lt 1) { "  (changed $([int]$ago.TotalHours) h ago)" }
                    else { "  (changed $([int]$ago.TotalDays) days ago)" }
        }
        $mark = if ($i + 1 -eq $default) { '>' } else { ' ' }
        Say ("{0} {1,2}) {2}{3}" -f $mark, ($i + 1), $c.Label, $when)
    }
    $answer = Read-Host "Type a number, or press Enter for $default"
    $n = 0
    if (-not [int]::TryParse($answer, [ref]$n) -or $n -lt 1 -or $n -gt $choices.Count) { $n = $default }
    $Branch = $choices[$n - 1].Branch
}
Set-Content -Path $LastChoice -Value $Branch

# --- Bring it down ----------------------------------------------------------------------------------------------
Say ''
Say "Getting the latest '$Branch' from GitHub..." 'Cyan'
Git fetch --quiet --prune origin

# This copy is only for running the app, so anything changed in it (a hot-reload experiment) is put aside, not lost.
if (& $GitExe -C $Repo status --porcelain) {
    Git stash push --quiet --include-untracked -m "Put aside by Run latest, $(Get-Date -Format 'yyyy-MM-dd HH:mm')"
    Say "Changes in $Repo were put aside (git stash list shows them)." 'Yellow'
}

& $GitExe -C $Repo show-ref --verify --quiet "refs/heads/$Branch"
if ($LASTEXITCODE -eq 0) { Git switch --quiet $Branch } else { Git switch --quiet --track -c $Branch "origin/$Branch" }

function Update-ToRemote {
    # Moves the branch to GitHub's version. Normally that's a fast-forward; if the branch was rewritten on GitHub,
    # the old copy is kept as a backup branch first, so nothing is lost.
    & $GitExe -C $Repo merge --ff-only --quiet "origin/$Branch" 2>$null
    if ($LASTEXITCODE -ne 0) {
        $backup = "backup/$Branch-$(Get-Date -Format 'yyyyMMdd-HHmmss')"
        Git branch $backup
        Git reset --quiet --keep "origin/$Branch"
        Say "The branch was rewritten on GitHub; the old copy is kept as $backup." 'Yellow'
    }
}
Update-ToRemote
Say ("Now on: " + (& $GitExe -C $Repo log -1 --format='%h  %s  (%cr)')) 'Green'

# A newer copy of this script in that version is used from the next run.
$newer = Join-Path $Repo 'tools/dev/run-latest.ps1'
if ((Test-Path $newer) -and (Test-Path $Installed) -and
    ((Get-FileHash $newer).Hash -ne (Get-FileHash $Installed).Hash) -and $PSCommandPath -eq $Installed) {
    Copy-Item -Force $newer $Installed
    Say 'This launcher has been updated; the new one is used next time.' 'DarkGray'
}

# --- Run it -----------------------------------------------------------------------------------------------------
if ($NoWatch) {
    Say 'Building and starting the app...' 'Cyan'
    & dotnet run --project (Join-Path $Repo $AppProject)
    exit $LASTEXITCODE
}

$env:DOTNET_WATCH_RESTART_ON_RUDE_EDIT = '1'   # a code change that can't be applied live restarts the app instead of asking
Say ''
Say 'Building and starting the app. The first build takes a few minutes; after that it''s quicker.' 'Cyan'
Say 'Leave this window open: changes pushed to GitHub show up in the app by themselves.' 'Cyan'
Say 'In the app, F12 saves a note (and a picture) for Claude.' 'Cyan'
Say 'Press Ctrl+C here, or close this window, to stop. (Close just the app and the next change opens it again.)' 'Cyan'
Say ''
$watch = Start-Process -FilePath 'dotnet' -ArgumentList @('watch', '--project', $AppProject, '--non-interactive') `
    -NoNewWindow -PassThru -WorkingDirectory $Repo

try {
    $next = (Get-Date).AddSeconds($PullEvery)
    while (-not $watch.HasExited) {
        Start-Sleep -Milliseconds 500
        if ($PullEvery -le 0 -or (Get-Date) -lt $next) { continue }
        $next = (Get-Date).AddSeconds($PullEvery)

        & $GitExe -C $Repo fetch --quiet origin $Branch 2>$null
        if ($LASTEXITCODE -ne 0) { continue }   # offline for a moment: try again next time
        $here = (& $GitExe -C $Repo rev-parse HEAD).Trim()
        $there = (& $GitExe -C $Repo rev-parse "origin/$Branch").Trim()
        if ($here -eq $there) { continue }

        $news = @(& $GitExe -C $Repo log --format='  %h %s' "$here..$there")
        $changed = @(& $GitExe -C $Repo diff --name-only $here $there)
        Update-ToRemote
        Say ''
        Say "$(Get-Date -Format 'HH:mm:ss')  New from GitHub ($($news.Count) change(s)), now in the app:" 'Green'
        $news | ForEach-Object { Say $_ 'Green' }

        # Rewrite changed screens in place: git replaces files rather than editing them, and HotAvalonia watches
        # for edits, so this makes sure each one reloads.
        foreach ($f in $changed | Where-Object { $_ -like '*.axaml' }) {
            $path = Join-Path $Repo $f
            if (Test-Path $path) { [IO.File]::WriteAllBytes($path, [IO.File]::ReadAllBytes($path)) }
        }
    }
}
finally {
    # dotnet watch and the app it started.
    if (-not $watch.HasExited) {
        if ($OnWindows) { & taskkill /PID $watch.Id /T /F 2>$null | Out-Null }
        else { Stop-Process -Id $watch.Id -ErrorAction SilentlyContinue }
    }
}
