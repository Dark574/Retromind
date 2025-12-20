# Third-Party Notices

Retromind uses several third-party libraries and tools.  
Each of these components is licensed under its own terms, which are summarized below.

For full license texts, see the files in the `Licenses/` directory shipped with this project and its binary distributions (e.g. AppImage).

---

## Avalonia

**Component:** Avalonia UI  
**Website:** https://avaloniaui.net/  
**License:** MIT

Avalonia is a cross-platform .NET UI framework used for the Retromind desktop application.

See: `Licenses/Avalonia.MIT.txt`

---

## LibVLC / LibVLCSharp

**Components:**

- **LibVLCSharp**
    - Website: https://code.videolan.org/videolan/LibVLCSharp
    - License: LGPL-2.1-or-later

- **LibVLC (VLC engine)**
    - Website: https://www.videolan.org/vlc/
    - License: License: LGPL-2.1-or-later and other compatible licenses (including GPL-2.0-or-later for some modules)

Retromind uses LibVLCSharp as a .NET wrapper around LibVLC to enable video playback (e.g. preview videos, trailers).

See:

- `Licenses/LibVLCSharp.LGPL-2.1.txt`
- `Licenses/LibVLC.LGPL-2.1.txt` (and any additional license files bundled with the LibVLC distribution)

---

## CommunityToolkit.Mvvm

**Component:** CommunityToolkit.Mvvm (part of .NET Community Toolkit)  
**Repository:** https://github.com/CommunityToolkit/dotnet  
**License:** MIT

Used for MVVM helpers (ObservableObject, RelayCommand, IAsyncRelayCommand, etc.).

See: `Licenses/CommunityToolkit.Mvvm.MIT.txt`

---

## System.Text.Json and other .NET BCL components

Retromind relies on standard .NET runtime libraries provided by Microsoft (e.g. `System.*`).  
These are covered by the .NET Runtime and SDK licenses.

**Website:** https://dotnet.microsoft.com/  
**License:** see the official .NET license terms.

---

## Silk.NET.SDL

**Component:** Silk.NET.SDL  
**Repository:** https://github.com/dotnet/Silk.NET  
**License:** MIT

Used for SDL-based input handling (e.g., gamepad support in BigMode).

See: `Licenses/Silk.NET.MIT.txt`

---

## Other NuGet Packages

Retromind may use additional NuGet packages (e.g. helpers and small utilities), typically under permissive licenses such as MIT or BSD.

Examples (non-exhaustive, update as needed):

- **Package:** `Some.Package.Name` – License: MIT  
  See: `Licenses/Some.Package.Name.MIT.txt`

- **Package:** `Another.Package` – License: BSD-2-Clause  
  See: `Licenses/Another.Package.BSD-2-Clause.txt`

Please consult the `Licenses/` directory for the complete list of third-party license texts included with this distribution.

---

## Build Tooling (Not distributed as part of the app)

The following tools may be used during development or for packaging, but are **not** distributed as part of the Retromind binaries:

- Docker
- appimagetool
- .NET SDK

These tools are governed by their own licenses and terms, which apply only when you use them directly.

---

## Notes

- Retromind itself is licensed under **GPL-3.0-only** (see `COPYING`).
- The licenses of third-party components do **not** change; they continue to apply to their respective code.
- If you redistribute modified versions of Retromind, ensure that you keep or update this notice file and include the required third-party license texts as mandated by each license.