param(
    [string]$ExePath = "C:\Users\aa.fedin\Desktop\Tia_18_Agent\TiaPortal18Agent.exe"
)

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

$regPaths = @(
    "HKLM:\SOFTWARE\Siemens\Automation\Openness\18.0\Whitelist\$exeName\Entry",
    "HKLM:\SOFTWARE\WOW6432Node\Siemens\Automation\Openness\18.0\Whitelist\$exeName\Entry"
)

foreach ($p in $regPaths) {
    if (-not (Test-Path $p)) {
        New-Item -Path $p -Force | Out-Null
    }
    Set-ItemProperty -Path $p -Name "Path" -Value $ExePath
    Set-ItemProperty -Path $p -Name "FileHash" -Value $base64Hash
    Set-ItemProperty -Path $p -Name "Date" -Value $dateMod
    Set-ItemProperty -Path $p -Name "DateModified" -Value $dateMod
    Write-Host "Registered in $p"
    Write-Host "  Path: $ExePath"
    Write-Host "  FileHash: $base64Hash"
}
