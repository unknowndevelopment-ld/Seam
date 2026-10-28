# Desktop validation

The desktop release builds with zero warnings and zero errors. Its checks exercise real native resource serialization and readback, source preservation, YMAP hierarchy/light merging, YBN collision handling, resource manifests and escrow metadata, texture dictionaries, clothing/tattoo pack generation, portable EXE packaging and native window controls.

UI checks open the real Windows launcher, map workspace and clothing editor. They verify moving/resizing controls, separate resource export, progress coalescing, texture/tattoo artwork rendering and mode transitions. Run them from an interactive Windows desktop.

Installation checks exercise the real first-run dialog, invalid folders, the saved continue-without-GTA choice, disabled 3D and camera controls, blocked tattoo entry, and working 2D YTD editing. Native checks compare merged map/light repair bytes with previews enabled and disabled, export separate map resources, and build a clothing pack without a selected installation. Test settings use private files under `artifacts/`; they do not overwrite the user's saved installation choice.

Synthetic demo assets are generated during the tests. Additional private-map checks run only when `SEAM_SCHOOL_FIXTURE` names an existing local fixture directory. Actual GTA preview probes require a local game installation and are not part of the portable executable.

The checks do not establish compatibility with every custom resource or replace a FiveM/Singleplayer gameplay test. Protected formats remain undecoded, arbitrary mesh intersections are not automatically subtracted, and final tattoo projection must be reviewed in game.
