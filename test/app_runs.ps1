# app_runs.ps1 - Records test pages with the app itself (HANDOFF.md section 7, GUI test). PowerShell 5.1.
#
# For each run, the script:
#   1. stops DesktopRecorder.exe (when it is not recording),
#   2. writes the run settings into %APPDATA%\DesktopRecorder\settings.txt,
#   3. starts DesktopRecorder.exe with record_on_launch=1, so the app records the saved region at once,
#   4. reads the FFmpeg command line of the app, waits, and presses the hotkey (Win+Shift+D) to stop,
#   5. runs ffprobe and frames.py on the new file(s).
# The script makes a copy of settings.txt first and puts it back at the end.
#
# Example:
#   powershell -ExecutionPolicy Bypass -File test\app_runs.ps1 -Region 20,180,1880,820 -Runs dda420,gdi420
# Runs: dda420 gdi420 dda444 gdi444 ddachg gdichg split
# -Page counter (default) runs frames.py --counter. -Page other runs frames.py --threshold 0.1.

param(
    [Parameter(Mandatory = $true)][int[]]$Region,     # x,y,w,h physical px
    [string[]]$Runs = @('dda420'),
    [double]$Fps = 10,
    [double]$Seconds = 10,
    [int]$Quality = 1,
    [string]$Page = 'counter',
    [string]$Tag = '',
    [string]$OutDir = '',
    # Title start of the test page window. The script maximizes it and brings it to the front before each run.
    [string]$Window = 'Frame Counter'
)

$ErrorActionPreference = 'Stop'
$here = Split-Path -Parent $MyInvocation.MyCommand.Path
$root = Split-Path -Parent $here
if ($OutDir -eq '') { $OutDir = Join-Path $here 'results\app_runs' }
New-Item -ItemType Directory -Force -Path $OutDir | Out-Null
$exe = Join-Path $root 'DesktopRecorder.exe'
$settingsPath = Join-Path $env:APPDATA 'DesktopRecorder\settings.txt'
$backup = Join-Path $OutDir 'settings.before.txt'
$recDir = Join-Path $OutDir 'videos'
New-Item -ItemType Directory -Force -Path $recDir | Out-Null

Add-Type -Namespace AR -Name U -MemberDefinition @'
[DllImport("user32.dll")] public static extern void keybd_event(byte vk, byte scan, uint flags, IntPtr extra);
public delegate bool CB(IntPtr h, IntPtr l);
[DllImport("user32.dll")] public static extern bool EnumWindows(CB cb, IntPtr l);
[DllImport("user32.dll")] public static extern bool IsWindowVisible(IntPtr h);
[DllImport("user32.dll")] public static extern bool ShowWindow(IntPtr h, int c);
[DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr h);
[DllImport("user32.dll", CharSet=CharSet.Unicode)] public static extern int GetWindowText(IntPtr h, System.Text.StringBuilder s, int n);
public static IntPtr Find(string start) {
    IntPtr found = IntPtr.Zero;
    EnumWindows(delegate(IntPtr h, IntPtr l) {
        if (!IsWindowVisible(h)) return true;
        var s = new System.Text.StringBuilder(300); GetWindowText(h, s, 300);
        if (s.ToString().StartsWith(start)) { found = h; return false; }
        return true; }, IntPtr.Zero);
    return found;
}
'@
function Show-TestPage {
    if ($Window -eq '') { return }
    $h = [AR.U]::Find($Window)
    if ($h -eq [IntPtr]::Zero) { throw "No window with a title that starts with '$Window'." }
    [void][AR.U]::ShowWindow($h, 3)   # SW_MAXIMIZE
    # A synthetic Alt press lets this process move another window to the front.
    [AR.U]::keybd_event(0x12, 0, 0, [IntPtr]::Zero); [AR.U]::keybd_event(0x12, 0, 2, [IntPtr]::Zero)
    [void][AR.U]::SetForegroundWindow($h)
    Start-Sleep -Milliseconds 500
}
function Send-StopHotkey {
    $keys = [byte[]](0x5B, 0x10, 0x44)   # Win, Shift, D (the app hotkey stops a recording)
    foreach ($k in $keys) { [AR.U]::keybd_event($k, 0, 0, [IntPtr]::Zero); Start-Sleep -Milliseconds 30 }
    [array]::Reverse($keys)
    foreach ($k in $keys) { [AR.U]::keybd_event($k, 0, 2, [IntPtr]::Zero); Start-Sleep -Milliseconds 30 }
}

$table = @{
    'dda420' = @{ capture_backend = 'ddagrab'; chroma = 'yuv420'; frame_rate_mode = 'fixed';   split_minutes = 0 }
    'gdi420' = @{ capture_backend = 'gdigrab'; chroma = 'yuv420'; frame_rate_mode = 'fixed';   split_minutes = 0 }
    'dda444' = @{ capture_backend = 'ddagrab'; chroma = 'yuv444'; frame_rate_mode = 'fixed';   split_minutes = 0 }
    'gdi444' = @{ capture_backend = 'gdigrab'; chroma = 'yuv444'; frame_rate_mode = 'fixed';   split_minutes = 0 }
    'ddachg' = @{ capture_backend = 'ddagrab'; chroma = 'yuv420'; frame_rate_mode = 'changes'; split_minutes = 0 }
    'gdichg' = @{ capture_backend = 'gdigrab'; chroma = 'yuv420'; frame_rate_mode = 'changes'; split_minutes = 0 }
    'split'  = @{ capture_backend = 'ddagrab'; chroma = 'yuv420'; frame_rate_mode = 'fixed';   split_minutes = 1 }
}

