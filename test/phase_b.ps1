# phase_b.ps1 - Phase B recordings with the app (HANDOFF.md section 6.3). PowerShell 5.1.
#
# For each run, the script:
#   1. opens test/scroll.html with the run speed and font in a new maximized Chrome window,
#   2. writes the run settings into %APPDATA%\DesktopRecorder\settings.txt (record_on_launch=1),
#   3. starts DesktopRecorder.exe (it records the saved region at once), then presses R and Space
#      on the page to start the scroll,
#   4. waits for the end of the scroll, presses Win+Shift+D to stop, and closes the Chrome window,
#   5. writes the FFmpeg command, ffprobe output and the foreground window titles to the output folder.
# The script restores settings.txt at the end. It does not read the frames (see the Phase B notes).
#
# Example:
#   & .\test\phase_b.ps1 -Runs B3
# A run definition: Fps, Chroma (yuv420|yuv444), Mode (fixed|changes), Quality (0|1|2), Speed (CSS px/s), Font (%).

param(
    [string[]]$Runs = @('B3'),
    [string]$OutDir = ''
)

$ErrorActionPreference = 'Stop'
$here = Split-Path -Parent $MyInvocation.MyCommand.Path
$root = Split-Path -Parent $here
if ($OutDir -eq '') { $OutDir = Join-Path $here 'results\phase_b' }
$recDir = Join-Path $OutDir 'videos'
New-Item -ItemType Directory -Force -Path $recDir | Out-Null
$exe = Join-Path $root 'DesktopRecorder.exe'
$settingsPath = Join-Path $env:APPDATA 'DesktopRecorder\settings.txt'
$pageUri = ([Uri](Join-Path $here 'scroll.html')).AbsoluteUri

# Region: physical px. The text column starts at x 24; the widest line (L136) ends at x 1101 at font 100%.
# The long side stays under 1568 px, so the image reader does not scale the frames down.
$region100 = @(8, 120, 1120, 892)
$region125 = @(8, 120, 1400, 892)

$table = [ordered]@{
    'B1'  = @{ Fps = 4;  Chroma = 'yuv420'; Mode = 'fixed';   Quality = 1; Speed = 1000; Font = 100 }
    'B2'  = @{ Fps = 6;  Chroma = 'yuv420'; Mode = 'fixed';   Quality = 1; Speed = 1000; Font = 100 }
    'B3'  = @{ Fps = 10; Chroma = 'yuv420'; Mode = 'fixed';   Quality = 1; Speed = 1000; Font = 100 }
    'B4'  = @{ Fps = 15; Chroma = 'yuv420'; Mode = 'fixed';   Quality = 1; Speed = 1000; Font = 100 }
    'B5'  = @{ Fps = 10; Chroma = 'yuv420'; Mode = 'fixed';   Quality = 1; Speed = 500;  Font = 100 }
    'B6'  = @{ Fps = 10; Chroma = 'yuv420'; Mode = 'fixed';   Quality = 1; Speed = 2000; Font = 100 }
    'B7'  = @{ Fps = 10; Chroma = 'yuv420'; Mode = 'fixed';   Quality = 1; Speed = 3000; Font = 100 }
    'B8'  = @{ Fps = 10; Chroma = 'yuv444'; Mode = 'fixed';   Quality = 1; Speed = 1000; Font = 100 }
    'B9'  = @{ Fps = 10; Chroma = 'yuv420'; Mode = 'fixed';   Quality = 0; Speed = 1000; Font = 100 }
    'B11' = @{ Fps = 10; Chroma = 'yuv420'; Mode = 'fixed';   Quality = 2; Speed = 1000; Font = 100 }
    'B12' = @{ Fps = 15; Chroma = 'yuv420'; Mode = 'changes'; Quality = 1; Speed = 1000; Font = 100 }
    'B13' = @{ Fps = 10; Chroma = 'yuv420'; Mode = 'fixed';   Quality = 1; Speed = 1000; Font = 125 }
    # LL: lossless baseline for the reader. FFmpeg directly (not the app): ddagrab + h264_nvenc lossless, 4:4:4.
    'LL'  = @{ Fps = 10; Chroma = 'yuv444'; Mode = 'fixed';   Quality = 1; Speed = 1000; Font = 100; Direct = $true }
    'B7s4'= @{ Fps = 4;  Chroma = 'yuv420'; Mode = 'fixed';   Quality = 1; Speed = 3000; Font = 100 }
}

