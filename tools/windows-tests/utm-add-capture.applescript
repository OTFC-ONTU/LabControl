-- Add a separate, local-only QMP monitor to the stopped disposable clone.
-- Arguments: VM_UUID EXISTING_PRIVATE_CAPTURE_DIRECTORY
-- The caller must create the canonical capture directory with mode 0700.
on run argv
    if (count of argv) is not 2 then error "VM UUID and private capture directory are required"
    set vmId to item 1 of argv
    if vmId is not "4C8EAE7F-40C9-462E-8C51-446F4FC6C4A7" then error "Only the recorded disposable clone is allowed"
    set captureDirectory to item 2 of argv
    if captureDirectory does not start with "/private/tmp/" then error "Use a private temporary capture directory"
    if captureDirectory contains "," then error "Invalid QEMU socket path"
    if (length of captureDirectory) > 75 then error "Unix socket path is too long"
    set directoryFile to POSIX file captureDirectory
    tell application "UTM"
        set testVM to virtual machine id vmId
        if name of testVM does not start with "M4 isolated" then error "Unexpected VM name"
        if status of testVM is not stopped then error "The disposable clone must be stopped"
        set cfg to configuration of testVM
        if directory share mode of cfg is not none then error "Clone sharing changed"
        set adapters to network interfaces of cfg
        if (count of adapters) is not 1 then error "Unexpected fixture NIC count"
        if mode of item 1 of adapters is not host then error "Only host-only networking is allowed"
        if address of item 1 of adapters is not "6E:8B:CE:5A:D6:C0" then error "Fixture NIC changed"
        set existingArguments to qemu additional arguments of cfg
        repeat with currentArgument in existingArguments
            if argument string of currentArgument contains "m4capture" then error "Capture monitor already configured"
        end repeat
        set updatedArguments to existingArguments & {{argument string:"-chardev"}, {argument string:"socket,id=m4capture,path=" & captureDirectory & "/capture.sock,server=on,wait=off", file urls:{directoryFile}}, {argument string:"-mon"}, {argument string:"chardev=m4capture,mode=control"}}
        update configuration testVM with {qemu additional arguments:updatedArguments}
        return "Secondary capture monitor configured for the recorded clone"
    end tell
end run
