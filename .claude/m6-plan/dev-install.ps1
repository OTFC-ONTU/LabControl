<#
.SYNOPSIS
  Development-only installer for the LabControl agent (ROADMAP M2). Run as Administrator
  on the Windows VM or on PC-00 until the real Setup.exe exists (M4).

.DESCRIPTION
  Lays the PC out exactly as Setup.exe will (docs/INSTALLER.md steps 3-6a, ARCHITECTURE section 5):

    C:\Program Files\LabControl\app\<version>\agent.exe, session.exe   side-by-side (D-19)
    C:\Program Files\LabControl\app\current                              names <version>
    C:\ProgramData\LabControl\                                           SYSTEM + Administrators only

  then provisions the trust material with `agent.exe --install` (INSTALLER.md step 4, the
  same code the M4 installer uses), registers the "LabControl" service as LocalSystem with
  restart-on-failure, adds the firewall rule and the Defender exclusion, and starts it.

  With -Student it also performs INSTALLER.md step 8 - the standard `student` account with
  auto-logon - so the "student cannot stop the service / kill session.exe / read ProgramData"
  checks of ROADMAP M2 can be run on PC-00 before Setup.exe exists. The password goes to the
  LSA secret `DefaultPassword`, as Setup will do; it is never printed.

  It does NOT set the hostname, Wake-on-LAN or the power settings, and it does not hide the
  administrator from the logon screen; those are Setup.exe's job and are not needed to test
  the agent.

.PARAMETER Build
  Directory holding agent.exe and session.exe (a `dotnet publish` output, e.g. artifacts\win-arm64).
.PARAMETER Payload
  The USB payload written by the console: setup.json + ca.crt (or the folder containing LabControl\).
.PARAMETER Number
  The PC number (1..30). Defaults to the number in a PC-NN hostname.
.PARAMETER ConsoleHost
  Pin the console address (host or host:port) on a VM whose network does not pass UDP broadcasts.
.PARAMETER Student
  Create the standard `student` account (INSTALLER.md step 8) with auto-logon at boot. Safe to
  repeat: an existing account is brought to the same state.
.PARAMETER RemoveStudent
  With -Uninstall: also remove the auto-logon and the `student` account and its profile. Without
  it the account is kept, as Setup.exe --uninstall keeps it.
.PARAMETER Reprovision
  Provision again with a new agent id even if agent.json exists (a reinstall, D-25).
