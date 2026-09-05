<#
.SYNOPSIS
  Development-only installer for the LabControl agent (ROADMAP M2). Run as Administrator
  on the Windows VM or on PC-00 until the real Setup.exe exists (M4).

.DESCRIPTION
  Lays the PC out exactly as Setup.exe will (docs/INSTALLER.md steps 3-6a, ARCHITECTURE §5):

    C:\Program Files\LabControl\app\<version>\agent.exe, session.exe   side-by-side (D-19)
    C:\Program Files\LabControl\app\current                              names <version>
    C:\ProgramData\LabControl\                                           SYSTEM + Administrators only

  then provisions the trust material with `agent.exe --install` (INSTALLER.md step 4, the
  same code the M4 installer uses), registers the "LabControl" service as LocalSystem with
  restart-on-failure, adds the firewall rule and the Defender exclusion, and starts it.

  It does NOT set the hostname, Wake-on-LAN, power settings or the `student` account; those
  are Setup.exe's job and are not needed to test the agent.

.PARAMETER Build
  Directory holding agent.exe and session.exe (a `dotnet publish` output, e.g. artifacts\win-arm64).
.PARAMETER Payload
  The USB payload written by the console: setup.json + ca.crt (or the folder containing LabControl\).
.PARAMETER Number
  The PC number (1..30). Defaults to the number in a PC-NN hostname.
.PARAMETER ConsoleHost
  Pin the console address (host or host:port) on a VM whose network does not pass UDP broadcasts.
.PARAMETER Reprovision
  Provision again with a new agent id even if agent.json exists (a reinstall, D-25).
