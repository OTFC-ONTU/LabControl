param([Parameter(Mandatory=$true)][string]$OriginalFixture, [switch]$Desktop)
$ErrorActionPreference = 'Stop'
# Test-only read-only inspection of the already running exact Setup process.
if ($OriginalFixture -notmatch '^C:\\ProgramData\\LabControl\.InteractiveSetup\.[a-f0-9]{32}$') { throw 'Unexpected fixture path' }
$request = Get-Content -LiteralPath (Join-Path $OriginalFixture 'request.json') -Raw | ConvertFrom-Json
$output = Join-Path $OriginalFixture 'native-dialog-msaa.json'
if (Test-Path -LiteralPath $output) { throw 'Preserve existing inspection evidence' }
if (-not $Desktop) {
    if (-not [Security.Principal.WindowsIdentity]::GetCurrent().IsSystem) { throw 'SYSTEM launcher required' }
    $scheduler = New-Object -ComObject Schedule.Service
    $scheduler.Connect()
    $folder = $scheduler.GetFolder('\')
    $task = $scheduler.NewTask(0)
    $task.Principal.UserId = $request.AdminSid
    $task.Principal.LogonType = 3
    $task.Principal.RunLevel = 1
    $task.Settings.ExecutionTimeLimit = 'PT2M'
    $task.Settings.DisallowStartIfOnBatteries = $false
    $task.Settings.StopIfGoingOnBatteries = $false
    $action = $task.Actions.Create(0)
    $action.Path = Join-Path $env:SystemRoot 'System32\WindowsPowerShell\v1.0\powershell.exe'
    if ($PSCommandPath.Contains('"')) { throw 'Invalid script path' }
    $action.Arguments = '-NoProfile -NonInteractive -ExecutionPolicy Bypass -File "' + $PSCommandPath + '" -Desktop -OriginalFixture "' + $OriginalFixture + '"'
    $name = 'LabControl UI inspection ' + [guid]::NewGuid().ToString('D')
    $registered = $folder.RegisterTaskDefinition($name, $task, 2, $request.AdminSid, $null, 3, $null)
    $xml = $registered.Xml
    try {
        $null = $registered.Run($null)
        $deadline = [DateTime]::UtcNow.AddSeconds(90)
        while (-not (Test-Path -LiteralPath $output) -and [DateTime]::UtcNow -lt $deadline) { Start-Sleep -Milliseconds 250 }
        if (-not (Test-Path -LiteralPath $output)) { throw 'No native inspection result' }
        Get-Content -LiteralPath $output -Raw
    } finally {
        $current = $folder.GetTask($name)
        if ($current.Xml -eq $xml -and $current.State -ne 4) { $folder.DeleteTask($name, 0) }
    }
    exit
}
if ([Security.Principal.WindowsIdentity]::GetCurrent().User.Value -ne $request.AdminSid) { throw 'Wrong interactive administrator' }
if ((Get-FileHash -LiteralPath $request.Executable -Algorithm SHA256).Hash -ne $request.Sha256) { throw 'Setup executable changed' }
$targets = @(Get-Process | Where-Object { try { $_.Path -eq $request.Executable -and $_.SessionId -eq [Diagnostics.Process]::GetCurrentProcess().SessionId } catch { $false } })
if ($targets.Count -ne 1) { throw 'Expected exactly one existing Setup process' }
Add-Type -TypeDefinition @'
using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Text;
public static class SetupUiInspect {
 public class Row { public string Class; public string Label; public string TextError; public int? Number; public long? Checked; public int? AccessibleState; public bool Visible; public bool Enabled; }
 [ComImport,Guid("618736E0-3C3D-11CF-810C-00AA00389B71"),InterfaceType(ComInterfaceType.InterfaceIsIDispatch)]
 public interface IAccessibleState {
  [DispId(-5007)] object this[[MarshalAs(UnmanagedType.Struct)] object child] { [return:MarshalAs(UnmanagedType.Struct)] get; }
 }
 [DllImport("oleacc.dll")] static extern int AccessibleObjectFromWindow(IntPtr window,uint objectId,ref Guid interfaceId,[MarshalAs(UnmanagedType.Interface)] out IAccessibleState accessible);
 delegate bool Callback(IntPtr w, IntPtr p);
 [DllImport("user32.dll")] static extern bool EnumWindows(Callback c,IntPtr p);
 [DllImport("user32.dll")] static extern bool EnumChildWindows(IntPtr w,Callback c,IntPtr p);
 [DllImport("user32.dll")] static extern uint GetWindowThreadProcessId(IntPtr w,out uint p);
 [DllImport("user32.dll",CharSet=CharSet.Unicode)] static extern int GetClassName(IntPtr w,StringBuilder s,int n);
 [DllImport("user32.dll",CharSet=CharSet.Unicode)] static extern int GetWindowText(IntPtr w,StringBuilder s,int n);
 [DllImport("user32.dll")] static extern bool IsWindowVisible(IntPtr w);
 [DllImport("user32.dll")] static extern bool IsWindowEnabled(IntPtr w);
 [DllImport("user32.dll",CharSet=CharSet.Unicode,SetLastError=true)] static extern IntPtr SendMessageTimeout(IntPtr w,uint m,IntPtr a,IntPtr b,uint f,uint t,out IntPtr r);
 public static List<Row> Inspect(uint pid) {
  var handles=new List<IntPtr>();
  EnumWindows((w,p)=>{uint x;GetWindowThreadProcessId(w,out x);if(x==pid){handles.Add(w);EnumChildWindows(w,(c,z)=>{handles.Add(c);return true;},IntPtr.Zero);}return true;},IntPtr.Zero);
  var rows=new List<Row>();
  foreach(var w in handles){
   var cls=new StringBuilder(256);GetClassName(w,cls,256);
   var native=new StringBuilder(512);GetWindowText(w,native,512);
   var row=new Row { Class=cls.ToString(),Visible=IsWindowVisible(w),Enabled=IsWindowEnabled(w) };
   var labels=new[]{"LabControl Setup","PC number","Create student account and enable automatic sign-in","Install / repair","Cancel"};
   string text=null; var buffer=Marshal.AllocHGlobal(1024);
   try { Marshal.WriteInt16(buffer,0);IntPtr result;if(SendMessageTimeout(w,13,new IntPtr(512),buffer,2,2000,out result)==IntPtr.Zero)row.TextError="WM_GETTEXT:"+Marshal.GetLastWin32Error();else text=Marshal.PtrToStringUni(buffer); } finally{Marshal.FreeHGlobal(buffer);}
   foreach(var label in labels)if(text==label||native.ToString()==label)row.Label=label;
   int number;if(int.TryParse(text,out number))row.Number=number;
   if(row.Label==labels[2]) { IntPtr state;if(SendMessageTimeout(w,0xF0,IntPtr.Zero,IntPtr.Zero,2,2000,out state)!=IntPtr.Zero)row.Checked=state.ToInt64();else row.TextError="BM_GETCHECK:"+Marshal.GetLastWin32Error(); }
   if(row.Label==labels[2]) {
    try { var iid=typeof(IAccessibleState).GUID;IAccessibleState accessible;Marshal.ThrowExceptionForHR(AccessibleObjectFromWindow(w,0xFFFFFFFC,ref iid,out accessible));try{row.AccessibleState=(int)accessible[0];}finally{Marshal.ReleaseComObject(accessible);} }
    catch(Exception error){row.TextError="MSAA:"+error.GetType().Name+":"+error.HResult.ToString("X8");}
   }
   rows.Add(row);
  }
  return rows;
 }
}
'@
$rows = [SetupUiInspect]::Inspect([uint32]$targets[0].Id)
$json = $rows | ConvertTo-Json -Depth 4
[IO.File]::WriteAllText($output, $json)
