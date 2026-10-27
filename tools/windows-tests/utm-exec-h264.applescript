-- Usage: osascript utm-exec-h264.applescript EXECUTABLE [ARG ...]
-- The M7 portion-6 H.264 drill (D-91) runs on a copy of the collect clone (same shared-NAT NIC, so
-- never run both at once, owned installation with the student account); this wrapper is bound to
-- that copy and its single shared-NAT NIC; it refuses every other VM, a changed NIC and shared folders.
-- Guest arguments must never contain passwords or enrollment material.
on run argv
    if (count of argv) < 1 then error "A guest executable is required"
    set vmId to "16841C4D-9505-4498-8271-1E92E27C0DAA"
    set commandPath to item 1 of argv
    set commandArgs to {}
    if (count of argv) > 1 then set commandArgs to items 2 thru -1 of argv
    tell application "UTM"
        set testVM to virtual machine id vmId
        if name of testVM is not "M7 isolated H.264 acceptance 2026-10-27" then error "Only the M7 H.264 drill clone is allowed"
        set config to configuration of testVM
        set adapters to network interfaces of config
        if (count of adapters) is not 1 then error "Expected one test NIC"
        if mode of item 1 of adapters is not shared then error "Expected the shared-NAT test NIC"
        if address of item 1 of adapters is not "6E:8B:CE:5A:20:20" then error "Fixture NIC identity changed"
        if (directory share mode of config) is not none then error "Remove test-clone shared folders before guest execution"
        set guestProcess to execute testVM at commandPath with arguments commandArgs output capturing true
        repeat 1200 times
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
