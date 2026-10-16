# Contributing

Use Windows x64, Git, the .NET 8 SDK and the Windows Desktop targeting pack.
For the native DLL, install CMake and Visual Studio Build Tools with Desktop
development with C++ and a Windows SDK.

```powershell
./build.ps1 -SmokeTest
./native/FH2ML.AzimuthRPC/build.ps1
./scripts/audit-public-tree.ps1
./tests/public-history-tests.ps1
```

`publish.ps1` also builds/tests the managed projects and produces the standalone
EXE. Both runtimes must retain the same version; a release tag must match it.
Ordinary pull requests build artifacts and never publish Releases.

Keep changes focused. Preserve failure handling, supported-build checks and
Unknown states when evidence is insufficient. Add meaningful deterministic tests
for changed behavior. Synthetic tests do not prove new offsets or live behavior.
Document the evidence for any new layout or state interpretation.

Do not commit live memory dumps, copyrighted game assets, local configs, logs,
credentials or build output. Review diagnostics before sharing them. Do not paste
or port third-party implementations into this repository. Describe observed
behavior and independently validated layouts instead, and record provenance.

Read LICENSE before contributing. This is a restrictive source-available project,
not open source. Proposals to redistribute/reuse code require separate written
permission. Bug reports and research observations remain welcome. Obtain written
agreement from the maintainer before submitting code patches; do not assume a
public repository grants general modification or redistribution rights.
