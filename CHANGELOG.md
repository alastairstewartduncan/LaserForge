# Changelog

All notable changes to A·S·D Lasercraft (formerly LaserForge). Versions follow [semantic versioning](https://semver.org).

## [0.2.0] – 2026-10-08

### Changed
- Renamed from LaserForge to **A·S·D Lasercraft**, because other products already use the LaserForge name.
- New logo: the A·S·D initials being engraved by the laser head, used for the app icon, About box and README.
- Projects, namespaces and the executable are now `AsdLasercraft.*` / `AsdLasercraft.exe`.
- Project files now use the `.asdl` extension.

## [0.1.1] – 2026-10-08

### Added
- LaserForge logo: app icon, window icons and README wordmark (`assets/`).
- Help menu with **About LaserForge**, GitHub link and "Report an issue" (pre-filled with version details).
- Central version and author details in `Directory.Build.props`, shown in the About box and window title and stamped into G-code headers.
- CI builds are numbered, and the downloadable build is named after its build number.

## [0.1.0] – 2026-10-08

### Added
- First version: drawing tools, text, SVG and image import, layers (line / fill / image), dithering, framing, material test grid, GRBL streaming and G-code export.
