-- Usage: osascript utm-exec.applescript VM_UUID EXECUTABLE [ARG ...]
-- Guest arguments must never contain passwords or enrollment material.
-- This wrapper accepts only explicitly isolated M4 clones, never the classroom VM.
on run argv
    if (count of argv) < 2 then error "VM UUID and guest executable are required"
    set vmId to item 1 of argv
    set commandPath to item 2 of argv
    set commandArgs to {}
    if (count of argv) > 2 then set commandArgs to items 3 thru -1 of argv
    tell application "UTM"
        set testVM to virtual machine id vmId
        if name of testVM does not start with "M4 isolated" then error "Only an M4 isolated clone is allowed"
        set config to configuration of testVM
        if (network interfaces of config) is not {} then error "Remove test-clone networking before guest execution"
        if (directory share mode of config) is not none then error "Remove test-clone shared folders before guest execution"
        set guestProcess to execute testVM at commandPath with arguments commandArgs output capturing true
        repeat 600 times
            set outcome to get result guestProcess
            if exited of outcome then
                set code to exit code of outcome
                set textOutput to output text of outcome
                set textError to error text of outcome
                if code is not 0 then return "GUEST_EXIT=" & code & linefeed & textOutput & linefeed & textError
                return textOutput & textError
            end if
            delay 0.1
        end repeat
        error "Guest execution has not finished; do not repeat a mutation blindly"
    end tell
end run
