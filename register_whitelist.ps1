param(
    [string]$ExePath = "$PSScriptRoot\TiaPortalAgent.exe"
)

if (-not (Test-Path $ExePath)) {
    $ExePath = "C:\Users\aa.fedin\Desktop\TiaPortalAgent\TiaPortalAgent.exe"
}
if (-not (Test-Path $ExePath)) {
    $ExePath = "C:\Users\aa.fedin\Favorites\Tia_18_Agent\TiaPortalAgent.exe"
}

if (-not (Test-Path $ExePath)) {
    Write-Error "Exe not found: $ExePath"
    exit 1
}

$hasher = [System.Security.Cryptography.SHA256]::Create()
$stream = [System.IO.File]::OpenRead($ExePath)
$hashBytes = $hasher.ComputeHash($stream)
$stream.Close()
$base64Hash = [Convert]::ToBase64String($hashBytes)
$dateMod = (Get-Item $ExePath).LastWriteTimeUtc.ToString("yyyy/MM/dd HH:mm:ss")
$exeName = [System.IO.Path]::GetFileName($ExePath)

$versions = @("18.0", "19.0", "20.0", "21.0")
try {
    $opennessKeys = Get-ChildItem "HKLM:\SOFTWARE\Siemens\Automation\Openness" -ErrorAction SilentlyContinue | Select-Object -ExpandProperty PSChildName
    if ($opennessKeys) {
        $versions = ($versions + $opennessKeys) | Select-Object -Unique
    }
} catch {}

foreach ($ver in $versions) {
    $regPaths = @(
        "HKLM:\SOFTWARE\Siemens\Automation\Openness\$ver\Whitelist\$exeName\Entry",
        "HKLM:\SOFTWARE\WOW6432Node\Siemens\Automation\Openness\$ver\Whitelist\$exeName\Entry"
    )

    foreach ($p in $regPaths) {
        try {
            $parent = Split-Path $p -Parent
            if (-not (Test-Path $parent)) {
                New-Item -Path $parent -Force -ErrorAction SilentlyContinue | Out-Null
            }
            if (-not (Test-Path $p)) {
                New-Item -Path $p -Force -ErrorAction SilentlyContinue | Out-Null
            }
            Set-ItemProperty -Path $p -Name "Path" -Value $ExePath -ErrorAction SilentlyContinue
            Set-ItemProperty -Path $p -Name "FileHash" -Value $base64Hash -ErrorAction SilentlyContinue
            Set-ItemProperty -Path $p -Name "Date" -Value $dateMod -ErrorAction SilentlyContinue
            Set-ItemProperty -Path $p -Name "DateModified" -Value $dateMod -ErrorAction SilentlyContinue
            Write-Host "Registered in $p"
        } catch {
        }
    }
}
Write-Host "Whitelist registered for $exeName ($base64Hash)"
