# AzimuthRPC v0.9.10

Native DLL migration to FH2 Mod Platform 2.7.3 and guarded car-ID compatibility recovery.

- Replaces the old FH2ML ABI with `fh2mod_init`, platform registration, logging and game-base discovery.
- The DLL no longer rejects startup when the game image is not yet mapped.
- The standalone EXE and native DLL prefer the known `AudioEngine+0x278` car-ID field, and try a bounded, unambiguous candidate within the same object when it is unavailable.
- Existing Discord IPC, presence, speed, location and game-state handling are unchanged.
- Platform release package ships the official v2.7.3 loader and required license notices.
- Synthetic regression covers both relocated and ambiguous car IDs.

## Compatibility

Live gameplay testing on Azimuth 1.0.2 is **still required**. The fallback is a conservative compatibility heuristic, not a confirmed new offset.