Add-Type -Namespace PB -Name U -MemberDefinition @'
[DllImport("user32.dll")] public static extern void keybd_event(byte vk, byte scan, uint flags, IntPtr extra);
[DllImport("user32.dll")] public static extern IntPtr GetForegroundWindow();
[DllImport("user32.dll")] public static extern bool ShowWindow(IntPtr h, int c);
[DllImport("user32.dll", CharSet=CharSet.Unicode)] public static extern int GetWindowText(IntPtr h, System.Text.StringBuilder s, int n);
'@
function Send-Keys([byte[]]$keys) {
    foreach ($k in $keys) { [PB.U]::keybd_event($k, 0, 0, [IntPtr]::Zero); Start-Sleep -Milliseconds 30 }
    [array]::Reverse($keys)
    foreach ($k in $keys) { [PB.U]::keybd_event($k, 0, 2, [IntPtr]::Zero); Start-Sleep -Milliseconds 30 }
    Start-Sleep -Milliseconds 100
}
function Get-ForegroundTitle {
    $s = New-Object System.Text.StringBuilder 300
    [void][PB.U]::GetWindowText([PB.U]::GetForegroundWindow(), $s, 300)
    $s.ToString()
}
function Stop-App {
    Get-Process DesktopRecorder -ErrorAction SilentlyContinue | Stop-Process
    Start-Sleep -Milliseconds 700
}

$original = Get-Content $settingsPath
Copy-Item $settingsPath (Join-Path $OutDir 'settings.before.txt') -Force

function Write-RunSettings($cfg, $region) {
    $values = [ordered]@{
        folder = $recDir; fps = "$($cfg.Fps)"; quality = "$($cfg.Quality)"; split_minutes = '0'
        record_on_launch = '1'; copy_to_clipboard = '0'; play_sounds = '0'
        capture_backend = 'ddagrab'; encoder = 'auto'; chroma = $cfg.Chroma; frame_rate_mode = $cfg.Mode
        region = ($region -join ','); last_region = ($region -join ',')
    }
    $lines = New-Object System.Collections.Generic.List[string]
    $seen = @{}
    foreach ($line in $original) {
        $eq = $line.IndexOf('=')
        $key = if ($eq -gt 0) { $line.Substring(0, $eq) } else { '' }
        if ($values.Contains($key)) { $lines.Add("$key=" + $values[$key]); $seen[$key] = 1 } else { $lines.Add($line) }
    }
    foreach ($k in $values.Keys) { if (-not $seen.ContainsKey($k)) { $lines.Add("$k=" + $values[$k]) } }
    [System.IO.File]::WriteAllLines($settingsPath, $lines)
}

