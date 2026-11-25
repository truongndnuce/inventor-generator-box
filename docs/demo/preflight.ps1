# Pre-flight check + split-screen layout for the Smart Drawing demo.
# Usage: powershell -ExecutionPolicy Bypass -File docs\demo\preflight.ps1 [-Layout]
param([switch]$Layout)

$ok = $true
function Check($name, $pass, $hint) {
    if ($pass) { Write-Host "[ OK ] $name" -ForegroundColor Green }
    else { Write-Host "[FAIL] $name -> $hint" -ForegroundColor Red; $script:ok = $false }
}

$port = { param($p) [bool](Get-NetTCPConnection -LocalPort $p -State Listen -ErrorAction SilentlyContinue) }
Check "PostgreSQL :5432" (& $port 5432) "start the PostgreSQL service"
Check "Web API :18080"   (& $port 18080) "dotnet run --project be/Cadflow.Api"
Check "Inventor running" ([bool](Get-Process Inventor -ErrorAction SilentlyContinue)) "start Inventor 2027"

try {
    $w = Invoke-RestMethod http://localhost:18080/api/workers -TimeoutSec 5
    Check "Worker online ($(@($w).Count) registered)" (@($w).Count -gt 0) "load the Cadflow add-ins in Inventor"
} catch { Check "Worker endpoint" $false $_.Exception.Message }

foreach ($u in "http://localhost:18080/", "http://localhost:18080/plm/") {
    try { $r = Invoke-WebRequest $u -UseBasicParsing -TimeoutSec 5; Check "GET $u" ($r.StatusCode -eq 200) "build fe / fe-plm" }
    catch { Check "GET $u" $false "build fe / fe-plm (npm run build)" }
}
try { $null = Invoke-WebRequest https://developer.autodesk.com -UseBasicParsing -TimeoutSec 8; Check "Internet (Forge Viewer)" $true "" }
catch { Check "Internet (Forge Viewer)" $false "viewer SDK loads from developer.api.autodesk.com" }

if ($Layout) {
    Add-Type @"
using System; using System.Runtime.InteropServices;
public class Win {
  [DllImport("user32.dll")] public static extern bool MoveWindow(IntPtr h,int x,int y,int w,int ht,bool r);
  [DllImport("user32.dll")] public static extern bool ShowWindow(IntPtr h,int c);
}
"@
    Add-Type -AssemblyName System.Windows.Forms
    $s = [System.Windows.Forms.Screen]::PrimaryScreen.WorkingArea
    $half = [int]($s.Width / 2)
    $inv = Get-Process Inventor -ErrorAction SilentlyContinue | Where-Object MainWindowHandle -ne 0 | Select-Object -First 1
    $chr = Get-Process chrome -ErrorAction SilentlyContinue | Where-Object MainWindowHandle -ne 0 | Select-Object -First 1
    if ($inv) { [Win]::ShowWindow($inv.MainWindowHandle, 9) | Out-Null; [Win]::MoveWindow($inv.MainWindowHandle, $s.X, $s.Y, $half, $s.Height, $true) | Out-Null }
    if ($chr) { [Win]::ShowWindow($chr.MainWindowHandle, 9) | Out-Null; [Win]::MoveWindow($chr.MainWindowHandle, $s.X + $half, $s.Y, $half, $s.Height, $true) | Out-Null }
    Write-Host "Layout: Inventor left, Chrome right." -ForegroundColor Cyan
}

if ($ok) { Write-Host "`nAll checks passed. Start Bandicam." -ForegroundColor Green } else { Write-Host "`nFix the failures above first." -ForegroundColor Yellow }
