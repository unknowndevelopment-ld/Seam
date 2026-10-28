# Clothing Studio

Open `Seam.exe` and choose **Clothing Studio**. **Map Merger** opens the existing map tool separately. Closing either workspace returns to the launcher; the Tools button opens the launcher without discarding your work. Closing the launcher checks open workspaces for unsaved changes.

## Clothing and preview

Add extracted native YDD/YDR clothing files or a clothing folder. Matching YTD textures, alternate models and YLD files are associated by filename. Choose the male/female ped, component or prop slot, and local drawable index. Builds reindex each component from zero. Save projects as `.seamclothes`; projects reference original sources and private working copies, so keep those files available.

First-run setup asks you to select your GTA V Legacy installation or continue without GTA. The choice is saved; change it later using **GTA installation**. A detected folder is offered only as a suggestion. With no installation selected, the 3D preview and tattoo editor are disabled, while clothing pack creation and 2D texture editing remain available.

With an installation selected, the preview loads actual male/female MP freemode models and textures, with orbit, pan and zoom. Tattoo editing unlocks only after the live ped has loaded successfully. No Rockstar models or textures are bundled with Seam. Preview uses a rest/A-pose, native UVs, diffuse textures and rigid prop bone attachments. It does not simulate GTA's complete shader, animation, cloth-physics or Enhanced rendering pipeline.

## Live texture editor

Select a clothing item and open Textures, or open a YTD directly. Select an entry to paint, erase, add image/text layers, change colours/opacity/size/rotation, or replace its image. The selected model updates as artwork changes. Edited dictionaries are saved as private working copies for project builds; Save YTD also offers an explicit destination. PNG and DDS export are available. Watch an external image to refresh the preview when your image editor saves it.

Textures must use power-of-two dimensions up to 4096 pixels. Saved YTDs receive a full mip chain and are decoded again before publishing. Unsupported/protected dictionaries are reported instead of decoded or silently replaced.

## Tattoos

Choose Tattoos and add a design. Import an image or draw on a transparent canvas, add editable text/image layers, select a body zone and gender, and adjust placement and scale. Save retains a flattened PNG and an editable artwork sidecar. The ped preview is a body-UV approximation; GTA's decoration projection is the final reference.

The editor stores rotation in degrees. Export bakes that rotation into the texture and expands the axis-aligned projection footprint, then writes native rotation zero. This preserves non-square design proportions without guessing the native rotation unit. Artwork is exported at no more than 1024 pixels; the original design is retained.

## Build

Choose **FiveM** or **Singleplayer**, then Build pack and select an output parent folder. The builder validates local IDs, source files and native formats, preserves earlier builds using new collection names, and leaves originals untouched.

FiveM builds include native clothing YMT/YDD/YTD files, apparel shop metadata, tattoo overlays and a manifest. Start the generated resource in your server; its build report lists the mappings. Singleplayer builds create a native `dlc.rpf`, nested archives, setup/content metadata and installation instructions. The app does not modify the game installation. Use your usual supported clothing/appearance or tattoo menu to select the additions.

Protected clothing cannot safely be renamed into a new addon collection, so the builder reports it and stops that build. Enhanced assets, custom animation playback, model sculpting/weight painting, shader parity, automatic heel/hair rigs and editor plugins are outside this implementation. This is an independent original tool; it does not contain the reference app's code or licensed libraries. Native readback and local preview were tested; a FiveM/Singleplayer gameplay session was not performed.
