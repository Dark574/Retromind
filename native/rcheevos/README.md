# rcheevos rhash integration

Retromind vendors the game-identification (`rhash`) portion of
[`RetroAchievements/rcheevos`](https://github.com/RetroAchievements/rcheevos).

- Upstream version: `v12.4.0`
- Source archive:
  `https://github.com/RetroAchievements/rcheevos/archive/refs/tags/v12.4.0.tar.gz`
- Source archive SHA-256:
  `7fb1a43b8edfe727219d054ed868cc985bca54f331f9c2410f818dd3143df5d3`
- License: MIT (`upstream/LICENSE` and `../../Licenses/rcheevos.MIT.txt`)

The files below `upstream/` are unmodified copies of the public headers,
`src/rc_compat.*`, and `src/rhash/` files from that archive.
`retromind_rhash.c` is a small Retromind-owned stable ABI wrapper around the
upstream iterator API. `build.sh` builds `libretromind-rhash.so` for Linux.

When updating, replace the complete vendored subset from a reviewed release,
update both version strings and the archive checksum, then run the native hash
tests and build an AppImage.
