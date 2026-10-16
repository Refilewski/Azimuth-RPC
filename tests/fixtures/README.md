# Synthetic regression fixtures

`synthetic/scenarios.json` models 22 regression scenarios using only scalar state
inputs. These records are synthetic test data, not FH2 game assets or live memory.
`tests/SyntheticFixtures.cs` constructs small zero-filled structures, invented heap
addresses, two example car identifiers, and deliberately chosen motion vectors.
Only the few record names required by the parser's layout guards are synthesized.
Names alone never establish an active state.

The cases preserve historical mode expectations, including five scenarios without
human semantic labels; those remain parser controls rather than live validation.
Numeric discriminator inputs were minimized locally from previous regression
evidence. Position, velocity, speed, heap pointers and surrounding bytes are newly
constructed. Passing these tests does not constitute new live-game evidence.

Keep live research dumps local and ignored. Do not commit process snapshots,
arbitrary surrounding memory, game binaries, images or other copyrighted assets.
