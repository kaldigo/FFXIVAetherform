# Aetherform

A Dalamud plugin for installing the Aetherform body and building physics presets in Penumbra. Open **/aetherform**. The installer and preset builder use the AetherglassUI window shell and controls.

## Install through Dalamud

In Dalamud custom plugin repositories, add https://raw.githubusercontent.com/kaldigo/DalamudPlugins/main/pluginmaster.json. Open /xlplugins, search for Aetherform and install it. Select a collection and choose **Install body**. The plugin downloads Aetherform.pmp from the body release, verifies its SHA-256, and imports it into Penumbra. The download is cached in %APPDATA%/XIVLauncher/pluginConfigs/Aetherform/payload. The original physics controls work from the plugin alone.

The physics configurator includes Tiramisu2 motion sources by Amatiramisu. Choose a chest style and variation, then select height, collision, butt, belly, thighs, genitals, ears and tail independently. All choices are available immediately after installing the plugin.

## Development bundle

Extract the entire plugin ZIP to a dedicated folder. In Dalamud settings, open **Experimental**, add the extracted folder to **Dev Plugin Locations**, then load **Aetherform** under **Dev Tools → Installed Dev Plugins**. Load Penumbra.

1. Open **/aetherform** and select your test collection on **Body installer**. Choose **Install body** and complete any import prompt in Penumbra. Aetherform enables the imported body in the collection you picked. Importing an update can replace an earlier copy of the body shared by other collections.
2. Pick your chest and leg shape and other appearance options in the body mod's Penumbra settings. Assign your character to the test collection in Penumbra to preview it.
3. Open **Physics preset builder**. Set chest motion, height, collision, butt, belly, thighs, genitals, Miqo’te ears and tail independently. Choose **Apply to collection**, then redraw your character in Penumbra. Named presets can be loaded into another collection and applied there.
4. Switching your character back to its original collection restores that collection's appearance. Each applied preset is restored while the body is enabled and the plugin is loaded. Unloading the plugin removes its temporary physics redirects.

The body ships with its own default physics. Custom presets resolve to one complete physics file per supported race at one priority above the body. The plugin assembles the selected simulators at runtime from 31 embedded sources. The catalog comes from the Aetherform physics bank with its reduced ear bounce and V1 tail. The pinned Proteus solver libraries are in vendor/proteus. Body assets are staged separately.

## Automatic refits on equip

When Aetherform supplies both the selected chest and legs in your character’s effective collection, the plugin fits vanilla tops, bottoms, gloves and shoes as you wear them. The switch on **Body installer** is per collection and starts enabled. The target models come from the **Smallclothes** options, including the leg modifiers. Tops and bottoms bind to the embedded vanilla-to-AF map. **Cloth Detail 2** splits each triangle on a supported flexible equipment panel into four, interpolates its UVs and skinning weights, smooths the refit displacement and checks vertices and triangle interiors against the drawn skin in the rest pose. All supported panels in a garment share a bounded displacement field. The solver keeps vanilla waist attachments at the compatible body join, and distinguishes authored interior geometry from exterior cloth during clearance. The pass preserves original skin triangles and rigid single-bone islands. Meshes with active shape-replacement tables, unsupported vertex layouts, or a vertex count beyond the MDL limit retain their existing fit. Earlier editable fits remain available. Gloves and shoes keep their hand and foot shapes.

Generated models appear under **Aetherform → Refits → Vanilla to Aetherform** in Penumbra. Each garment has its own mod with a Proteus Fit group, a separate option per target preset, and a Proteus/bodyretarget.json record. Choose the garment in Proteus to edit its fitted model. Previous outfit-cache files are reused on equip. The generated mod is enabled temporarily for the active collection at the lowest signed priority. Handmade model replacements win. Save upstream Proteus corrections as separate mods above the handmade fits. The local cache is separated by source and target under **refits/Vanilla/Aetherform**. Generated files are cached for reuse with the same shape and game version. The plugin watches the local player and leaves unsupported races, model layouts, and unrelated model replacements alone.

Skirt-driven regions automatically reconcile their body influences against the source body and the final AF skin. The skirt's own animation weights and sewn boundaries stay fixed. A sampled local clearance pass accounts for additional relative thigh movement introduced by the conversion, preserving shape offsets and preventing reversed triangles. This uses a bounded thigh-motion model rather than playing back a particular idle. Both vanilla and family conversions use it, with separate cache revisions so earlier fits regenerate. Generation runs in the background and completed fits are cached.

Standalone exports with fewer model variants run first. Each complete mod activates before the next export starts, so completed fits do not wait for unrelated bulk conversions. Size options with the same compatible candidates reuse their source selection and completed geometry; glove and shoe protection remains specific to each requested model.

YAB and Rue gear prefers verified direct topology correspondence to the selected AF body, then chooses the closest compatible size within that route. UV correspondence remains a fallback when no compatible direct candidate exists. Family conversions run before vanilla refits. Clothing is adaptively refined to the target body density in the source pose before fitting, so newly added vertices follow the body deformation. Authored source vertices, sewn skin boundaries, shape controls, UVs and visibility groups are preserved through refinement; hand and foot vertices remain protected. Compatible adjacent bodies can be discovered through shared topology. Gloves and shoes retain their authored hand or foot while the arm or leg portion is refitted. A standalone copy preserves the source mod's tabs and page names, option groups and ordering, combining flags, metadata edits, textures, materials and preview images. Every supported clothing model variant is converted, including inactive options, so changing styles or accessories does not require the original mod to remain enabled. The AF copy receives the current option selections and source priority. Only after the complete copy is registered and enabled does Aetherform disable the source in the current collection. Conversion failure leaves the source enabled; an activation failure restores the prior enabled mods. Other collections and the original files are unchanged. Completed exports retain user edits, and a changed AF shape or source revision generates a separate copy. Family fits appear under **Aetherform → Refits → YAB and Rue to Aetherform**, separately from **Vanilla to Aetherform**. Existing generated family fits are moved into this folder when discovered. Their cache is stored separately under `refits/YAB-Rue/Aetherform`. Model file swaps that cannot be converted are declined before switching mods.

## Build

Requires Windows, .NET 10, Python 3, and the Dalamud API 15 development assemblies. Clone [AetherglassUI](https://github.com/kaldigo/FFXIV-AetherglassUI) into a sibling directory named AetherglassUI and check out revision f3960c317f9c35e7ddba82310aad2e7e240b92d1.

From this repository, run:

    dotnet build src/Aetherform.csproj -c Release
    python tools/prepare.py "path/to/Aetherform v0.1.6.pmp" "path/to/Aetherform Physics v0.2.0.zip" payload
    python tools/package.py out/Aetherform.zip

The last two steps require local copies of the indicated Aetherform inputs, including the derived YAB+, Tre and Tiramisu assets. Packaging checks the body file references, the packaged defaults, and every area selection across all eight supported race paths. The resulting ZIP includes AetherglassUI and both payloads.


## Rebuilding the map

Maintainers run the following command with Python, NumPy and opencv-python. Supply the unmodified female Midlander vanilla smallclothes models and the AF Yiggle Medium chest and Yiggle Medium B leg models. The tool applies Hip Dips and Soft Butt to the reference legs before baking. It reads the primary skin mesh from each MDL.

    python tools/bake_vanilla_af.py VANILLA_TOP.mdl VANILLA_LEGS.mdl AF_TOP.mdl AF_LEGS.mdl solver/VanillaAF.map.gz

Commit the resulting map. The release builds embed that file in Proteus.Refit.dll. They do not invoke or package the maintainer tool.