$summary = @()
try {
    foreach ($run in $Runs) {
        $cfg = $table[$run]
        if ($cfg -eq $null) { throw "Unknown run $run" }
        $region = if ($cfg.Font -eq 125) { $region125 } else { $region100 }
        Write-Host "=== $run fps=$($cfg.Fps) $($cfg.Chroma) $($cfg.Mode) q=$($cfg.Quality) speed=$($cfg.Speed) font=$($cfg.Font)"
        Stop-App
        Write-RunSettings $cfg $region

        # A new Chrome window with the run settings in the URL hash, maximized and in front.
        $url = $pageUri + "#speed=$($cfg.Speed)&font=$($cfg.Font)&delay=0"
        Start-Process chrome -ArgumentList '--new-window', $url
        Start-Sleep 3
        $page = [PB.U]::GetForegroundWindow()
        [void][PB.U]::ShowWindow($page, 3)
        Start-Sleep 1
        $titleStart = Get-ForegroundTitle

        $before = @(Get-ChildItem $recDir -Filter *.mp4 | ForEach-Object FullName)
        $scroll = 7415.0 * $cfg.Font / 100 / $cfg.Speed
        if ($cfg.Direct) {
            $out = Join-Path $recDir ("lossless_" + (Get-Date -Format 'yyyy-MM-dd_HH-mm-ss') + '.mp4')
            $src = "ddagrab=output_idx=0:framerate={0}:draw_mouse=0:offset_x={1}:offset_y={2}:video_size={3}x{4}" -f $cfg.Fps, $region[0], $region[1], $region[2], $region[3]
            $dur = [string]([math]::Round($scroll + 5.0, 1))
            Start-Process ffmpeg -WindowStyle Hidden -ArgumentList @('-hide_banner', '-loglevel', 'error', '-y', '-f', 'lavfi', '-i', $src, '-t', $dur,
                '-c:v', 'h264_nvenc', '-tune', 'lossless', '-rgb_mode', 'yuv444', '-profile:v', 'high444p', '-movflags', '+frag_keyframe+empty_moov', "`"$out`"")
        } else {
            Start-Process $exe
        }
        Start-Sleep 2.5
        $proc = Get-CimInstance Win32_Process -Filter "Name='ffmpeg.exe'" | Select-Object -First 1
        $cmd = if ($proc) { $proc.CommandLine } else { '(no ffmpeg process)' }
        $cpuStart = if ($proc) { (Get-Process -Id $proc.ProcessId).TotalProcessorTime.TotalSeconds } else { 0 }
        $t0 = Get-Date
        Send-Keys ([byte[]](0x52))   # R = reset
        Send-Keys ([byte[]](0x20))   # Space = start the scroll
        # Distance at font 100% was 7415 CSS px with this window (HANDOFF 6.8). Scale with the font.
        Start-Sleep -Milliseconds ([int](($scroll + 2.0) * 1000))
        $titleEnd = Get-ForegroundTitle
        $cpuEnd = 0
        if ($proc) { try { $cpuEnd = (Get-Process -Id $proc.ProcessId).TotalProcessorTime.TotalSeconds } catch { } }
        $wall = ((Get-Date) - $t0).TotalSeconds
        if ($cfg.Direct) {
            # FFmpeg stops by itself (-t).
            while (Get-Process ffmpeg -ErrorAction SilentlyContinue) { Start-Sleep -Milliseconds 500 }
        } else {
            Send-Keys ([byte[]](0x5B, 0x10, 0x44))   # Win+Shift+D stops
            Start-Sleep 3
        }
        # Close the run window (it is in front).
        if ((Get-ForegroundTitle) -like 'Scroll Test*') { Send-Keys ([byte[]](0x11, 0x57)) }   # Ctrl+W
        Start-Sleep 1

        $new = @(Get-ChildItem $recDir -Filter *.mp4 | Sort-Object Name | ForEach-Object FullName | Where-Object { $before -notcontains $_ })
        $valid = ($titleStart -like 'Scroll Test*') -and ($titleEnd -like 'Scroll Test*') -and ($new.Count -eq 1)
        $file = if ($new.Count -ge 1) { $new[0] } else { '' }
        $probe = if ($file) { (& ffprobe -v error -select_streams v:0 -show_entries stream=codec_name,profile,pix_fmt,width,height -of compact=p=0 $file) -join ' ' } else { '' }
        $cpu = if ($cpuEnd -gt 0) { [math]::Round(100 * ($cpuEnd - $cpuStart) / $wall / [Environment]::ProcessorCount, 3) } else { $null }
        $row = [pscustomobject]@{
            Run = $run; Fps = $cfg.Fps; Chroma = $cfg.Chroma; Mode = $cfg.Mode; Quality = $cfg.Quality
            Speed = $cfg.Speed; Font = $cfg.Font; Region = ($region -join ','); File = $file; Probe = $probe
            Cmd = $cmd; CpuPct = $cpu; TitleStart = $titleStart; TitleEnd = $titleEnd; Valid = $valid
            MB = if ($file) { [math]::Round((Get-Item $file).Length / 1MB, 3) } else { $null }
        }
        $row | ConvertTo-Json | Set-Content (Join-Path $OutDir "$run.json")
        Write-Host ("{0} valid={1} {2} MB={3} cpu={4}%" -f $run, $valid, $probe, $row.MB, $cpu)
        $summary += $row
    }
}
finally {
    Stop-App
    [System.IO.File]::WriteAllLines($settingsPath, $original)
    Start-Process $exe
}
