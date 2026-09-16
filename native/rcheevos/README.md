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
upstream iterator API. `retromind_chd_reader.c` supplies the custom CD-reader
callbacks required for compressed CHD images. `build.sh` builds both rcheevos
and libchdr into one `libretromind-rhash.so` for Linux, so no system libchdr or
`chdman` installation is required at runtime.

The CHD reader uses the separately vendored libchdr source in `../libchdr/`:

- Upstream version: `v0.3.0`
- Source archive:
  `https://github.com/rtissera/libchdr/archive/refs/tags/v0.3.0.tar.gz`
- Source archive SHA-256:
  `313f6bf5537e2494daa3daa8a931a2536fc4f2b8312c07e3c3d5324d2052fa20`
- License: BSD-3-Clause (`../libchdr/LICENSE.txt` and
  `../../Licenses/libchdr.BSD-3-Clause.txt`)

The adapter's track and sector handling follows RetroArch's MIT-licensed CHD
integration. See `../../Licenses/RetroArch.MIT.txt`. libchdr's bundled decoder
dependencies and their license files are listed in `../../THIRD-PARTY-NOTICES.md`.

When updating, replace the complete vendored subset from a reviewed release,
update both version strings and the archive checksum, then run the native hash
tests, validate at least one known CHD hash, and build an AppImage.
