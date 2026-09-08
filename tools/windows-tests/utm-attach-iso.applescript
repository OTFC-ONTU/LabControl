-- Run only after gracefully stopping the isolated clone.
-- Arguments: VM_UUID REMOVABLE_DRIVE_UUID CODE_ONLY_ISO_ABSOLUTE_PATH
on run argv
    if (count of argv) is not 3 then error "VM UUID, removable drive UUID and code-only ISO are required"
    set vmId to item 1 of argv
    set driveId to item 2 of argv
    set isoFile to POSIX file (item 3 of argv)
    tell application "UTM"
        set testVM to virtual machine id vmId
        if name of testVM does not start with "M4 isolated" then error "Only an M4 isolated clone is allowed"
        if status of testVM is not stopped then error "The clone must be stopped"
        set cfg to configuration of testVM
        if (directory share mode of cfg) is not none then error "Clone sharing changed"
        if (network interfaces of cfg) is not {} then
            if vmId is not "4C8EAE7F-40C9-462E-8C51-446F4FC6C4A7" then error "Unknown networked fixture"
            set adapters to network interfaces of cfg
            if (count of adapters) is not 1 then error "Unexpected test NIC count"
            if mode of item 1 of adapters is not host then error "Only host-only fixture networking is allowed"
            if address of item 1 of adapters is not "6E:8B:CE:5A:D6:C0" then error "Fixture NIC changed"
        end if
        set updatedDisks to {}
        set found to false
        repeat with disk in drives of cfg
            if id of disk is driveId then
                if removable of disk is not true then error "Refusing to modify a fixed disk"
                set end of updatedDisks to {id:driveId, source:isoFile}
                set found to true
            else
                set end of updatedDisks to contents of disk
            end if
        end repeat
        if not found then error "The recorded removable drive is missing"
        update configuration testVM with {drives:updatedDisks}
        return configuration of testVM
    end tell
end run
