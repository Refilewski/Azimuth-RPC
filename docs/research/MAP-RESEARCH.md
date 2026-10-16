# Map/location research

This branch adds read-only map captures to the Debug host for FH2 Xbox One 1.0.0.64.

## Capture

1. Start AzimuthRPC.Debug and FH2.
2. Enter Free Roam and open the World Map.
3. Wait a moment for map elements to exist.
4. Press **F8** once.
5. Wait until the Debug window reports both output paths.
6. Look in the Debug executable's `dumps` directory.

F8 writes:

- `fh2-live-dump-*.json` — the bounded state/memory graph.
- `fh2-map-elements-*.json` — the map research capture.
- `fh2-native-location-*.json` — the experimental read-only `Career::LocationTrackerScripted` probe.

For repeated native-location experiments use **F9** instead of F8. F9 writes only the native-location JSON, so moving between several cities does not create a full graph/map dump every time.

## Native location experiment

The current production location resolver still uses coarse map polygons plus confirmed car-meet anchors. Native-location research must stay separate from that resolver until repeated live captures prove a stable game-owned field.

Current research lead:

- `Career::LocationTrackerScripted` vtable RVA `0x02C6D5C0`.
- Candidate current-location getter slot `+0x38`.
- Two observed helpers at `0x3A91A0` and `0x3A9290` follow `this+0x20/+0x08 -> +0xD0` before querying the same key.
- The scanner records object fields, guided pointer graphs, strings and small integers, with special attention to possible Road Trip location IDs `3..9`.

Recommended live validation in one FH2/Azimuth process lifetime:

1. Park in a location you can identify independently (for example directly at the Nice, Sisteron or Montellino car meet).
2. Press **F9** twice a few seconds apart without moving.
3. Drive to a second known location and press F9 twice again.
4. Repeat for a third location if practical.
5. Do not restart Azimuth/FH2 between the first comparison set; candidate heap addresses are expected to stay stable within one process lifetime.
6. After a promising field is found, repeat the experiment after a full process restart to prove the pointer chain can be rediscovered and is not a session artifact.

Compare the captures with:

```powershell
python tools/research/compare_native_location.py dumps/fh2-native-location-*.json
```

For real validation, provide human ground-truth labels instead of trusting the existing polygon result:

```powershell
python tools/research/compare_native_location.py `
  dumps/nice-a.json dumps/nice-b.json dumps/sisteron-a.json dumps/sisteron-b.json `
  --label nice-a.json=Nice `
  --label nice-b.json=Nice `
  --label sisteron-a.json=Sisteron `
  --label sisteron-b.json=Sisteron
```

The comparator ranks changing integer fields by coverage, small-enum behavior, repeated same-label stability and separation between different human labels. A field is only a lead until it survives repeated captures, menus/movement and a process restart.

## Confirmed MapSetTransform layout

The live FH2 1.0.0.64 code sample at `GameBase + 0x1942C70` shows that transform data is not stored inline at `MapElementGeneric+0x28/+0x38`.

`MapSetTransform(element, position, direction, scalar)` builds two vec4 values: `position` and `position + direction`, then stores them in a heap-backed array.

For initialized elements:

- `element + 0x40` -> 0x20-byte buffer: vec4 position, then vec4 position+direction.
- `element + 0x48` -> 0x08-byte buffer containing two scalar values.
- `element + 0x50` -> value `2`.

The scanner dereferences these fields directly and writes a `Transform` object containing:

- `Position`,
- `PositionPlusDirection`,
- reconstructed `Direction`,
- `Scalars`,
- `DistanceFromPlayer`.

## Live mainland anchors

A live 2026-10-15 world-map capture correlated `car_meet` map tags / localized labels with their real transforms. These are game-owned map anchors, not coordinates estimated from a screenshot:

| Display name | X | Y | Z | Evidence |
| --- | ---: | ---: | ---: | --- |
| Montellino | 10508.40 | 185.559 | -3260.87 | `car_meet`, `ZLOT SAMOCHODOWY W MONTELLINO` |
| San Giovanni | 5618.33 | 82.0175 | -2379.07 | `car_meet`, localized `SAN GIOVANNI`; internal asset `carmeet_sangimignano` |
| Saint-Martin | 1017.30 | 135.889 | -1799.88 | `car_meet`, `ZLOT SAMOCHODOWY W SAINT-MARTIN` |
| Sisteron | -4147.82 | 106.929 | -2039.93 | `car_meet`; adjacent/internal marker data identifies `carmeet_sisteron` |
| Nice Massena | 433.464 | 33.9058 | -4682.50 | `car_meet`, localized Nice Massena label / `car_meet_nice_30123` |
| Castelletto | 7840.33 | 176.352 | -6845.91 | `car_meet`, `ZLOT SAMOCHODOWY W CASTELLETTO` / Italian south-coast marker |

This also corrects an earlier naming assumption: the user-facing map label is **San Giovanni** even though some internal assets use `sangimignano` / `san_gimignano`.

`Fh2MainlandLocationResolver` now uses these six live anchors conservatively. It is intentionally not claiming that the car-meet coordinates are exact city boundaries. It returns `null` when the nearest anchor is too far away or when the two nearest anchors are too close, so Discord falls back to `Exploring Southern Europe` instead of showing a confidently wrong city.

The exact FH2 position from this capture (`X=10347.093`, `Z=-5920.2954`) is almost equally distant from the Montellino and Castelletto anchors, which is a useful real example of why an ambiguity guard is required.

## Other recorded research data

For every matching `MapElementGeneric` object the capture also records:

- object address,
- `+0x08` entity pointer,
- `+0x20` map-count field,
- complete raw 0x58 bytes,
- printable strings reachable from pointer-sized fields,
- printable strings near the entity,
- occurrences of integers `3..9` as Road Trip location-ID candidates,
- read-only code samples for `MapSetTag`, `MapGetTag`, `MapSetTransform`, and `MapAdd`.

The earlier interpretation of inline candidate vectors at `+0x28/+0x38` was incorrect and should not be used as map coordinates.
