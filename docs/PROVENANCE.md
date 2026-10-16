# Component provenance and remaining review

This is an evidence inventory, not a list of inspiration projects or a legal
clearance. No source copyright/license notices were found in the retained tracked
source that this cleanup could establish as third-party notices. That absence is
not permission to license uncertain work.

| Component | Available evidence | Remaining action |
|---|---|---|
| Managed snapshot/UI/state code | Supplied development source, own labeled observations, strict synthetic regression tests | Maintainer must confirm authorship/rights for pre-existing implementation |
| External telemetry hook and opcode emitter | Local debug observations and historical byte regression baseline; event-slot lead explicitly attributed to ExtraOptions | Establish original hook authorship or commission an independent replacement using local behavioral specifications; do not relabel existing code as clean-room |
| Native AzimuthRpcMod.cpp | Added in this repository and mirrors the managed behavior; guarded Win32 reads and Discord IPC | Confirm authorship of initial implementation and any source used during development |
| Managed/native LocationResolver | Local anchors and approximated mainland polygons; native port follows this repository's managed implementation | Confirm ownership/origin of hand-authored polygon data; no map image is retained |
| Debug research scanners | Local research implementation using observed RVAs, object layouts and debugger findings | Confirm ownership of pre-existing scanner code |
| Synthetic fixtures/builder | New purpose-built model; only numeric discriminator inputs and historical expectations preserved | No raw heap memory or game assets retained; not new live evidence |
| Former custom icon | Original provenance unavailable | Removed; executable/tray use the platform/default application icon |
| Discord/.NET/Windows interfaces | Protocol and platform interfaces; no vendored SDK implementation or NuGet PackageReference found | Framework/runtime distribution remains governed by its own terms; a source license cannot replace them |

Historical root RVAs and event-context research were attributed to
RokkuDayo/ForzaHorizon2_ExtraOptions. A prior project note also reports a requested
credit from MrReLaX1/Forza-Horizon-2-Online for reused work/ideas/offsets, without
identifying an exact retained component. The precise scope remains unresolved.
Keep these specific provenance facts here until resolved; do not claim independent
discovery. No broad courtesy credits file is retained.

A local comparison against inspected snapshots of ExtraOptions, FH2-Dualsense,
FH2-LivingCockpit and Forza-Horizon-2-Online found no identical contiguous
50-token source windows after comments were removed. This rules out neither
translation, structural adaptation nor older versions. It is not sufficient to
certify independent authorship. No demonstrably copied implementation was
identified or cosmetically rewritten in this PR, and no independent hook rewrite
is claimed. All gameplay features remain; only the undocumented custom icon was
removed. The maintainable native source now builds directly rather than through
a CMake string-replacement patch.

On 2026-10-16 the maintainer chose the restrictive AzimuthRPC Use-Only License 1.0
and instructed this preparation to proceed without further provenance work. That
is a maintainer decision, not new evidence that resolves the historical questions
above. The license grants only rights controlled by its licensor and preserves
third-party licenses/notices. No copyright clearance or independent rewrite is
claimed. See PUBLICATION_AUDIT.md for the separate history/publication decision.