.PARAMETER Uninstall
  Remove the service, the firewall rule, the exclusion and Program Files\LabControl. Keeps
  ProgramData\LabControl (the PC's identity) unless -PurgeData is also given.

.EXAMPLE
  .\dev-install.ps1 -Build Z:\artifacts\win-arm64 -Payload Z:\usb -Number 1 -ConsoleHost 192.168.64.1
.EXAMPLE
  .\dev-install.ps1 -Build D:\win-x64 -Payload D:\usb -Number 0 -Student
#>
[CmdletBinding()]
param(
    [string] $Build,
    [string] $Payload,
    [int]    $Number = 0,
    [string] $ConsoleHost,
    [switch] $Student,
    [switch] $RemoveStudent,
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
$BeaconPort  = 47801      # Defaults.BeaconPort: the console's UDP discovery beacon
$StudentName     = 'student'
$StudentPassword = '1'     # Defaults.StudentDefaultPassword (D-09). Never write it to the console or a log.
$UsersSid         = 'S-1-5-32-545'   # BUILTIN\Users, by SID so a Ukrainian Windows works too
$AdministratorsSid = 'S-1-5-32-544'
$WinlogonKey     = 'HKLM:\SOFTWARE\Microsoft\Windows NT\CurrentVersion\Winlogon'

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

# INSTALLER.md step 8 stores the auto-logon password as the LSA secret `DefaultPassword`,
# not as a plain registry value. Windows PowerShell has no cmdlet for that, so the two
# advapi32 calls Setup.exe will make are compiled here on the fly.
$lsaSource = @'
using System;
using System.ComponentModel;
using System.Runtime.InteropServices;
public static class LabControlLsa
{
    [StructLayout(LayoutKind.Sequential)]
    struct LSA_UNICODE_STRING { public ushort Length; public ushort MaximumLength; public IntPtr Buffer; }
    [StructLayout(LayoutKind.Sequential)]
    struct LSA_OBJECT_ATTRIBUTES { public int Length; public IntPtr RootDirectory; public IntPtr ObjectName; public uint Attributes; public IntPtr SecurityDescriptor; public IntPtr SecurityQualityOfService; }
    [DllImport("advapi32.dll")] static extern uint LsaOpenPolicy(IntPtr systemName, ref LSA_OBJECT_ATTRIBUTES attributes, uint access, out IntPtr policy);
    [DllImport("advapi32.dll")] static extern uint LsaStorePrivateData(IntPtr policy, ref LSA_UNICODE_STRING key, ref LSA_UNICODE_STRING data);
    [DllImport("advapi32.dll", EntryPoint = "LsaStorePrivateData")] static extern uint LsaDeletePrivateData(IntPtr policy, ref LSA_UNICODE_STRING key, IntPtr data);
    [DllImport("advapi32.dll")] static extern uint LsaClose(IntPtr policy);
    [DllImport("advapi32.dll")] static extern int LsaNtStatusToWinError(uint status);
    const uint POLICY_CREATE_SECRET = 0x00000020;
    static LSA_UNICODE_STRING Str(string s)
    {
        var u = new LSA_UNICODE_STRING();
        u.Buffer = Marshal.StringToHGlobalUni(s);
        u.Length = (ushort)(s.Length * 2);
        u.MaximumLength = (ushort)((s.Length + 1) * 2);
        return u;
    }
    static void Check(uint status, string call)
    {
        if (status != 0) throw new Win32Exception(LsaNtStatusToWinError(status), call + " failed");
    }
    /// An empty value deletes the secret.
    public static void Store(string key, string value)
    {
        var attributes = new LSA_OBJECT_ATTRIBUTES();
        attributes.Length = Marshal.SizeOf(attributes);
        IntPtr policy;
        Check(LsaOpenPolicy(IntPtr.Zero, ref attributes, POLICY_CREATE_SECRET, out policy), "LsaOpenPolicy");
        var k = Str(key);
        var v = string.IsNullOrEmpty(value) ? new LSA_UNICODE_STRING() : Str(value);
        try
        {
            if (v.Buffer == IntPtr.Zero) Check(LsaDeletePrivateData(policy, ref k, IntPtr.Zero), "LsaStorePrivateData");
            else Check(LsaStorePrivateData(policy, ref k, ref v), "LsaStorePrivateData");
        }
        finally
        {
            Marshal.FreeHGlobal(k.Buffer);
            if (v.Buffer != IntPtr.Zero) Marshal.FreeHGlobal(v.Buffer);
            LsaClose(policy);
        }
    }
}
'@

function Set-AutoLogonSecret([string] $password) {
    if (-not ('LabControlLsa' -as [type])) { Add-Type -TypeDefinition $lsaSource -Language CSharp }
    [LabControlLsa]::Store('DefaultPassword', $password)
}

function Get-StudentAccount {
    Get-LocalUser -Name $StudentName -ErrorAction SilentlyContinue
}

function Install-StudentAccount {
    $secure = ConvertTo-SecureString $StudentPassword -AsPlainText -Force
    if (Get-StudentAccount) {
        Step "Account $StudentName exists; bringing it to the expected state"
        Set-LocalUser -Name $StudentName -Password $secure -PasswordNeverExpires $true -UserMayChangePassword $false -AccountNeverExpires -Description 'Student'
        Enable-LocalUser -Name $StudentName
    } else {
        Step "Creating the standard account $StudentName"
        try {
            New-LocalUser -Name $StudentName -Password $secure -PasswordNeverExpires -UserMayNotChangePassword -AccountNeverExpires -Description 'Student' | Out-Null
        } catch {
            # A local password policy with a minimum length or complexity refuses "1";
            # INSTALLER.md step 8 relaxes the policy with secedit and retries.
            Write-Warning "New-LocalUser: $($_.Exception.Message); relaxing the local password policy and retrying"
            $cfg = Join-Path $env:TEMP 'labcontrol-secpol.inf'
            $db  = Join-Path $env:TEMP 'labcontrol-secpol.sdb'
            Set-Content -Path $cfg -Encoding Unicode -Value @(
                '[Unicode]', 'Unicode=yes',
                '[System Access]', 'MinimumPasswordLength = 0', 'PasswordComplexity = 0',
                '[Version]', 'signature="$CHICAGO$"', 'Revision=1')
            & secedit.exe /configure /db $db /cfg $cfg /areas SECURITYPOLICY /quiet | Out-Null
            Remove-Item -Force $cfg, $db -ErrorAction SilentlyContinue
            New-LocalUser -Name $StudentName -Password $secure -PasswordNeverExpires -UserMayNotChangePassword -AccountNeverExpires -Description 'Student' | Out-Null
        }
    }
    Done 'account ready'

    Step "Group membership: $StudentName in Users only"
    $user = Get-StudentAccount
    $users  = Get-LocalGroup -SID $UsersSid
    $admins = Get-LocalGroup -SID $AdministratorsSid
    if (-not (Get-LocalGroupMember -Group $users -ErrorAction SilentlyContinue | Where-Object SID -eq $user.SID)) {
        Add-LocalGroupMember -Group $users -Member $user
    }
    if (Get-LocalGroupMember -Group $admins -ErrorAction SilentlyContinue | Where-Object SID -eq $user.SID) {
        Remove-LocalGroupMember -Group $admins -Member $user
        Done 'removed from Administrators'
    }
    Done 'member of Users, not an administrator'

    Step "Auto-logon as $StudentName at boot (Winlogon + LSA secret)"
    Set-ItemProperty -Path $WinlogonKey -Name AutoAdminLogon     -Value '1'           -Type String
    Set-ItemProperty -Path $WinlogonKey -Name DefaultUserName    -Value $StudentName  -Type String
    Set-ItemProperty -Path $WinlogonKey -Name DefaultDomainName  -Value $env:COMPUTERNAME -Type String
    Remove-ItemProperty -Path $WinlogonKey -Name AutoLogonCount   -ErrorAction SilentlyContinue
    Remove-ItemProperty -Path $WinlogonKey -Name DefaultPassword  -ErrorAction SilentlyContinue   # never in plain registry
    Set-AutoLogonSecret $StudentPassword
    Done 'auto-logon set; takes effect at the next boot'
}

function Remove-StudentAccount {
    Step 'Removing the auto-logon'
    if ((Get-ItemProperty $WinlogonKey -ErrorAction SilentlyContinue).DefaultUserName -eq $StudentName) {
        Set-ItemProperty -Path $WinlogonKey -Name AutoAdminLogon -Value '0' -Type String
        Remove-ItemProperty -Path $WinlogonKey -Name DefaultUserName -ErrorAction SilentlyContinue
        Remove-ItemProperty -Path $WinlogonKey -Name DefaultDomainName -ErrorAction SilentlyContinue
        Remove-ItemProperty -Path $WinlogonKey -Name DefaultPassword -ErrorAction SilentlyContinue
        try { Set-AutoLogonSecret '' } catch { Write-Warning "LSA secret: $($_.Exception.Message)" }
        Done 'removed'
    } else {
        Done "auto-logon was not set to $StudentName; left alone"
    }
    $user = Get-StudentAccount
    if ($user) {
        Step "Removing the account $StudentName and its profile"
        Get-CimInstance Win32_UserProfile | Where-Object { $_.SID -eq $user.SID.Value } | Remove-CimInstance -ErrorAction SilentlyContinue
        Remove-LocalUser -Name $StudentName
        Done 'removed'
    }
}

if ($Uninstall) {
    if ($RemoveStudent) { Remove-StudentAccount }
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
        Step "Deleting $DataDir (the PC's identity - it will get a new agent id next time)"
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

# The UTM shared folder is a WebDAV drive, and Windows refuses WebDAV files larger than
# 50 MB by default; agent.exe is bigger. Raise the limit once. Restarting WebClient drops
# the drive for a few seconds, so wait for the build directory to come back.
$webClient = 'HKLM:\SYSTEM\CurrentControlSet\Services\WebClient\Parameters'
if ((Get-Service WebClient -ErrorAction SilentlyContinue) -and (Get-ItemProperty $webClient -ErrorAction SilentlyContinue).FileSizeLimitInBytes -ne 4294967295) {
    Step 'Raising the WebDAV file size limit (shared folders)'
    Set-ItemProperty $webClient -Name FileSizeLimitInBytes -Value 4294967295 -Type DWord
    Restart-Service WebClient -Force
    $deadline = (Get-Date).AddSeconds(60)
    while (-not (Test-Path $agentSource) -and (Get-Date) -lt $deadline) { Start-Sleep -Seconds 2; Get-ChildItem $Build -ErrorAction SilentlyContinue | Out-Null }
    if (-not (Test-Path $agentSource)) { throw "$Build is not reachable after restarting WebClient; reopen the drive in Explorer and run again." }
    Done 'limit raised'
}

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

Step 'Firewall rules (inbound allow: console beacon UDP 47801, ICMP echo) in group LabControl'
# By port, not by program path: the agent's path changes with every self-update (app\<version>\,
# D-19) and a per-program rule silently stops the beacon for the new version (PC-00, 2026-09-07).
Get-NetFirewallRule -Group $RuleGroup -ErrorAction SilentlyContinue | Remove-NetFirewallRule
New-NetFirewallRule -DisplayName 'LabControl Beacon' -Group $RuleGroup -Direction Inbound -Action Allow -Protocol UDP -LocalPort $BeaconPort -Profile Any | Out-Null
New-NetFirewallRule -DisplayName 'LabControl ICMP' -Group $RuleGroup -Direction Inbound -Action Allow -Protocol ICMPv4 -IcmpType 8 -Profile Any | Out-Null
Done 'rules added'

Step "Defender exclusion for $InstallDir (unsigned binaries, D-15)"
try {
    Add-MpPreference -ExclusionPath $InstallDir -ErrorAction Stop
    Done 'exclusion added'
} catch {
    Write-Warning "Could not add the Defender exclusion: $($_.Exception.Message). If a third-party antivirus is installed, exclude $InstallDir by hand (D-15)."
}

if ($Student) { Install-StudentAccount }

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
if ($Student) {
    Write-Host "  student  : account '$StudentName' (standard user), auto-logon from the next boot"
}
Write-Host 'Now open Enrol PCs in the console and type the passphrase; the PC enrols on its own.'
if ($Student) {
    Write-Host "Then reboot: the PC must log on as '$StudentName' by itself. As $StudentName, check that services.msc"
    Write-Host "cannot stop $ServiceName, Task Manager cannot end session.exe, and $DataDir is access denied."
}
