# Package catalog

One `<name>.yaml` per package; the installer binary sits next to it (git-ignored).

```yaml
name: intellij-idea-community
version: "2026.2"
file: ideaIC-2026.2.exe            # cached here, pushed to PCs over the LAN
sha256: "<hash>"
silent: "/S /CONFIG=idea-silent.config /D=C:\\Program Files\\JetBrains\\IDEA"
detect:
  path: "C:\\Program Files\\JetBrains\\IDEA\\bin\\idea64.exe"
run_as: system                     # system | user
reboot: never                      # never | if_required | always
```
