# Test fixture only. SYSTEM schedules the currently logged-in recorded student with a limited token.
param([switch]$Desktop, [string]$Root)
$ErrorActionPreference = 'Stop'
$ExpectedInstallation = 'b2e98b07-9256-4823-9631-f350c46b014e'
$ExpectedHashes=@{
'M4-handout.docx'='f8489490a3de80e730018756774d97291359fcccb921b38963627f2a6764b8c5'
'M4-handout.pdf'='5b88dabf158cc6bf1827caa65ebf845fe07697a35342c2ac53dbd55861fb4295'
}
Add-Type @'
using System;
using System.Text;
using System.Runtime.InteropServices;
public static class HandoutNative {
 [DllImport("kernel32.dll")] public static extern uint WTSGetActiveConsoleSessionId();
 [DllImport("wtsapi32.dll", SetLastError=true)] public static extern bool WTSQueryUserToken(uint session, out IntPtr token);
 [DllImport("kernel32.dll")] public static extern bool CloseHandle(IntPtr token);
 [DllImport("userenv.dll", CharSet=CharSet.Unicode, SetLastError=true)] public static extern bool GetDefaultUserProfileDirectory(StringBuilder path, ref uint size);
}
'@
function Assert-NoLinks([string]$Path) {
    $item = Get-Item -LiteralPath $Path -Force
    while ($null -ne $item) {
        if (($item.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) { throw 'Reparse point refused.' }
        if ($item -is [IO.FileInfo]) { $item = $item.Directory } else { $item = $item.Parent }
    }
}
function Hash([string]$Path) { (Get-FileHash -LiteralPath $Path -Algorithm SHA256).Hash.ToLowerInvariant() }
if (!$Desktop) {
    if (![Security.Principal.WindowsIdentity]::GetCurrent().IsSystem) { throw 'SYSTEM launcher required.' }
    $statePath = 'C:\ProgramData\LabControl\installation.json'
    Assert-NoLinks $statePath
    $state = Get-Content -LiteralPath $statePath -Raw | ConvertFrom-Json
    if ($state.installation_id -ne $ExpectedInstallation -or !$state.create_student_account -or !$state.created_student_sid -or $state.removal_ready) { throw 'Unexpected installation.' }
    $sid = [string]$state.created_student_sid
    $token = [IntPtr]::Zero
    if (![HandoutNative]::WTSQueryUserToken([HandoutNative]::WTSGetActiveConsoleSessionId(), [ref]$token)) { throw 'No active student token.' }
    try {
        $active = New-Object Security.Principal.WindowsIdentity($token)
        try { if ($active.User.Value -ne $sid -or $active.Groups.Value -contains 'S-1-5-32-544') { throw 'Active user is not the recorded standard student.' } }
        finally { $active.Dispose() }
    } finally { [void][HandoutNative]::CloseHandle($token) }
    $Root = Join-Path $env:ProgramData ('LabControl.HandoutProbe.' + [guid]::NewGuid().ToString('N'))
    $acl = New-Object Security.AccessControl.DirectorySecurity
    $acl.SetOwner((New-Object Security.Principal.SecurityIdentifier('S-1-5-32-544')))
    $acl.SetAccessRuleProtection($true, $false)
    foreach ($who in @('S-1-5-18','S-1-5-32-544')) {
        $acl.AddAccessRule((New-Object Security.AccessControl.FileSystemAccessRule((New-Object Security.Principal.SecurityIdentifier($who)), 'FullControl', 'ContainerInherit,ObjectInherit', 'None', 'Allow')))
    }
    $acl.AddAccessRule((New-Object Security.AccessControl.FileSystemAccessRule((New-Object Security.Principal.SecurityIdentifier($sid)), 'ReadAndExecute', 'ContainerInherit,ObjectInherit', 'None', 'Allow')))
    [void][IO.Directory]::CreateDirectory($Root, $acl)
    Copy-Item -LiteralPath $PSCommandPath -Destination (Join-Path $Root 'probe.ps1')
    [IO.File]::WriteAllText((Join-Path $Root 'student-sid.txt'), $sid)
    $output = Join-Path $Root 'output'
    [void][IO.Directory]::CreateDirectory($output)
    $outputAcl = Get-Acl -LiteralPath $output
    $outputAcl.AddAccessRule((New-Object Security.AccessControl.FileSystemAccessRule((New-Object Security.Principal.SecurityIdentifier($sid)), 'Modify', 'ContainerInherit,ObjectInherit', 'None', 'Allow')))
    Set-Acl -LiteralPath $output -AclObject $outputAcl
    $scheduler = New-Object -ComObject Schedule.Service
    $scheduler.Connect()
    $folder = $scheduler.GetFolder('\')
    $definition = $scheduler.NewTask(0)
    $definition.Principal.UserId = $sid
    $definition.Principal.LogonType = 3
    $definition.Principal.RunLevel = 0
    $definition.Settings.ExecutionTimeLimit = 'PT2M'
    $definition.Settings.DisallowStartIfOnBatteries = $false
    $definition.Settings.StopIfGoingOnBatteries = $false
    $action = $definition.Actions.Create(0)
    $action.Path = Join-Path $env:SystemRoot 'System32\WindowsPowerShell\v1.0\powershell.exe'
    $action.Arguments = '-NoProfile -NonInteractive -ExecutionPolicy Bypass -File "' + (Join-Path $Root 'probe.ps1') + '" -Desktop -Root "' + $Root + '"'
    $name = 'LabControl.HandoutProbe.' + [guid]::NewGuid().ToString('N')
    $task = $folder.RegisterTaskDefinition($name, $definition, 2, $sid, $null, 3, $null)
    $xml = $task.Xml
    try {
        [void]$task.Run($null)
        $result = Join-Path $output 'result.json'
        $deadline = [DateTime]::UtcNow.AddSeconds(90)
        while (!(Test-Path -LiteralPath $result) -and [DateTime]::UtcNow -lt $deadline) { Start-Sleep -Milliseconds 250 }
        Write-Output ('Fixture output: ' + $Root)
        if (!(Test-Path -LiteralPath $result)) { throw 'Probe did not return.' }
        Get-Content -LiteralPath $result -Raw
    } finally { if ($folder.GetTask($name).Xml -eq $xml) { $folder.DeleteTask($name, 0) } }
    return
}
$checks=@()
try {
 Assert-NoLinks $Root
 $sid=[IO.File]::ReadAllText((Join-Path $Root 'student-sid.txt'))
 $identity=[Security.Principal.WindowsIdentity]::GetCurrent()
 if($identity.IsSystem -or $identity.User.Value -ne $sid -or $identity.Groups.Value -contains 'S-1-5-32-544' -or [Diagnostics.Process]::GetCurrentProcess().SessionId -eq 0){throw 'Expected standard student session'}
 $materials=Join-Path ([Environment]::GetFolderPath('DesktopDirectory')) 'Materials'
 Assert-NoLinks $materials
 if(@(Get-ChildItem $materials -Filter 'M4-handout*').Count -ne 2){throw 'Unexpected duplicate handouts'}
 foreach($name in $ExpectedHashes.Keys){
  $leaf=Join-Path $materials $name
  Assert-NoLinks $leaf
  if((Hash $leaf) -ne $ExpectedHashes[$name]){throw 'Unexpected delivered bytes'}
  $stream=[IO.File]::Open($leaf,[IO.FileMode]::Open,[IO.FileAccess]::ReadWrite,[IO.FileShare]::None)
  $length=$stream.Length
  try {$marker=[Text.Encoding]::ASCII.GetBytes('M4 reversible edit');[void]$stream.Seek(0,[IO.SeekOrigin]::End);$stream.Write($marker,0,$marker.Length);$stream.Flush($true);if($stream.Length -ne $length+$marker.Length){throw 'Append failed'}}finally{$stream.SetLength($length);$stream.Flush($true);$stream.Dispose()}
  if((Hash $leaf) -ne $ExpectedHashes[$name]){throw 'Original bytes changed'}
  $temporary=Join-Path $materials ([guid]::NewGuid().ToString('N')+'.m4-probe')
  $renamed=$temporary+'.renamed'
  try{[IO.File]::Copy($leaf,$temporary,$false);[IO.File]::AppendAllText($temporary,'editable');[IO.File]::Move($temporary,$renamed);[IO.File]::Delete($renamed)}finally{if([IO.File]::Exists($temporary)){[IO.File]::Delete($temporary)};if([IO.File]::Exists($renamed)){[IO.File]::Delete($renamed)}}
  $extension=[IO.Path]::GetExtension($name)
  $association=$null
  try{$association=(Get-ItemProperty ('HKCU:\Software\Microsoft\Windows\CurrentVersion\Explorer\FileExts\'+$extension+'\UserChoice') -ErrorAction Stop).ProgId}catch{}
  $checks+=@{name=$name;bytes=$length;sha256=$ExpectedHashes[$name];actual_file_write_verified=$true;copy_edit_rename_delete_verified=$true;user_choice=$association}
 }
 $viewers=@()
 foreach($process in @(Get-Process -Name msedge -ErrorAction SilentlyContinue)){
  if($process.SessionId -ne [Diagnostics.Process]::GetCurrentProcess().SessionId -or $process.MainWindowTitle -notlike '*M4-handout*'){continue}
  $native=Get-CimInstance Win32_Process -Filter ('ProcessId='+$process.Id)
  $owner=Invoke-CimMethod -InputObject $native -MethodName GetOwnerSid
  $viewers+=@{process='msedge';pid=$process.Id;window_title=$process.MainWindowTitle;recorded_student_owner=($owner.Sid -eq $sid);session_id=$process.SessionId}
 }
 $result=@{ok=$true;standard_student_verified=$true;no_duplicate_files=$true;checks=$checks;pdf_viewers=$viewers}
}catch{$result=@{ok=$false;checks=$checks;error=$_.Exception.GetType().Name;detail=$_.Exception.Message}}
$path=Join-Path $Root 'output\result.json'
[IO.File]::WriteAllText(($path+'.tmp'),($result|ConvertTo-Json -Depth 5))
[IO.File]::Move(($path+'.tmp'),$path)
