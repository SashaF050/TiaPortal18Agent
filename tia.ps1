param(
    [string]$Command = "--version",
    [string]$Arg1 = "",
    [string]$Arg2 = "",
    [string]$Arg3 = ""
)

$exe = Join-Path $PSScriptRoot "TiaPortal18Agent.exe"
if (-not (Test-Path $exe)) {
    $exe = "C:\Users\aa.fedin\Desktop\Tia_18_Agent\TiaPortal18Agent.exe"
}

$rawArgs = @()
if ($Command) { $rawArgs += $Command }
if ($Arg1) { $rawArgs += $Arg1 }
if ($Arg2) { $rawArgs += $Arg2 }
if ($Arg3) { $rawArgs += $Arg3 }

try {
    $bytes = [System.IO.File]::ReadAllBytes($exe)
    $asm = [System.Reflection.Assembly]::Load($bytes)
    $asm.EntryPoint.Invoke($null, (, [string[]]$rawArgs))
} catch {
    Write-Error "Execution error: $_"
    if ($_.Exception.InnerException) {
        Write-Error "Details: $($_.Exception.InnerException)"
    }
}

