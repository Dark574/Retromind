---
title: Retromind – Portable media frontend for Linux
description: Retromind is a Linux-first, portable media frontend for organizing and launching games, movies, books, comics and more.
---

Retromind is a **Linux-first**, portable media manager for organizing and launching your library of
games, movies, books, comics and more.

Built with **C#** and **Avalonia**, Retromind combines a desktop library interface with a
controller-friendly BigMode and is distributed as a portable **AppImage**.

---

## Status

Retromind is currently in early alpha. Features and data formats may change between releases, so feedback
and bug reports are especially welcome.

Retromind is primarily developed and tested on CachyOS. The AppImage is built on Debian 12 and requires
glibc 2.36 or newer. Other Linux distributions are expected to work, but have not all been tested yet.

---

## Why Retromind?

- **Portable media library** with relative paths, designed to move between Linux systems on an external drive
- **Desktop and controller-friendly BigMode interfaces** with video previews and customizable runtime themes
- **Flexible game launching** for native applications, scripts and emulators, including Wine, Proton, UMU, wrappers and environment overrides
- **Metadata and artwork scraping** from multiple providers with bulk processing and per-field import decisions
- **Library discovery and filtering** through global search, favorites, saved filters and an optional metadata query language
- **Smart local imports** with multi-disc recognition and optional playlist launching
- **Store integration** for Steam and Heroic imports plus experimental native GOG library, installation, update and uninstall support
- **Managed compatibility runners**, including direct GE-Proton downloads and reusable emulator profiles

Retromind is open source under the GPL-3.0-only license and developed in public on GitHub.

### Portability

The directory containing the AppImage acts as Retromind's portable data root. Library files, settings and
themes stay together, while launch paths inside that directory can be stored relatively. An optional
portable HOME/XDG mode is available for Retromind itself, with separate overrides for launched programs.

External games, emulators and launchers may still depend on host drivers, packages, accounts or services.
See the [README](https://github.com/Dark574/Retromind#configuration-portable) for the complete portability model.

---

## Screenshots

> Note: The screenshots are for demonstration purposes only.  
> All product names, logos, and brands shown are property of their respective owners.

### Desktop library

![Retromind desktop library](./images/retromind-library.jpg)

### BigMode (controller-friendly UI)

#### HorizontalRow theme with C64 media

![Retromind BigMode HorizontalRow theme with C64 media](./images/retromind-bigmode-c64-horizontal-row.jpg)

#### Amiga system theme

![Retromind BigMode Amiga system theme](./images/retromind-bigmode-amiga-system-theme.jpg)

#### Arcade theme

![Retromind BigMode Arcade theme](./images/retromind-bigmode-arcade.jpg)

---

## Download

The latest alpha builds are available on GitHub Releases:

- **Releases:**  
  [https://github.com/Dark574/Retromind/releases](https://github.com/Dark574/Retromind/releases)

Download `Retromind-<version>-linux-x86_64.AppImage`, make it executable and start it:

```bash
chmod +x Retromind-<version>-linux-x86_64.AppImage
./Retromind-<version>-linux-x86_64.AppImage
```

Start directly in BigMode:

```bash
./Retromind-<version>-linux-x86_64.AppImage --bigmode
```

The matching `.AppImage.zsync` asset is metadata for compatible delta-update tools and is not required for
a normal installation.

### AppImage requirements

- Linux x86_64 with glibc 2.36 or newer
- X11-compatible desktop session

The AppImage bundles .NET, LibVLC and its required helper tools. It does not require the host `libfuse2`
userspace library; normal mounting still needs Linux kernel FUSE support, while extract-and-run remains
available as a fallback.

---

## Build from source

Requirements:

- .NET SDK 10.0
- Linux with an X11-compatible desktop session
- VLC / LibVLC runtime

Basic usage:

```bash
dotnet restore
dotnet run --project Retromind.csproj
```

Start directly in BigMode:
```bash
dotnet run --project Retromind.csproj -- --bigmode
```

For AppImage build instructions, configuration details, tests and the complete feature documentation, see
the repository README:

[https://github.com/Dark574/Retromind](https://github.com/Dark574/Retromind)

---

## Contributing

Contributions are welcome!

Please read:

- [CONTRIBUTING.md](https://github.com/Dark574/Retromind/blob/main/CONTRIBUTING.md)
- [CODE_OF_CONDUCT.md](https://github.com/Dark574/Retromind/blob/main/CODE_OF_CONDUCT.md)

before opening issues or pull requests.

Bug reports and feature requests can be submitted through the
[issue tracker](https://github.com/Dark574/Retromind/issues).

---

## License

Retromind is licensed under **GPL-3.0-only**.  
See the full license text here:

- [COPYING](https://github.com/Dark574/Retromind/blob/main/COPYING)
