# GOG WPE runtime compatibility

Analysis date: 2026-11-11. Application baseline: Avalonia 12.1.6 and
Avalonia.Controls.WebView 12.1.1. The AppImage build baseline remains Debian Bookworm / glibc 2.36.

## Build environment and runtime discovery

`build/Dockerfile.appimage` does not install or export a WPE runtime. `build/AppRun` adds only the
bundled VLC directories to `LD_LIBRARY_PATH`; it does not add the whole AppImage `usr/lib` directory.
Consequently, the GOG WebView normally discovers WPE from the host, even though Retromind itself
was built on Bookworm. The self-contained .NET publish does not supply the native WPE libraries.

`MainWindowViewModel.HasLinuxWpeRuntime()` asks Avalonia for a WPE adapter with
`OffscreenRenderer` support. If unavailable, Retromind uses its system-browser callback flow.
WebKitGTK remains excluded because of the earlier GTK/AppImage integration and native-crash issues.

## Released WebView package and the tested host

The [12.1.1 source](https://github.com/AvaloniaUI/Avalonia.Controls.WebView/blob/43d26d90312b338d1d82bd12b215c16b5f3fa195/src/Avalonia.Controls.WebView.Core/Linux/WpeWebViewAdapter.cs)
uses the legacy libwpe/FDO embedding API. It requires all three libraries:

| Library | CachyOS host observed during this analysis |
| --- | --- |
| `libWPEWebKit-2.0.so.1` | Loads; installed `wpewebkit` version 2.54.2-1.1 |
| `libwpe-1.0.so.1` | Missing |
| `libWPEBackend-fdo-1.0.so.1` | Missing |

There is a second incompatibility: the installed WebKit library does not export
`webkit_web_view_backend_new`, `webkit_web_view_new`, `webkit_web_view_get_backend`, or
`webkit_web_view_backend_get_wpe_backend`. Installing only libwpe and FDO would therefore not
make this WebKit build compatible. In particular, the adapter needs `webkit_web_view_backend_new`
when initializing its legacy view backend.

The modern exports `webkit_web_view_get_wpe_view` and `wpe_display_headless_new` are present.
This agrees with the [upstream report for Arch builds with the legacy API disabled](https://github.com/AvaloniaUI/Avalonia.Controls.WebView/issues/88).
WPE 2.54 supports a [new WPEPlatform API](https://wpewebkit.org/blog/2026-10-06-wpe-platform.html);
the legacy API is optional at build time. The same library filename alone does not establish API compatibility.

Both released WebView versions 12.1.0 and 12.1.1 returned `IsInstalled=false`,
`SupportedScenarios=None`, and `WPE WebKit libraries are not installed.` on this host.
Thus the reported immediate browser fallback is consistent with runtime discovery failing before
Retromind constructs the embedded login window.

The package manager records the host WPE installation on 2026-10-27. A plausible explanation for
discovering the incompatibility only now is that earlier GOG sessions reused the stored refresh token;
deleting it forced the embedded-login path to run again. This timing explanation is an inference,
not a reconstructed record of previous login attempts.

## Upstream solution

[Avalonia WebView PR #96](https://github.com/AvaloniaUI/Avalonia.Controls.WebView/pull/96) was merged
on 2026-10-25, after [12.1.1 was released on 2026-10-12](https://github.com/AvaloniaUI/Avalonia.Controls.WebView/releases/tag/12.1.1).
It adds both legacy and modern WPE backends. As of this analysis, the latest published GitHub release
is still 12.1.1; merged source is not the code shipped in that package.

The inspected upstream revision is `5b1b80bb2bf7247bd347e8437a0a5c1e208917d5`.
Its [WpeRuntime](https://github.com/AvaloniaUI/Avalonia.Controls.WebView/blob/5b1b80bb2bf7247bd347e8437a0a5c1e208917d5/src/Avalonia.Controls.WebView.Core/Linux/WpeRuntime.cs)
checks for the modern exports and chooses WPEPlatform automatically on WPE 2.54 or later.
Its `Shm` mode still supports software buffers through a headless display, so the intended
Retromind integration can remain independent of the Avalonia X11/Wayland backend.

The host passes the modern backend's two-symbol availability rule. This is a loader/symbol result;
the newer adapter has not been built or run as an interactive GOG login during this analysis.

## Bookworm packaging implications

Bookworm remains the supported build baseline. Its normal WPE packages are from the 2.38 series;
the [1.1 runtime file list](https://packages.debian.org/bookworm/amd64/libwpewebkit-1.1-0/filelist)
contains `libWPEWebKit-1.1.so.0`, rather than Avalonia's required `libWPEWebKit-2.0.so.1`.
Simply adding the normal Bookworm WPE packages to the Dockerfile is insufficient.

Copying a newer distribution's runtime is not a drop-in solution either: for example, Debian Trixie's
[amd64 WPE 2.0 package](https://packages.debian.org/trixie/amd64/libwpewebkit-2.0-1)
requires glibc >= 2.38. Retromind's AppImage ELF checks enforce glibc <= 2.36.
Any bundled WPE solution would need a compatible build/backport and its full runtime, subprocess,
resource and dependency handling. It would also need validation against the historical library-mixing issues.

Decision: keep using official NuGet releases and wait for a released WebView package containing
WPEPlatform support. Retromind will not maintain a custom WebView build or fork for this issue.
Until then, keep the existing WPE-only selection, `Shm` rendering, temporary per-login data/cache
directories and system-browser fallback. The Bookworm build baseline remains unchanged.

Once an official package includes the upstream fix, update it and validate the embedded GOG login
on X11 and native Wayland. A separate bundled-runtime effort would be needed if embedded login
must also work on hosts without WPE installed; that is outside this decision's scope.

## Validation performed

- Inspected the installed package metadata, `readelf` dependencies and `nm -D` symbol exports.
- Used a temporary C# probe outside the repository to query Avalonia adapter availability and load native libraries.
- Compared the released 12.1.1 source with the merged WPEPlatform implementation and release metadata.
- Reviewed the AppImage Dockerfile, launcher and glibc compatibility checks.

No system packages were installed, no AppImage was built, and no login or stored credentials were used by
the probes. Interactive rendering, input, callback interception, cancellation and cleanup with the newer
adapter remain unverified, on both X11 and native Wayland.
