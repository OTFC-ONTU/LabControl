# tools

- `docs-build.sh` — regenerates `docs/html/` from every `.md` in the repository.
  Run it after **any** documentation change; the HTML is generated and must never be
  hand-edited. Implementation: `DocsBuild/` (a .NET 10 console app using Markdig,
  deliberately outside `LabControl.sln` — it is a build tool, not part of the product).
- `publish-all.sh` — self-contained publishes: console (osx-arm64, win-x64, linux-x64),
  agent + session + setup (win-x64, win-arm64 for VM testing). *(created in M0)*
- `build-usb.sh <mount>` — assembles the USB payload (needs `lab.json` from the
  console). *(created in M4)*
