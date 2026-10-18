# FH2 offsets and validation

Only **FH2 Xbox One1.0.0.64**. Image RVAs are added to dynamically resolved GameBase. Heap offsets use validated roots. Captured absolute heap addresses are never runtime constants.

**CONFIRMED**: established for this build/source or repeated captured structure. **LIVE-VALIDATED**: user-reported live observation. **LIKELY**: supported interpretation with limited coverage. **EXPERIMENTAL**: retained heuristic. **DEPRECATED / WRONG**: do not use. Confidence is scoped to this build and evidence.

## Build and roots

| Name | RVA/value; root | Type / meaning | Status; discovery; confidence | Guards / notes |
|---|---|---|---|---|
| Preferred base | 0x140000000; PE | uint64 preferred image base | CONFIRMED; supplied identity/current source; high | Runtime may relocate |
| Timestamp / size | 0x565501ED /0x3A18000; PE | uint32 identity | CONFIRMED; supplied identity/current source; high | Also machine8664, optional magic20B, entrypointF18058, non-DLL |
| PlayerManager | 0x34ABA60; GameBase | pointer64 global | CONFIRMED; existing source with ExtraOptions attribution; high | Exact canonical aligned pointer, revalidate |
| AudioEngine | 0x365D3D8; GameBase | pointer64 global | CONFIRMED; existing source with ExtraOptions attribution; high | Appears AudioEngineFmod/game-parameter bridge, not merely audio-only |

Mapped headers must match file identity. Preferred base is tried first, then eligible image/private allocation bases. No stable heap address is assumed.

## Vehicle fields

| Name | Offset; root | Type / meaning | Status; discovery; confidence | Guards / notes |
|---|---|---|---|---|
| Speed | +0x90; AudioEngine | float32 MPH | CONFIRMED captured / LIKELY unit interpretation; motion controls/current source; high for field | Finite0..400; km/h=MPH*1.609344 |
| Position XYZ | +0xA0/A4/A8; AudioEngine | three float32 world coordinates | CONFIRMED captured / LIKELY world semantics; motion controls; high field/medium complete semantics | Finite, each abs<=100000; no region inference |
| Position mirror | +0x190/194/198; AudioEngine | three float32 | CONFIRMED duplicate; captured comparisons; high | Same bound; component differences<=1.5 used by fallback |
| Velocity | +0x170/174/178; AudioEngine | Vector3, likely m/s | CONFIRMED captured / LIKELY units; stopped/moving correlation; high field/medium units | Finite each abs<=500; length*3.6 finite<=1000km/h |
| Car raw ID | +0x278; AudioEngine | observed inline ASCII; possible MSVC small-string/heap representation | CONFIRMED short IDs only; **EXPERIMENTAL** heap fallback | Inline length5..63; for possible heap strings, validate 64-bit size/capacity/pointer at +0x288/+0x290/+0x278, read at most 128 bytes with double-read guards; not a state detector |
| UI registry | +0x630; AudioEngine | pointer64 | CONFIRMED; captured pointer traversal; high | Exact canonical aligned pointer; stable chain |

Friendly-name fallback is formatting, not an authoritative car database. CarIn and AudioObjectCar are separate findings. The MSVC heap-string decoding path was added to address reports of `invalid-character` car probes, but **no live dump has yet confirmed** that FH2 uses this object layout in the affected sessions. It fails closed on invalid metadata, changed bytes or unreadable pointers; validate against live car swaps before treating this as a production-proven fix.

## Mixer/UI registry

All records require two exact equal samples and stable root/registry pointers. Names are structural guards **only**. Headers/active values use exact float32 bits3F800000, not approximate comparison.

| Name | Offset; root | Type / meaning | Status; discovery; confidence | Activation / guards |
|---|---|---|---|---|
| Pause/Photo record | +0xC00 length28; registry | float header/active, int subtype/guard, name | LIVE-VALIDATED; human labels and prior user report; high | +C00 float1,+C0C int2,+C10 exact BASE_Paused NUL; +C04 float1,+C08 int1 Pause/int2 Photo |
| Map record | +0xE80 length32; registry | same header family | CONFIRMED captured; repeated corrected labels; high within controls | +E80 float1,+E8C int2,+E90 exact BASE_Map_Paused NUL; +E84 float1,+E88 int1; pause active/subtype both0 |
| Loading record | +0x7A0 length29; registry | mixer loading snapshot | LIKELY; retained source/structure; medium | +7A0 float1,+7AC int1,+7B0 exact BASE_Loading NUL,+7A4 float1; secondary signal, not guaranteed during race transitions |

Pause/photo require map active/subtype both0. Simultaneous pause/photo and map positives are ambiguous ->Unknown. All inactive signals ->Unknown, never Driving. Short reads, changed records or malformed guards ->Unknown.

## Frontend, flow and loading

