# lossless_counter.ps1 - condition E (Bran, 2026-11-19): counter test of the lossless capture at 15 fps.
# PowerShell 5.1. Run from the repository folder:
#   powershell -ExecutionPolicy Bypass -File test\lossless_counter.ps1
# The script:
#   1. builds test\capture_args.cs (it prints the FFmpeg arguments of the app for folder mode),
#   2. opens test\counter.html in a new Chrome window with a separate test profile, maximized,
#   3. records 10 s with the app arguments, "Changes only" and "Fixed", at 15 fps, region 20,120,1880x860,
#   4. runs frames.py --counter on each work file (HANDOFF 6.5 method),
#   5. closes only the Chrome window of the test profile.
# The screen must be on. With the display off, ddagrab gets no frames and FFmpeg waits.
param([string]$OutDir = '', [int]$Seconds = 10, [double]$Fps = 15)
$ErrorActionPreference = 'Stop'
$here = Split-Path -Parent $MyInvocation.MyCommand.Path
$repo = Split-Path -Parent $here
if ($OutDir -eq '') { $OutDir = Join-Path $here 'results\lossless_counter' }
New-Item -ItemType Directory -Force -Path $OutDir | Out-Null
$csc = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319\csc.exe'
$tool = Join-Path $OutDir 'capture_args.exe'
& $csc /nologo "/out:$tool" (Join-Path $here 'capture_args.cs') (Join-Path $repo 'SettledFrames.cs') (Join-Path $repo 'RecorderCore.cs') | Out-Null
if ($LASTEXITCODE -ne 0) { throw 'capture_args build failed' }
$ffmpeg = (Get-Command ffmpeg).Source
$uri = (New-Object System.Uri (Join-Path $here 'counter.html')).AbsoluteUri
$profileDir = Join-Path $env:TEMP 'dr_counter_profile'
Start-Process chrome -ArgumentList @("--user-data-dir=$profileDir", '--no-first-run', '--new-window', '--start-maximized', $uri) | Out-Null
Start-Sleep -Seconds 5
$results = @()
foreach ($mode in @('changes', 'fixed')) {
    $work = Join-Path $OutDir ("work_$mode.mp4")
    $argLine = & $tool $mode $Fps 20 120 1880 860 $work
    $argLine = $argLine -replace ' -movflags ', (" -t $Seconds -movflags ")
    $log = Join-Path $OutDir ("ffmpeg_$mode.log")
    $p = Start-Process -FilePath $ffmpeg -ArgumentList $argLine -NoNewWindow -PassThru -RedirectStandardError $log
    if (-not $p.WaitForExit(($Seconds + 20) * 1000)) { Stop-Process -Id $p.Id -Force; $results += "$mode TIMEOUT (screen off?)"; continue }
    $json = Join-Path $OutDir ("frames_$mode.json")
    $txt = & python (Join-Path $here 'frames.py') $work --counter --expected-fps $Fps --json $json 2>&1 | Out-String
    $results += "$mode exit=$($p.ExitCode)"
    $results += $txt
}
Get-CimInstance Win32_Process -Filter "Name='chrome.exe'" | Where-Object { $_.CommandLine -like '*dr_counter_profile*' } | ForEach-Object { Stop-Process -Id $_.ProcessId -Force -ErrorAction SilentlyContinue }
$results
