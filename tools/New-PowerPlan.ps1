# Creates the hidden "ClevoFanControl - locked" power scheme FanControl uses for
# locked mode. Run once, from an elevated PowerShell:
#
#   powershell -ExecutionPolicy Bypass -File tools\New-PowerPlan.ps1
#
# Creating a scheme needs admin (the static power settings schema). After this,
# FanControl only writes values into it and switches to it on lock, which a
# normal user may do. Re-running is safe: it deletes the old scheme first.
#
# FanControl finds the scheme by its exact name, so the name must not change.

$name = "ClevoFanControl - locked"
$sub  = "54533251-82be-4824-96c1-47b60b740d00"

$admin = ([Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent()).IsInRole(
    [Security.Principal.WindowsBuiltInRole]::Administrator)
if (-not $admin) {
    Write-Error "Run this from an elevated PowerShell: creating a power scheme needs admin."
    exit 1
}

# Remove an old copy by name, so repeated runs do not pile up schemes.
foreach ($line in (powercfg /list)) {
    $m = [regex]::Match($line, "([0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12})\s+\((.+)\)")
    if ($m.Success -and $m.Groups[2].Value -eq $name) {
        powercfg /delete $m.Groups[1].Value | Out-Null
    }
}

# Duplicate Balanced: it carries a complete set of values, so FanControl only
# has to overwrite the handful it cares about.
$temp = (powercfg /duplicatescheme 381b4222-f694-41f0-9685-ff5bb260df2e |
         Select-String -Pattern "([0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12})").Matches[0].Groups[1].Value
if (-not $temp) {
    Write-Error "powercfg /duplicatescheme failed"
    exit 1
}

powercfg /changename $temp $name "Managed by FanControl for locked mode"

# Seed the same values FanControl writes, so the plan is already sane even
# before the first lock: clamped clock, no boost, high energy preference.
powercfg /setacvalueindex $temp $sub bc5038f7-23e0-4960-96da-33abaf5935ec 25
powercfg /setdcvalueindex $temp $sub bc5038f7-23e0-4960-96da-33abaf5935ec 25
powercfg /setacvalueindex $temp $sub be337238-0d82-4146-a960-4f3749d470c7 0
powercfg /setdcvalueindex $temp $sub be337238-0d82-4146-a960-4f3749d470c7 0
powercfg /setacvalueindex $temp $sub 36687f9e-e3a5-4dbf-b1dc-15eb381c6863 90
powercfg /setdcvalueindex $temp $sub 36687f9e-e3a5-4dbf-b1dc-15eb381c6863 90
powercfg /setacvalueindex $temp $sub 0cc5b647-c1df-4637-891a-dec35c318583 60
powercfg /setdcvalueindex $temp $sub 0cc5b647-c1df-4637-891a-dec35c318583 60

Write-Host ""
Write-Host "Created: $name"
Write-Host "GUID:    $temp"
Write-Host ""
Write-Host "Restart FanControl for it to pick the scheme up. It stays hidden from"
Write-Host "Settings and appears in 'powercfg /L'."