| Name | Offset; root | Type / meaning | Status; discovery; confidence | Guards / use |
|---|---|---|---|---|
| Frontend marker | +0x38; AudioEngine | float32 zero vs-80 | EXPERIMENTAL; Hub/Garage vs20 non-frontend captures; medium | Two equal0x48 reads; +30/+34 int0,+3C/+40/+44 float-80; +38 accepts0 or-80. Zero is Hub family, not alone a mode |
| SimulationManager | +0x50; PlayerManager | pointer64 | CONFIRMED captured structure; high | Vtable GameBase+0x2C5FD70; two equal0x28 reads, stable pointers |
| Flow primary/secondary | +0x10/+0x20; SimulationManager | int32 sub-flow | LIKELY observations / EXPERIMENTAL classification; medium | +18 int1,+24 int5. Guarded frontend+2/0=>Hub. Frontend+primary3,secondary0..0x40=>Garage |
| GameManager | +0x48; PlayerManager | pointer64 | CONFIRMED captured structure; high | Vtable GameBase+0x2C5F8C8; two equal0x40 reads, stable pointers |
| Loading pair | +0x2D/+0x2E; GameManager | byte/byte | EXPERIMENTAL; one race-loading capture vs21 controls; medium | +28=0,+29..2C=1,+2F=0;0/0=>Loading,1/1=>not loading; other pairs invalid |

Garage secondary7,8,10,11,13 has been observed. Range0..64 is the retained implementation heuristic, not64 proven substates or a universal3/10 enum. Frontend Hub/Garage has priority over GameManager loading, then mixer loading, then pause/photo/map. Historical recent-Hub timers are not reintroduced.

## Rev and native telemetry

| Name | RVA/offset; root | Type / meaning | Status; discovery; confidence | Guards / notes |
|---|---|---|---|---|
| Feedback thunk | 0x10768B0; GameBase | native hook, R8->CarIn | LIVE-VALIDATED; user x64dbg; high | Exact stock bytes and supported PE; reproduce overwritten code, return thunk+12 |
| Normalized rev | +0x00; CarIn R8 at thunk | float32 | LIVE-VALIDATED; smooth .084/.10/.13/.18; high | Not physical RPM; sample range-.05..1.2 |
| gearRpm | +0x04; CarIn | float32 feedback parameter | LIKELY; existing source; medium | Range-2..2; no physical unit claim |
| Shift/backfire | +0x31/+0x32; CarIn | byte flags | LIKELY; existing source; medium | Debug feedback only |
| AudioObjectCar method | 0x1026370; GameBase | virtual slot3 | LIVE-VALIDATED research; user report; medium/high | Documentation only; no reusable pointer chain |
| Audio params read | instruction0x1026960; [r14+0xBC] | float32 GAME_EngineRPM sent to FMOD | LIVE-VALIDATED research; user report; medium/high | r14 is per-car params at that instruction. Stable chain UNKNOWN; never hardcode its address |

Our remote buffer is **not an FH2 struct**: rev float+0, rev sequence uint32+4, gear float+8, shift byte+C, backfire byte+D, context int32+10, context sequence uint32+14. Two24-byte reads, equal even sequences, up to3 attempts. Rev is active only if its sequence changed within450ms. Event context has callback lifetime, independent of rev freshness.

## Event/activity

| Name | RVA/offset; root | Type / meaning | Status; discovery; confidence | Guards / notes |
|---|---|---|---|---|
| Event enter slot | 0x2D1EA50; GameBase | pointer64 callback | CONFIRMED current source/historical PE check; ExtraOptions attribution; high | Wrap/chain current callback; do not patch stock function body |
| Stock target | 0x912860; GameBase | native function | CONFIRMED historical PE check/current source; high | Mods can replace slot; not assumed current pointer |
| Event context | +0x60; callback RCX object | int32 | LIVE-VALIDATED user:2 FreeRoam,1 Event/Race; high for observed values | Read after chained handler returns; unobserved/invalid context stays Unknown |

Current activity: Loading overrides context ->RaceLoading; Hub/Garage suppress activity. Otherwise2->FreeRoam,1 with fresh telemetry or Pause/Photo/Map->Event,1 otherwise->RaceLoading. No other enum values are invented.

## Deprecated / wrong

| Finding | Status / reason |
|---|---|
| PlayerManager+458/+460 player vector | DEPRECATED / WRONG; user correction; removed from runtime |
| Absolute captured AudioEngine as constant | WRONG; heaps change across process lifetimes |
| FH4/FM7 Data Out-like blob is FH2 memory layout | UNCONFIRMED; scanner removed; no stable chain/structure proof |
| ASCII BASE_Loading/BASE_Map/BASE_Paused implies active | WRONG; names persist when inactive |
| Unknown or zero signals imply Driving | WRONG; insufficient evidence |
| Flow3/10 universally Garage | WRONG; secondary changes across substates |
