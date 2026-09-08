-- Usage: osascript utm-exec-host-test.applescript VM_UUID LAB_UUID EXECUTABLE [ARG ...]
-- This particular disposable fixture was archived before its host-only NIC was attached.
-- Guest arguments must never contain passwords or enrollment material.
-- This wrapper accepts only explicitly isolated M4 clones, never the classroom VM.
on run argv
    if (count of argv) < 3 then error "VM UUID, disposable lab UUID and executable are required"
    set vmId to item 1 of argv
    if vmId is not "4C8EAE7F-40C9-462E-8C51-446F4FC6C4A7" then error "This wrapper is bound to the archived M4 fixture"
    set labId to item 2 of argv
    set commandPath to item 3 of argv
    set commandArgs to {}
    if (count of argv) > 3 then set commandArgs to items 4 thru -1 of argv
    tell application "UTM"
        set testVM to virtual machine id vmId
        if name of testVM does not start with "M4 isolated" then error "Only an M4 isolated clone is allowed"
        set config to configuration of testVM
        set adapters to network interfaces of config
        if (count of adapters) is not 1 then error "Expected one host-only test NIC"
        if mode of item 1 of adapters is not host then error "Only host-only networking is allowed"
        if address of item 1 of adapters is not "6E:8B:CE:5A:D6:C0" then error "Fixture NIC identity changed"
        set marker to open file testVM at "D:\\fixture-lab-id.txt"
        set recordedLab to read marker for length 128 closing true
        repeat while recordedLab ends with linefeed or recordedLab ends with return
            set recordedLab to text 1 thru -2 of recordedLab
        end repeat
        if recordedLab is not labId then error "Mounted payload belongs to a different disposable lab"
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
