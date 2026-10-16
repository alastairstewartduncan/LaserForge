# Changelog

All notable changes to A·S·D Lasercraft (formerly LaserForge). Versions follow [semantic versioning](https://semver.org).

## [0.4.0] – 2026-10-16

### Added – AI (optional, powered by Claude)
- **Describe a design** (AI menu, toolbar sparkle, Ctrl+G): type a description and Claude draws it as an SVG using laser-friendly rules. It's imported as editable vectors, with lettering outlined in your installed fonts (curved text supported), scaled to your size and placed on sensible layers. Live burn-style preview, "change it" refinements, and Engrave / Line art / Engrave + cut styles with ready-made ideas.
- **Read a test grid photo**: photograph a burned material test grid and Claude picks the best cell for your goal (darkest clean engrave, light engrave, photo, or cut-through), with confidence, reasoning, a runner-up and tips. Save the result as a material preset or apply it to the current layer. The app remembers the last grid you generated so the speeds and powers are exact.
- **Assistant tab** (Ctrl+K): a chat that sees your project, machine, material presets and recent console lines. It can add text and shapes, set layer settings, apply presets, add layers, move, resize, array, re-layer, select, delete and generate artwork – each change one undo step. A "Can edit" toggle switches it to advice only. It has no way to frame, start, stop or stream jobs.
- **AI settings**: your own Claude API key (stored in Windows Credential Manager), model choice, connection test, and privacy options. AI features can be hidden entirely.

### Added – smart image tools (run on your PC)
- **Remove background**: the U²-Net (u2netp) model via ONNX Runtime – a one-off 4.6 MB download, SHA-256 verified, then fully offline – or an instant plain-background mode.
- **Photo to line art** with detail, line strength and shading sliders and a live preview; the result burns with threshold dithering.
- **Auto-tune for material**: one click sets brightness, contrast, gamma, invert and dithering from the image's tonal range and the layer's material.
- **GRBL explanations**: every `ALARM:` and `error:` in the console is followed by its meaning and a practical fix. The full GRBL 1.1 error and alarm tables are included.

### Engineering
- New `AsdLasercraft.Core.Ai` namespace: a dependency-free Claude Messages API client (text, images, tool use, friendly errors), a scripted `FakeAiClient`, and the design generator, grid reader and assistant tools, all unit-tested without a network.
- SVG import can now return `<text>` runs with their font, size, anchor and transforms.
- 28 new core tests (105 total); the UI smoke test now opens the AI settings, design, test grid and line art windows and runs an assistant conversation against a fake client.

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
