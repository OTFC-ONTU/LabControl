#!/usr/bin/env python3
"""Read-only framebuffer evidence from an explicitly identified disposable VM.

On the stopped clone only, add a SECONDARY QMP monitor:
  -chardev socket,id=m4capture,path=<private-dir>/capture.sock,server=on,wait=off
  -mon chardev=m4capture,mode=control
Keep UTM's own monitor unchanged. The existing parent directory must be owned by
this host user and mode 0700. Capture blank credential fields only; never capture
an entered password or other secret. This command sends no input to the guest.
"""
import argparse
import json
import os
from pathlib import Path
import socket
import stat
import uuid


def capture(socket_path, expected_uuid):
    expected_uuid = str(uuid.UUID(expected_uuid))
    path = Path(socket_path)
    if not path.is_absolute() or path.parent != path.parent.resolve():
        raise ValueError("Use an absolute socket path without symbolic-link parents")
    parent = path.parent.stat()
    if not stat.S_ISDIR(parent.st_mode) or parent.st_uid != os.getuid() or parent.st_mode & 0o077:
        raise PermissionError("Capture directory must be private to the current host user")
    node = path.lstat()
    if not stat.S_ISSOCK(node.st_mode) or node.st_uid != os.getuid():
        raise PermissionError("Expected a current-user Unix socket")
    image = path.parent / ("m4-ui-" + uuid.uuid4().hex + ".ppm")
    with socket.socket(socket.AF_UNIX) as client:
        client.settimeout(10)
        client.connect(str(path))
        with client.makefile("rwb") as stream:
            def read():
                line = stream.readline(1024 * 1024 + 1)
                if not line or len(line) > 1024 * 1024:
                    raise IOError("Invalid QMP response length")
                return json.loads(line)

            if "QMP" not in read():
                raise IOError("QMP greeting missing")

            def command(name, arguments=None):
                request_id = uuid.uuid4().hex
                request = {"execute": name, "id": request_id}
                if arguments is not None:
                    request["arguments"] = arguments
                stream.write(json.dumps(request).encode("utf-8") + b"\r\n")
                stream.flush()
                for _ in range(128):
                    response = read()
                    if response.get("id") != request_id:
                        continue  # Unrelated asynchronous event; never print its payload.
                    if "error" in response or "return" not in response:
                        raise IOError("QMP command failed: " + name)
                    return response["return"]
                raise IOError("QMP response limit exceeded")

            command("qmp_capabilities")
            identity = command("query-uuid")
            if str(uuid.UUID(identity["UUID"])) != expected_uuid:
                raise PermissionError("QMP VM identity differs from the explicit disposable clone")
            command("screendump", {"filename": str(image)})
    metadata = image.lstat()
    if not stat.S_ISREG(metadata.st_mode) or metadata.st_uid != os.getuid() or metadata.st_size < 10:
        raise IOError("Invalid framebuffer output")
    image.chmod(0o600)
    with image.open("rb") as source:
        if source.read(3) != b"P6\n":
            raise IOError("Expected a PPM framebuffer")
    return image


if __name__ == "__main__":
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--socket", required=True)
    parser.add_argument("--disposable-vm-uuid", required=True)
    options = parser.parse_args()
    try:
        result = capture(options.socket, options.disposable_vm_uuid)
        print(json.dumps({"ok": True, "image": str(result)}))
    except Exception as error:
        # No raw QMP payloads, titles, field text, or personal identities.
        print(json.dumps({"ok": False, "error_type": type(error).__name__}))
        raise SystemExit(1)
