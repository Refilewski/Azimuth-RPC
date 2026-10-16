# Research record

This document describes the **current** research/validation state for the 0.9.x codebase. Important corrections from superseded experiments are retained in [STATE_FINDINGS](research/STATE_FINDINGS.md); internal task instructions and generated reports are excluded from the public tree.

## Current production surfaces

AzimuthRPC now has two supported implementations built from the same repository:

- the managed standalone Windows host (`AzimuthRPC.exe`), and
- the native FH2ML mod (`AzimuthRPC.dll`).

Both target Forza Horizon 2 Xbox One build 1.0.0.64 through Azimuth. Unknown game images fail closed instead of applying the known RVAs to an unvalidated layout.

The native DLL is no longer hypothetical: it validates the FH2 image, performs guarded in-process reads and publishes Discord Rich Presence directly over Discord IPC. It does not host .NET inside Azimuth.

## Evidence and regression fixtures

Minimal synthetic regression records are stored under:

```text
tests/fixtures/synthetic/
```

The JSON records contain scalar scenario inputs. `tests/SyntheticFixtures.cs` builds invented pointers and small zero-filled structures at test time. Live research captures stay local and ignored and must not be promoted as raw process snapshots.

The fixture corpus protects existing state interpretation and snapshot behavior. Synthetic controls are regression tests, not new observed game states. See [fixture policy](../tests/fixtures/README.md).

## Validated runtime data

The current production snapshot path uses the mapped FH2 image plus the validated AudioEngine root. Current runtime reads include:

- car identifier/name;
- speed;
- live XYZ position;
- mirrored position and velocity used for consistency/world-presence checks;
- Pause, Photo Mode, World Map, Horizon Hub, Garage and Loading state signals.

Current exact offsets and confidence notes live in [OFFSETS](OFFSETS.md). The code remains the final authority when documentation and experiments disagree.

The managed debug/research host also contains telemetry and event-context hook experiments. Those findings are useful for validation, but not every debug hook has been promoted into the native FH2ML DLL.

## Location research

Production location presence currently uses **live player coordinates plus known anchors/coarse polygons** for mainland macro locations. This is a practical fallback, not a discovered native FH2 road/region/landmark API.

Research has already checked several plausible native sources, including map/UI elements, hub/destination state, career location tracking, road-trip state, map data sources and trigger zones. The investigated candidates either represented UI/progression state or unrelated environmental zones rather than the player's physical road/region.

See [MAP-RESEARCH](research/MAP-RESEARCH.md) for the detailed native-location investigation. A converted navigation/map asset that exposes road segments, region polygons or landmark geometry would be preferable to maintaining hand-authored coarse polygons, but that remains unproven until the asset format is actually decoded and validated against live XYZ.

## Rejected or corrected assumptions

- Absolute heap addresses are session artifacts; production code must use validated image RVAs and guarded pointer chains.
- A previously assumed PlayerManager vector path was not a reliable player-position source; production XYZ comes from the validated AudioEngine fields.
- An FH4/FM7-style Forza Data Out structure has not been established as a reusable FH2 in-memory structure.
- Persistent mixer/UI strings are not sufficient evidence that a mode is active.
- `Unknown` UI state is not automatically equivalent to driving; world presence requires additional snapshot/telemetry consistency.
- Career/hub/road-trip location values are not treated as physical player location without live spatial correlation.

## Validation policy

Changes that touch snapshot interpretation, state detection, offsets or packaging should keep the committed regression suites green. `publish.ps1` runs the managed build, regression runners and offline smoke test before producing the standalone EXE. CI also builds and verifies the native FH2ML package.

Offline fixtures cannot prove every live lifecycle property. Changes involving new hooks, pointer lifetimes, Azimuth mapping changes, Discord reconnect behavior or newly discovered native location sources still require live validation before being promoted as production facts.

## Research workflow

For new findings:

1. capture the exact game build and live state;
2. prefer stable image-relative roots over session heap addresses;
3. compare at least one positive and one negative/control state;
4. record whether a signal is production-validated, debug-only, or speculative;
5. add/adjust a regression fixture when the finding materially affects production interpretation;
6. retain useful corrections in `docs/research/STATE_FINDINGS.md`; keep obsolete task instructions and generated reports out of the public tree.

The main open reverse-engineering target remains a **native physical road/region/landmark source** that can replace or refine the current coordinate polygon/anchor resolver.
