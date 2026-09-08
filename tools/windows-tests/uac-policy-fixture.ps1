param([Parameter(Mandatory=$true)][ValidateSet('Apply','Restore')][string]$Mode,[Parameter(Mandatory=$true)][string]$InstallationId)
$ErrorActionPreference='Stop'
if(-not [Security.Principal.WindowsIdentity]::GetCurrent().IsSystem){throw 'SYSTEM required'}
$installation=Get-Content 'C:\ProgramData\LabControl\installation.json'|ConvertFrom-Json
if($installation.installation_id -ne $InstallationId){throw 'Installation changed'}
$root='C:\ProgramData\LabControl.NativeSmoke.UacPolicy'
$statePath=Join-Path $root 'state.json'
if(-not(Test-Path $root)){
 if($Mode -ne 'Apply'){throw 'Fixture state missing'}
 $acl=New-Object Security.AccessControl.DirectorySecurity
 $acl.SetOwner([Security.Principal.SecurityIdentifier]'S-1-5-32-544')
 $acl.SetAccessRuleProtection($true,$false)
 foreach($sid in @('S-1-5-18','S-1-5-32-544')){$acl.AddAccessRule((New-Object Security.AccessControl.FileSystemAccessRule([Security.Principal.SecurityIdentifier]$sid,'FullControl','ContainerInherit,ObjectInherit','None','Allow')))}
 [IO.Directory]::CreateDirectory($root,$acl)|Out-Null
}
$node=Get-Item $root
if($node.Attributes -band [IO.FileAttributes]::ReparsePoint){throw 'Reparse fixture refused'}
$acl=Get-Acl $root
if($acl.GetOwner([Security.Principal.SecurityIdentifier]).Value -notin @('S-1-5-18','S-1-5-32-544')){throw 'Untrusted fixture owner'}
foreach($rule in $acl.GetAccessRules($true,$true,[Security.Principal.SecurityIdentifier])){if($rule.AccessControlType -eq 'Allow' -and $rule.IdentityReference.Value -notin @('S-1-5-18','S-1-5-32-544')){throw 'Fixture is not private'}}
$key=[Microsoft.Win32.Registry]::LocalMachine.OpenSubKey('SOFTWARE\Microsoft\Windows\CurrentVersion\Policies\System',$true)
try{
 $kind=$key.GetValueKind('EnableLUA').ToString();$value=[int]$key.GetValue('EnableLUA')
 if($kind -ne 'DWord' -or $value -notin @(0,1)){throw 'Unsupported baseline EnableLUA'}
 if($Mode -eq 'Apply'){
  if(Test-Path $statePath){throw 'Existing fixture requires inspection; do not repeat apply'}
  $record=[ordered]@{schema=1;installation_id=$InstallationId;value_name='EnableLUA';kind=$kind;original=$value;applied=1}
  $bytes=[Text.Encoding]::UTF8.GetBytes(($record|ConvertTo-Json -Compress))
  $file=[IO.FileStream]::new($statePath,[IO.FileMode]::CreateNew,[IO.FileAccess]::Write,[IO.FileShare]::None)
  try{$file.Write($bytes,0,$bytes.Length);$file.Flush($true)}finally{$file.Dispose()}
  if($key.GetValueKind('EnableLUA').ToString() -ne $kind -or [int]$key.GetValue('EnableLUA') -ne $value){throw 'Concurrent UAC edit'}
  $key.SetValue('EnableLUA',1,[Microsoft.Win32.RegistryValueKind]::DWord)
  if([int]$key.GetValue('EnableLUA') -ne 1){throw 'UAC readback failed'}
  '{"fixture_uac_enabled":true,"reboot_required":true,"production_setting":false}'
 }else{
  $record=Get-Content $statePath|ConvertFrom-Json
  if($record.schema -ne 1 -or $record.installation_id -ne $InstallationId -or $record.value_name -ne 'EnableLUA' -or $record.kind -ne 'DWord' -or $record.original -notin @(0,1) -or $record.applied -ne 1){throw 'Invalid original fixture value'}
  if($value -ne 1 -and $value -ne $record.original){throw 'Later UAC edit preserved'}
  $key.SetValue('EnableLUA',[int]$record.original,[Microsoft.Win32.RegistryValueKind]::DWord)
  if($key.GetValueKind('EnableLUA').ToString() -ne 'DWord' -or [int]$key.GetValue('EnableLUA') -ne $record.original){throw 'Restore readback failed'}
  '{"fixture_uac_restored":true,"reboot_required":true,"evidence_retained":true}'
 }
}finally{$key.Dispose()}
