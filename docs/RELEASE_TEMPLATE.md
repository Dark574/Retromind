# Retromind {{VERSION}} (Linux AppImage)

Retromind is a media frontend for Linux focused on games, movies, books and more.  
It is designed to be portable, controller‑friendly and run well on Linux desktops.

---

## 1. Download

**Linux (x86_64)**

- `Retromind-{{VERSION}}-linux-x86_64.AppImage`

SHA‑256 checksum: `checksum`

---

## 2. Install / Run

Download the AppImage and make it executable:

```bash
chmod +x Retromind-{{VERSION}}-linux-x86_64.AppImage
```

### 2.1 Start BigMode directly

```bash
./Retromind-{{VERSION}}-linux-x86_64.AppImage -- --bigmode
```

### 2.2 Start the regular desktop UI

```bash
./Retromind-{{VERSION}}-linux-x86_64.AppImage
```

---

## 3. What’s new in {{VERSION}}

Replace this list for each release:

- [Change 1 – e.g. “Initial BigMode implementation with Arcade theme”]
- [Change 2 – e.g. “System host theme + per‑system layouts (C64, SNES, …)”]
- [Change 3 – e.g. “Node settings: per‑node artwork (logo / wallpaper / video)”]

---

## 4. Known issues / limitations

- **Linux only**  
  Windows is not supported at the moment.

- **Early version**  
  Expect rough edges, missing settings and occasional layout glitches.  
  Feedback and bug reports are very welcome.

---

## 5. System requirements

- Linux x86_64
- GLibC ≥ 2.36 (built on Debian 12 "bookworm")
- OpenGL/Vulkan‑capable GPU (recommended)
- .NET runtime is bundled in the AppImage (self‑contained), no separate installation required

---

## 6. Debugging / logs

To capture additional LibVLC and console output (useful for bug reports), you can run:

```bash
./Retromind-{{VERSION}}-linux-x86_64.AppImage -- --bigmode |& tee vlc-log.txt
```

When opening an issue, please include:

- A short description of what you did
- Your Linux distribution and version
- The AppImage version (`{{VERSION}}`)
- `vlc-log.txt` (if video/audio is involved)
- Optional: output of `dotnet --info` if you also run a non‑AppImage build

---

## 7. Links

- Project page: https://github.com/<your-account>/Retromind
- Issue tracker: https://github.com/<your-account>/Retromind/issues
- (Optional) Documentation: https://github.com/<your-account>/Retromind/wiki