.PARAMETER Uninstall
  Remove the service, the firewall rule, the exclusion and Program Files\LabControl. Keeps
  ProgramData\LabControl (the PC's identity) unless -PurgeData is also given.

.EXAMPLE
  .\dev-install.ps1 -Build Z:\artifacts\win-arm64 -Payload Z:\usb -Number 1 -ConsoleHost 192.168.64.1
#>
[CmdletBinding()]
param(
    [string] $Build,
    [string] $Payload,
    [int]    $Number = 0,
    [string] $ConsoleHost,
    [switch] $Reprovision,
    [switch] $Uninstall,
    [switch] $PurgeData
)

$ErrorActionPreference = 'Stop'

# Same values as LabControl.Shared/Defaults.cs; keep them in step.
$ServiceName = 'LabControl'
$DisplayName = 'LabControl Agent'
$InstallDir  = 'C:\Program Files\LabControl'
$DataDir     = 'C:\ProgramData\LabControl'
$AppDir      = Join-Path $InstallDir 'app'
$CurrentFile = Join-Path $AppDir 'current'
$RuleGroup   = 'LabControl'

function Step([string] $text) { Write-Host "==> $text" -ForegroundColor Cyan }
function Done([string] $text) { Write-Host "    $text" -ForegroundColor Green }

$principal = New-Object Security.Principal.WindowsPrincipal([Security.Principal.WindowsIdentity]::GetCurrent())
if (-not $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) {
    throw 'Run this script from an elevated (Administrator) PowerShell.'
}

function Get-Service-IfExists {
    Get-Service -Name $ServiceName -ErrorAction SilentlyContinue
}

function Stop-AgentService {
    $svc = Get-Service-IfExists
    if ($svc -and $svc.Status -ne 'Stopped') {
        Step "Stopping service $ServiceName"
        Stop-Service -Name $ServiceName -Force
        $svc.WaitForStatus('Stopped', [TimeSpan]::FromSeconds(30))
        Done 'stopped'
    }
}

if ($Uninstall) {
    Stop-AgentService
    if (Get-Service-IfExists) {
        Step "Deleting service $ServiceName"
        & sc.exe delete $ServiceName | Out-Null
        Done 'deleted'
    }
    Step 'Removing firewall rules and the Defender exclusion'
    Get-NetFirewallRule -Group $RuleGroup -ErrorAction SilentlyContinue | Remove-NetFirewallRule
    try { Remove-MpPreference -ExclusionPath $InstallDir -ErrorAction Stop } catch { Write-Warning "Defender: $($_.Exception.Message)" }
    Done 'removed'
    if (Test-Path $InstallDir) {
        Step "Deleting $InstallDir"
        Remove-Item -Recurse -Force $InstallDir
        Done 'deleted'
    }
    if ($PurgeData -and (Test-Path $DataDir)) {
        Step "Deleting $DataDir (the PC's identity — it will get a new agent id next time)"
        Remove-Item -Recurse -Force $DataDir
        Done 'deleted'
    } elseif (Test-Path $DataDir) {
        Done "$DataDir kept (agent id, key, certificate); add -PurgeData to remove it"
    }
    exit 0
}

if (-not $Build)   { throw '-Build <dir> is required: the directory with agent.exe and session.exe.' }
if (-not $Payload) { throw '-Payload <dir> is required: the USB payload (setup.json + ca.crt) written by the console.' }

$agentSource   = Join-Path $Build 'agent.exe'
$sessionSource = Join-Path $Build 'session.exe'
if (-not (Test-Path $agentSource))   { throw "agent.exe not found in $Build" }
if (-not (Test-Path $sessionSource)) { throw "session.exe not found in $Build" }

Step 'Reading the version from agent.exe'
$version = (& $agentSource --version | Select-Object -Last 1).Trim()
if (-not $version -or $version -notmatch '^[A-Za-z0-9][A-Za-z0-9.+-]*$') { throw "agent.exe --version returned '$version'" }
Done "version $version"

Stop-AgentService

Step "Installing binaries into $AppDir\$version"
$versionDir = Join-Path $AppDir $version
New-Item -ItemType Directory -Force -Path $versionDir | Out-Null
Copy-Item -Force $agentSource   (Join-Path $versionDir 'agent.exe')
Copy-Item -Force $sessionSource (Join-Path $versionDir 'session.exe')
Set-Content -Path $CurrentFile -Value $version -NoNewline -Encoding ascii
Add-Content -Path $CurrentFile -Value "`r`n" -NoNewline -Encoding ascii
Done "app\current -> $version"

Step "Locking down $DataDir (SYSTEM + Administrators only)"
New-Item -ItemType Directory -Force -Path $DataDir | Out-Null
New-Item -ItemType Directory -Force -Path (Join-Path $DataDir 'logs') | Out-Null
& icacls $DataDir /inheritance:r /grant:r 'SYSTEM:(OI)(CI)F' 'Administrators:(OI)(CI)F' | Out-Null
if ($LASTEXITCODE -ne 0) { throw "icacls failed with exit code $LASTEXITCODE" }
Done 'ACL set'

$agentExe = Join-Path $versionDir 'agent.exe'
$provisioned = Test-Path (Join-Path $DataDir 'agent.json')
if ($provisioned -and -not $Reprovision) {
    Step 'Already provisioned; keeping agent.json, the key and the certificate (-Reprovision to redo)'
} else {
    Step 'Provisioning the trust material (agent.exe --install)'
    $installArgs = @('--install', '--payload', $Payload)
    if ($Number -gt 0)  { $installArgs += @('--number', $Number) }
    if ($ConsoleHost)   { $installArgs += @('--console', $ConsoleHost) }
    if ($Reprovision)   { $installArgs += '--force' }
    & $agentExe @installArgs
    if ($LASTEXITCODE -ne 0) { throw "agent.exe --install failed with exit code $LASTEXITCODE" }
    Done 'provisioned'
}

$binPath = "`"$agentExe`""
if (Get-Service-IfExists) {
    Step "Repointing service $ServiceName at $agentExe"
    & sc.exe config $ServiceName binPath= $binPath start= auto obj= LocalSystem | Out-Null
} else {
    Step "Creating service $ServiceName"
    & sc.exe create $ServiceName binPath= $binPath start= auto obj= LocalSystem DisplayName= "$DisplayName" | Out-Null
    if ($LASTEXITCODE -ne 0) { throw "sc create failed with exit code $LASTEXITCODE" }
}
& sc.exe description $ServiceName 'Classroom agent for the LabControl teacher console. Do not stop.' | Out-Null
# Recovery: restart three times, 10 s apart; the fourth action (agent.exe --rollback) is M4.
& sc.exe failure $ServiceName reset= 86400 actions= restart/10000/restart/10000/restart/10000 | Out-Null
& sc.exe failureflag $ServiceName 1 | Out-Null
Done 'service configured (LocalSystem, auto-start, restart on failure)'

Step 'Firewall rule (inbound allow for agent.exe, ICMP echo) in group LabControl'
Get-NetFirewallRule -Group $RuleGroup -ErrorAction SilentlyContinue | Remove-NetFirewallRule
New-NetFirewallRule -DisplayName 'LabControl Agent' -Group $RuleGroup -Direction Inbound -Action Allow -Program $agentExe -Profile Any | Out-Null
New-NetFirewallRule -DisplayName 'LabControl ICMP' -Group $RuleGroup -Direction Inbound -Action Allow -Protocol ICMPv4 -IcmpType 8 -Profile Any | Out-Null
Done 'rules added'

Step "Defender exclusion for $InstallDir (unsigned binaries, D-15)"
try {
    Add-MpPreference -ExclusionPath $InstallDir -ErrorAction Stop
    Done 'exclusion added'
} catch {
    Write-Warning "Could not add the Defender exclusion: $($_.Exception.Message). If a third-party antivirus is installed, exclude $InstallDir by hand (D-15)."
}

Step "Starting service $ServiceName"
Start-Service -Name $ServiceName
(Get-Service -Name $ServiceName).WaitForStatus('Running', [TimeSpan]::FromSeconds(30))
Done 'running'

Write-Host ''
Write-Host "Installed LabControl agent $version." -ForegroundColor Green
Write-Host "  binaries : $versionDir"
Write-Host "  data     : $DataDir"
Write-Host "  log      : $DataDir\logs\agent-$(Get-Date -Format yyyyMMdd).log"
Write-Host "  watch    : Get-Content '$DataDir\logs\agent-$(Get-Date -Format yyyyMMdd).log' -Wait -Tail 20"
Write-Host 'Now open Enrol PCs in the console and type the passphrase; the PC enrols on its own.'
