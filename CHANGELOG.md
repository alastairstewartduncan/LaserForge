# Changelog

All notable changes to A·S·D Lasercraft (formerly LaserForge). Versions follow [semantic versioning](https://semver.org).

## [0.3.0] – 2026-10-14

### Added – burn quality
- **Scan offset compensation** for bidirectional fill and image engraving: a speed → offset table in Machine settings, interpolated between speeds, fixes fuzzy or doubled edges. **Tools → Scan offset test card** helps you measure it.
- **Fill angle and cross-hatch** on Fill layers: scan at any angle, or add a second pass at 90° for darker, more even engraving.
- **Kerf offset** on Line layers: part outlines grow and holes shrink by half the kerf, so pieces come out at the drawn size.
- **Tabs**: leave small uncut bridges in part outlines so pieces don't drop through the bed (never added to holes).

### Added – before you burn
- **Job origin and anchor**: start from absolute coordinates, your user origin (G54), or the laser head's current position, with a 9-point anchor picker. Frame and burn use the same placement, and the start point is marked on the canvas.
- **Simulated preview**: play the generated job on the canvas with a scrub bar, speed control (1–500×), travel moves, and images shown as their real dithered burn. Ctrl+P.
- **Pre-flight checks** in a new "Ready to burn" dialog: outside the bed, overscan past the edge, 0 % power, open paths on fill layers, hidden layers that still burn, cuts ordered before engraves, grayscale on constant power, and long jobs.

### Added – workflow
- **Material library** (Ctrl+M): starting presets for common materials, edit or add your own, apply to a layer in one click. Saved to `%AppData%\ASD Lasercraft\materials.json`.
- **Grid and circular arrays** for batches.
- **Trace image to vectors**: turn the dark parts of an image into cuttable, fillable outlines.
- **Built-in laser simulator**: choose "Simulator (no laser)" as the port to try framing and jobs with no hardware; the head moves on the canvas.
- Drag and drop images, SVGs and projects onto the canvas. Recent files. Opening `.asdl` files from Explorer.
- Settings (machine profile, port, view options) are remembered between sessions.
- Update check against GitHub releases, with a banner when a new version is out.

### Changed – look and feel
- New dark interface: graphite surfaces with a laser-orange accent, line icons throughout, a tool rail, card-style panels, rulers, zoom controls, status chips and toast notifications. The Windows title bar matches the theme.
- Animated **splash screen**: the laser head engraves the A·S·D LASERCRAFT title, throwing sparks, while the app loads.
- While a job runs, the head glows when firing and leaves a burn trail on the canvas; the Job panel shows elapsed and remaining time.
- Brighter default layer colours for the dark bed.

### Engineering
- Main window split into focused partial classes (file, edit, layers, laser, preview, tools).
- Golden-file G-code tests, a software GRBL controller (`GrblSimulator`) for streaming tests, and 48 new core tests (77 total).
- CI now launches the app in `--smoke-test` mode to open every window and render screenshots, builds an **installer** (Inno Setup), and a tag like `v0.3.0` publishes a GitHub release.
- Project files gain `FormatVersion` 2; older files open unchanged.

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
