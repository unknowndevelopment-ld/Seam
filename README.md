# Seam

A portable Windows app for GTA V map merging, clothing packs, native texture editing and tattoo creation. A single launcher opens separate **Map Merger** and **Clothing Studio** workspaces.

This repository contains the Windows desktop app only.

## Download and run

Download **Seam.exe** from [Releases](https://github.com/unknowndevelopment-ld/Seam/releases/latest). The executable embeds its libraries, interface, icon and notices. Move or rename it without installing DLLs or configuration files.

Requires **Windows x64 and .NET Framework 4.8**. The actual male/female freemode ped preview requires your own **GTA V Legacy** installation. Game assets and encryption keys are not included.

Open `Seam.exe` to choose a tool, or use `Seam.exe --maps` / `Seam.exe --clothing` to open one directly. The window supports moving, resizing and standard minimize/maximize/close controls.

On first run, choose your **GTA V Legacy installation** or **Continue without GTA**. The choice is saved for your Windows account. Use **GTA installation** in the launcher or Clothing Studio to change it later; Map Merger also offers a shortcut while its preview is disabled. Detected folders are suggestions; the app does not select one without your choice.

Without a selected installation, map merging/fixing, clothing pack creation and 2D YTD editing remain available. All 3D previews and camera controls are disabled. Tattoo editing stays disabled until a valid installation is selected and its live freemode preview loads successfully.

## Map Merger

1. Add extracted map resource folders or stream assets.
2. Put the preferred source at the top and select **Auto merge**.
3. Inspect the source scene and processing report.
4. Select **Export resources**.

Each resource exports to its own `<original>_merged` folder. Readable YMAP/YBN data is combined where compatible. Original `.fxap` files, manifests, scripts, protected assets and relative paths remain with their resource. Input files remain unchanged.

The engine resolves verified duplicates and compatible authored map, collision and light data. It repairs verifiable placement/light/bounds faults and reports uncertain floating props. Overlap alone does not establish a faulty collision; arbitrary intersecting interiors and floor geometry still require review in game. Protected assets are preserved rather than decrypted. Extract RPF archives before importing.

## Clothing Studio

- Build native FiveM clothing collections or singleplayer DLC packs from named YDD/YDR drawables and matching YTD files.
- Preview the actual installed male/female MP freemode ped, clothing and props using native geometry and diffuse textures.
- Paint, erase, add editable text/image layers, replace textures, export PNG/DDS, and save native YTD dictionaries with mipmaps.
- Create tattoo artwork, choose body zones and placement, and export native tattoo packs.

See [Clothing Studio](docs/Clothing-Studio.md) for workflows and limitations. The preview uses a textured rest pose and approximate body UVs for tattoos. GTA shader parity, animation, cloth physics, sculpting, weight painting and general Enhanced-format support are outside this implementation. Check final packs and tattoo projection in game.

## Build and verify

Install a .NET SDK, the .NET Framework 4.8 Developer Pack, and Windows PowerShell. From the repository root:

```powershell
.\Build.cmd
.\Build.cmd -Check
```

The build produces `Seam.exe` and `dist/Seam.exe`. `-Check` runs native codec, merge/export, packaging and Windows UI checks. Use an interactive Windows desktop for the UI checks. Set `SEAM_SCHOOL_FIXTURE` to an optional local map fixture folder to run additional resource checks; the fixture is not distributed.

Tests generate their own demo assets. Actual-game preview checks are optional and require a local GTA V Legacy installation. See [validation](docs/VALIDATION.md).

## Dependencies and provenance

The app uses the supplied CodeWalker dev46 core library and SharpDX. Third-party binaries and their original notices are retained under `lib/` and embedded into the executable. See [third-party provenance](THIRD-PARTY.md). Those components retain their authors' terms; this repository does not relicense them.

The clothing builder and editor are independent implementations. No Durty Cloth Tool application code or proprietary UI dependencies are included.