if (-not (Test-Path $backup)) { Copy-Item $settingsPath $backup }
$original = Get-Content $settingsPath

function Write-RunSettings($run) {
    $values = @{
        folder = $recDir; fps = "$Fps"; quality = "$Quality"; record_on_launch = '1'
        copy_to_clipboard = '0'; play_sounds = '0'; encoder = 'auto'
        region = ($Region -join ','); last_region = ($Region -join ',')
    }
    foreach ($k in $table[$run].Keys) { $values[$k] = [string]$table[$run][$k] }
    $lines = New-Object System.Collections.Generic.List[string]
    $seen = @{}
    foreach ($line in $original) {
        $eq = $line.IndexOf('=')
        $key = if ($eq -gt 0) { $line.Substring(0, $eq) } else { '' }
        if ($values.ContainsKey($key)) { $lines.Add("$key=" + $values[$key]); $seen[$key] = 1 } else { $lines.Add($line) }
    }
    foreach ($k in $values.Keys) { if (-not $seen.ContainsKey($k)) { $lines.Add("$k=" + $values[$k]) } }
    [System.IO.File]::WriteAllLines($settingsPath, $lines)
}

function Stop-App {
    Get-Process DesktopRecorder -ErrorAction SilentlyContinue | Stop-Process
    Start-Sleep -Milliseconds 700
}

$summary = @()
try {
    foreach ($run in $Runs) {
        if (-not $table.ContainsKey($run)) { throw "Unknown run $run" }
        $name = $run + $Tag
        Write-Host "=== $name"
        Stop-App
        Write-RunSettings $run
        $before = @(Get-ChildItem $recDir -Filter *.mp4 | ForEach-Object FullName)
        Show-TestPage
        Start-Process $exe
        Start-Sleep 2
        $proc = Get-CimInstance Win32_Process -Filter "Name='ffmpeg.exe'" | Select-Object -First 1
        $cmd = if ($proc) { $proc.CommandLine } else { '(no ffmpeg process)' }
        $cpuStart = if ($proc) { (Get-Process -Id $proc.ProcessId).TotalProcessorTime.TotalSeconds } else { 0 }
        Set-Content -Path (Join-Path $OutDir "$name.cmd.txt") -Value $cmd
        Start-Sleep -Milliseconds ([int](($Seconds - 2) * 1000))
        $cpuEnd = 0
        if ($proc) { try { $cpuEnd = (Get-Process -Id $proc.ProcessId).TotalProcessorTime.TotalSeconds } catch { } }
        Send-StopHotkey
        Start-Sleep 4
        $after = @(Get-ChildItem $recDir -Filter *.mp4 | Sort-Object Name | ForEach-Object FullName)
        $new = @($after | Where-Object { $before -notcontains $_ })
        $files = @()
        foreach ($f in $new) {
            $probe = (& ffprobe -v error -select_streams v:0 -show_entries stream=codec_name,profile,pix_fmt,width,height -of compact=p=0:nk=0 $f) -join ' '
            $json = Join-Path $OutDir ((Split-Path $f -Leaf) + '.json')
            if ($Page -eq 'counter') {
                $txt = & python (Join-Path $here 'frames.py') $f --counter --expected-fps $Fps --json $json 2>&1 | Out-String
            } else {
                $txt = & python (Join-Path $here 'frames.py') $f --threshold 0.1 --json $json 2>&1 | Out-String
            }
            Set-Content -Path ($json -replace '\.json$', '.txt') -Value $txt
            $files += [pscustomobject]@{ File = (Split-Path $f -Leaf); MB = [math]::Round((Get-Item $f).Length / 1MB, 3); Probe = $probe }
            Write-Host $probe
            Write-Host ($txt -split "`n" | Where-Object { $_ -match 'Total frames|Unique|Real|RESULT|Values|Failed' }) -Separator "`n"
        }
        $cpu = if ($cpuEnd -gt 0) { [math]::Round(100 * ($cpuEnd - $cpuStart) / ($Seconds - 2) / [Environment]::ProcessorCount, 2) } else { $null }
        $summary += [pscustomobject]@{ Run = $name; Cmd = $cmd; CpuPct = $cpu; Files = $files }
    }
}
finally {
    Stop-App
    [System.IO.File]::WriteAllLines($settingsPath, $original)
    Start-Process $exe
}
$summary | ConvertTo-Json -Depth 5 | Set-Content (Join-Path $OutDir ("summary" + $Tag + ".json"))
$summary | ForEach-Object { "{0}  cpu={1}%  files={2}" -f $_.Run, $_.CpuPct, (($_.Files | ForEach-Object { "$($_.File) $($_.MB)MB" }) -join '; ') }
