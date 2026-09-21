param(
    [Parameter(ValueFromRemainingArguments = $true)]
    [string[]]$AgentArgs
)

$exe = Join-Path $PSScriptRoot "TiaPortalAgent.exe"
if (-not (Test-Path $exe)) {
    $exe = "C:\Users\aa.fedin\Desktop\TiaPortalAgent\TiaPortalAgent.exe"
}
if (-not (Test-Path $exe)) {
    $exe = "C:\Users\aa.fedin\Favorites\Tia_18_Agent\TiaPortalAgent.exe"
}

if (-not (Test-Path $exe)) {
    Write-Error "Executable not found: $exe"
    exit 1
}

if (-not $AgentArgs -or $AgentArgs.Length -eq 0) {
    $AgentArgs = @("--version")
}

try {
    $bytes = [System.IO.File]::ReadAllBytes($exe)
    $asm = [System.Reflection.Assembly]::Load($bytes)
    $asm.EntryPoint.Invoke($null, (, [string[]]$AgentArgs))
} catch {
    Write-Error "Execution error: $_"
    if ($_.Exception.InnerException) {
        Write-Error "Details: $($_.Exception.InnerException)"
    }
}
