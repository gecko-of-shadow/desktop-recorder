# ai_capture_runs.ps1 - makes the 5 real recordings of the AI capture brief (section 8, Phase 1) with the running app.
# PowerShell 5.1. The app (folder mode, hotkey Win+Shift+D) must run. Do not touch the PC during the run (about 11 min).
#   powershell -ExecutionPolicy Bypass -File test\ai_capture_runs.ps1 -Log <file> [-Only status,caret,tour,60,300]
# Synthetic input: the hotkey, a drag in the selector, the note in the note box. One monitor, 1920x1080 physical px.
# Time to "ready": from the stop hotkey until the clipboard holds the new recording folder.
param([string]$Log = '', [string[]]$Only = @('status', 'caret', 'tour', '60', '300'), [string]$Recordings = "$env:USERPROFILE\Recordings")
$ErrorActionPreference = 'Stop'
$Only = @($Only | ForEach-Object { $_ -split ',' } | ForEach-Object { $_.Trim() })   # -File gives "a,b" as one string
$here = Split-Path -Parent $MyInvocation.MyCommand.Path
if ($Log -eq '') { $Log = Join-Path $here 'results\ai_capture_runs.log' }
New-Item -ItemType Directory -Force -Path (Split-Path -Parent $Log) | Out-Null
Add-Type -AssemblyName System.Windows.Forms
Add-Type -Namespace AC -Name U -MemberDefinition @'
[DllImport("user32.dll")] public static extern void keybd_event(byte vk, byte scan, uint flags, IntPtr extra);
[DllImport("user32.dll")] public static extern void mouse_event(uint flags, int dx, int dy, int data, IntPtr extra);
public delegate bool CB(IntPtr h, IntPtr l);
[DllImport("user32.dll")] public static extern bool EnumWindows(CB cb, IntPtr l);
[DllImport("user32.dll")] public static extern bool IsWindowVisible(IntPtr h);
[DllImport("user32.dll")] public static extern bool ShowWindow(IntPtr h, int c);
[DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr h);
[DllImport("user32.dll")] public static extern IntPtr GetForegroundWindow();
[DllImport("user32.dll", CharSet=CharSet.Unicode)] public static extern int GetWindowText(IntPtr h, System.Text.StringBuilder s, int n);
public static string Title(IntPtr h) { var s = new System.Text.StringBuilder(300); GetWindowText(h, s, 300); return s.ToString(); }
public static IntPtr Find(string part) {
    IntPtr found = IntPtr.Zero;
    EnumWindows(delegate(IntPtr h, IntPtr l) {
        if (!IsWindowVisible(h)) return true;
        if (Title(h).IndexOf(part, StringComparison.OrdinalIgnoreCase) >= 0) { found = h; return false; }
        return true; }, IntPtr.Zero);
    return found;
}
'@
function Say([string]$m) { $line = (Get-Date -Format 'HH:mm:ss.fff') + ' ' + $m; Add-Content -Path $Log -Value $line -Encoding utf8; $line }
function Key([byte]$vk, [bool]$up) { $f = 0; if ($up) { $f = 2 }; [AC.U]::keybd_event($vk, 0, $f, [IntPtr]::Zero) }
function HotKey { Key 0x5B $false; Key 0x10 $false; Key 0x44 $false; Start-Sleep -Milliseconds 60; Key 0x44 $true; Key 0x10 $true; Key 0x5B $true }
function MoveTo([int]$x, [int]$y) { [AC.U]::mouse_event(0x8001, [int]($x * 65535 / 1919), [int]($y * 65535 / 1079), 0, [IntPtr]::Zero) }
function Wheel([int]$notches) { for ($i = 0; $i -lt $notches; $i++) { [AC.U]::mouse_event(0x0800, 0, 0, -120, [IntPtr]::Zero); Start-Sleep -Milliseconds 40 } }
function Front([string]$part) {
    for ($try = 0; $try -lt 20; $try++) {
        $h = [AC.U]::Find($part)
        if ($h -ne [IntPtr]::Zero) {
            [void][AC.U]::ShowWindow($h, 3)
            Key 0x12 $false; Key 0x12 $true
            [void][AC.U]::SetForegroundWindow($h)
            Start-Sleep -Milliseconds 500
            if ([AC.U]::Title([AC.U]::GetForegroundWindow()).IndexOf($part, [StringComparison]::OrdinalIgnoreCase) -ge 0) { return $true }
        }
        Start-Sleep -Milliseconds 500
    }
    return $false
}
function FrontTitle { [AC.U]::Title([AC.U]::GetForegroundWindow()) }
function ClipFolder {
    try { $l = [System.Windows.Forms.Clipboard]::GetFileDropList(); if ($l.Count -gt 0) { return $l[0] } } catch { }
    return ''
}
$script:recStart = Get-Date
function StartRec([int]$x1, [int]$y1, [int]$x2, [int]$y2, [int]$px, [int]$py) {
    $script:recStart = Get-Date
    HotKey
    Start-Sleep -Milliseconds 1000
    MoveTo $x1 $y1; Start-Sleep -Milliseconds 200
    [AC.U]::mouse_event(0x0002, 0, 0, 0, [IntPtr]::Zero)
    for ($i = 1; $i -le 10; $i++) { MoveTo ($x1 + ($x2 - $x1) * $i / 10) ($y1 + ($y2 - $y1) * $i / 10); Start-Sleep -Milliseconds 25 }
    Start-Sleep -Milliseconds 200
    [AC.U]::mouse_event(0x0004, 0, 0, 0, [IntPtr]::Zero)
    Start-Sleep -Milliseconds 300
    MoveTo $px $py
    Start-Sleep -Milliseconds 1500
    if (-not (Get-Process ffmpeg -ErrorAction SilentlyContinue)) { throw 'FFmpeg did not start. The hotkey or the drag failed.' }
    Say "  recording started, front: '$(FrontTitle)'"
}
function StopRec([string]$note) {
    Say "  stop, front before stop: '$(FrontTitle)'"
    $before = ClipFolder
    $sw = [Diagnostics.Stopwatch]::StartNew()
    HotKey
    $noteOk = $false
    for ($i = 0; $i -lt 80; $i++) {
        Start-Sleep -Milliseconds 100
        if ((FrontTitle).StartsWith('Desktop Recorder: note')) { $noteOk = $true; break }
    }
    if ($noteOk) { [System.Windows.Forms.SendKeys]::SendWait($note); Start-Sleep -Milliseconds 200; [System.Windows.Forms.SendKeys]::SendWait('{ENTER}') }
    else { Say "  WARNING: the note box did not come to the front ('$(FrontTitle)'). No note typed." }
    $folder = ''
    while ($sw.Elapsed.TotalSeconds -lt 240) {
        $c = ClipFolder
        if ($c -ne '' -and $c -ne $before -and (Test-Path -LiteralPath $c -PathType Container)) { $folder = $c; break }
        Start-Sleep -Milliseconds 100
    }
    $ready = $sw.Elapsed.TotalSeconds
    if ($folder -eq '') { Say "  WARNING: the clipboard did not get a new folder in 240 s." }
    else { Say ("  note={0} folder={1} ready_s={2:0.0}" -f $note, $folder, $ready) }
    Start-Sleep -Seconds 2
}
$chromeProfile = Join-Path $env:TEMP 'dr_counter_profile'
function OpenPage([string]$file, [string]$hash, [string]$title) {
    $uri = (New-Object System.Uri (Join-Path $here $file)).AbsoluteUri + $hash
    Start-Process chrome -ArgumentList @("--user-data-dir=$chromeProfile", '--no-first-run', '--hide-crash-restore-bubble', '--new-window', '--start-maximized', $uri) | Out-Null
    Start-Sleep -Seconds 4
    if (-not (Front $title)) { throw "The page '$title' did not come to the front." }
}
function CloseChrome {
    Get-CimInstance Win32_Process -Filter "Name='chrome.exe'" | Where-Object { $_.CommandLine -like '*dr_counter_profile*' } | ForEach-Object { Stop-Process -Id $_.ProcessId -Force -ErrorAction SilentlyContinue }
    Start-Sleep -Seconds 1
}
if (-not (Get-Process | Where-Object { $_.Name -like 'DesktopRecorder*' })) { throw 'Desktop Recorder does not run.' }
if (Get-Process ffmpeg -ErrorAction SilentlyContinue) { throw 'FFmpeg runs already. Stop the recording first.' }
Say "run start, tests: $($Only -join ',')"
try {
    if ($Only -contains 'status') {
        Say 'test status'
        OpenPage 'status.html' '' 'Status Line Test'
        [System.Windows.Forms.SendKeys]::SendWait('{F5}')
        $load = Get-Date
        Start-Sleep -Milliseconds 1500
        StartRec 8 120 1207 719 1600 900
        $left = 63 - ((Get-Date) - $load).TotalSeconds
        Start-Sleep -Milliseconds ([int]($left * 1000))
        StopRec 'status'
        CloseChrome
    }
    if ($Only -contains 'caret') {
        Say 'test caret'
        $txt = Join-Path (Split-Path -Parent $Log) 'caret_test.txt'
        Set-Content -Path $txt -Value 'Caret test O0Oo0 Il1| rn m. Do not type in this window.' -Encoding ascii
        Start-Process notepad -ArgumentList "`"$txt`""
        Start-Sleep -Seconds 3
        if (-not (Front 'caret_test')) { throw 'Notepad did not come to the front.' }
        # The region starts at the left screen edge, because the caret is at the start of the text (x about 5).
        # Two runs on 2026-11-19 were not valid: their regions started at x 20 and did not hold the caret.
        StartRec 0 40 1399 979 1700 600
        Start-Sleep -Seconds 30
        StopRec 'caret'
    }
    if ($Only -contains 'tour') {
        Say 'test tour'
        Start-Process 'ms-settings:display'
        Start-Sleep -Seconds 4
        if (-not (Front 'Settings')) { throw 'Settings did not come to the front.' }
        Start-Sleep -Seconds 1
        StartRec 420 120 1819 1019 1100 600
        $pages = @('', 'ms-settings:sound', 'ms-settings:personalization', 'ms-settings:dateandtime', 'ms-settings:easeofaccess-display')
        foreach ($p in $pages) {
            if ($p -ne '') { Start-Process $p; Start-Sleep -Milliseconds 2500 }
            Say "  page '$p' front: '$(FrontTitle)'"
            Start-Sleep -Milliseconds 1200
            for ($b = 0; $b -lt 4; $b++) { Wheel 5; Start-Sleep -Milliseconds 900 }
            Start-Sleep -Milliseconds 800
        }
        StopRec 'tour'
    }
    foreach ($t in @(@('60', 60), @('300', 300))) {
        if ($Only -notcontains $t[0]) { continue }
        Say "test $($t[0])"
        OpenPage 'scroll.html' '#speed=200&font=100&delay=0' 'Scroll Test'
        StartRec 8 120 1127 1011 1600 900
        # Space starts the scroll (37 s at 200 px/s). The page does not start from the URL alone.
        [System.Windows.Forms.SendKeys]::SendWait(' ')
        $end = (Get-Date).AddSeconds($t[1])
        $next = (Get-Date).AddSeconds(45)
        while ((Get-Date) -lt $end) {
            Start-Sleep -Milliseconds 500
            if ((Get-Date) -gt $next -and ($end - (Get-Date)).TotalSeconds -gt 20) {
                if ((FrontTitle).StartsWith('Scroll Test')) { [System.Windows.Forms.SendKeys]::SendWait(' ') }
                $next = (Get-Date).AddSeconds(45)
            }
        }
        StopRec $t[0]
        CloseChrome
    }
    Say 'run end'
}
catch {
    Say "ERROR: $($_.Exception.Message)"
    if (Get-Process ffmpeg -ErrorAction SilentlyContinue) { HotKey; Say '  sent the stop hotkey' }
    exit 1
}
