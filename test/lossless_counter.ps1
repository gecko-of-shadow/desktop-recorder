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
Add-Type -Namespace LC -Name U -MemberDefinition @'
[DllImport("user32.dll")] public static extern void keybd_event(byte vk, byte scan, uint flags, IntPtr extra);
public delegate bool CB(IntPtr h, IntPtr l);
[DllImport("user32.dll")] public static extern bool EnumWindows(CB cb, IntPtr l);
[DllImport("user32.dll")] public static extern bool IsWindowVisible(IntPtr h);
[DllImport("user32.dll")] public static extern bool ShowWindow(IntPtr h, int c);
[DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr h);
[DllImport("user32.dll")] public static extern IntPtr GetForegroundWindow();
[DllImport("user32.dll", CharSet=CharSet.Unicode)] public static extern int GetWindowText(IntPtr h, System.Text.StringBuilder s, int n);
public static string Title(IntPtr h) { var s = new System.Text.StringBuilder(300); GetWindowText(h, s, 300); return s.ToString(); }
public static IntPtr Find(string start) {
    IntPtr found = IntPtr.Zero;
    EnumWindows(delegate(IntPtr h, IntPtr l) {
        if (!IsWindowVisible(h)) return true;
        if (Title(h).StartsWith(start)) { found = h; return false; }
        return true; }, IntPtr.Zero);
    return found;
}
'@
# --hide-crash-restore-bubble: step 5 stops Chrome with force, so the next start showed "Restore pages?".
# That bubble covered the right end of the bar code and frames.py read no value (run 3 on 2026-11-19).
# Brings the counter page to the front. Returns $false when another window stays in front.
# Reason: a run on 2026-11-19 recorded another window because Chrome did not come to the front.
function Show-CounterPage {
    $h = [LC.U]::Find('Frame Counter')
    if ($h -eq [IntPtr]::Zero) { return $false }
    [void][LC.U]::ShowWindow($h, 3)   # SW_MAXIMIZE
    [LC.U]::keybd_event(0x12, 0, 0, [IntPtr]::Zero); [LC.U]::keybd_event(0x12, 0, 2, [IntPtr]::Zero)
    [void][LC.U]::SetForegroundWindow($h)
    Start-Sleep -Milliseconds 700
    return ([LC.U]::Title([LC.U]::GetForegroundWindow())).StartsWith('Frame Counter')
}
Start-Process chrome -ArgumentList @("--user-data-dir=$profileDir", '--no-first-run', '--hide-crash-restore-bubble', '--new-window', '--start-maximized', $uri) | Out-Null
Start-Sleep -Seconds 5
$results = @()
foreach ($mode in @('changes', 'fixed')) {
    $work = Join-Path $OutDir ("work_$mode.mp4")
    $argLine = & $tool $mode $Fps 20 120 1880 860 $work
    $argLine = $argLine -replace ' -movflags ', (" -t $Seconds -movflags ")
    $log = Join-Path $OutDir ("ffmpeg_$mode.log")
    if (-not (Show-CounterPage)) { $results += "$mode NOT RUN: the counter page is not in front. Nothing was recorded."; continue }
    $p = Start-Process -FilePath $ffmpeg -ArgumentList $argLine -NoNewWindow -PassThru -RedirectStandardError $log
    $null = $p.Handle   # keep the handle, so ExitCode is available after the wait
    if (-not $p.WaitForExit(($Seconds + 20) * 1000)) { Stop-Process -Id $p.Id -Force; $results += "$mode TIMEOUT (screen off?)"; continue }
    $p.WaitForExit()
    $front = [LC.U]::Title([LC.U]::GetForegroundWindow())
    if (-not $front.StartsWith('Frame Counter')) { $results += "$mode WARNING: at the end '$front' was in front. The result is not valid." }
    $json = Join-Path $OutDir ("frames_$mode.json")
    $txt = & python (Join-Path $here 'frames.py') $work --counter --expected-fps $Fps --json $json 2>&1 | Out-String
    $results += "$mode exit=$($p.ExitCode)"
    $results += $txt
}
Get-CimInstance Win32_Process -Filter "Name='chrome.exe'" | Where-Object { $_.CommandLine -like '*dr_counter_profile*' } | ForEach-Object { Stop-Process -Id $_.ProcessId -Force -ErrorAction SilentlyContinue }
$results
