# Retained state and telemetry findings

This replaces superseded V6.x implementation instructions and generated reports.
It preserves research conclusions; the old notes remain in private development
history and the local research checkout. No live dump is required by public tests.

- PlayerManager+0x48 is CGameManager (vtable RVA 0x2C5F8C8), not a player vector.
  PlayerManager+0x50 is ForzaSimulationManager (vtable RVA 0x2C5FD70), not a
  standalone Horizon Hub object.
- A 3/3 or fixed 3/10 flow pair alone is not a universal Hub/Garage detector.
  Garage secondary values vary. Current classification combines guarded frontend
  and simulation records; older fingerprints failed live testing and stay retired.
- The guarded GameManager byte pair +0x2D/+0x2E distinguishes the observed race
  loading transition (0/0) from non-loading controls (1/1). Coverage is limited;
  do not call it a universal loading enum.
- Registry strings persist when inactive. Pause, Photo and Map require numeric
  activation, structural guards, repeated equal reads and stable root pointers.
- The feedback thunk receives CarIn in R8. User debugger observations associate
  CarIn+0 with normalized rev, not physical RPM. No stable AudioObjectCar pointer
  chain has been established.
- Local static inspection found event slot RVA 0x2D1EA50 pointing to function RVA
  0x912860 in the supported image. That handler consumes context+0x60. User
  observations associate values 2 with Free Roam and 1 with Event. The original
  research lead was attributed to ExtraOptions; this is not independent discovery.
- Context callback lifetime and fresh rev samples differ. Loading overrides
  activity; inactive mixer records and zero vectors do not prove gameplay.
- Map-vs-driving labels were corrected explicitly by the user. Five historical
  unlabeled controls remain without semantic ground truth.

See [OFFSETS](../OFFSETS.md) for exact layouts/confidence and
[MAP-RESEARCH](MAP-RESEARCH.md) for unresolved native road/region/landmark research.
See [publication audit](../PUBLICATION_AUDIT.md) for remaining provenance limits.
