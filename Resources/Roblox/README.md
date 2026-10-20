# Roblox textures

Roblox's own textures that RoPorter ships, embedded in the exe and read through `Export/RobloxContent.cs`. They are Roblox's
files, gathered from the Roblox client and its asset delivery (through api.ocublox.tv).

## materials/

The Materials 2022 colour maps, one per material, resized to 512 x 512. They are light, mostly neutral patterns that Roblox
multiplies by the part's colour (on terrain, by Terrain.MaterialColors). They come from the texture packs in the client's part material table (the asset ids listed just
before "Loading PartMaterialTable" in RobloxStudioBeta.exe; the terrain ones also appear in the `FStringTerrainMaterialTable2022`
client setting). A texture pack is a small XML file naming its colour, normal, roughness and metalness images.

| Material | Texture pack | Material | Texture pack |
|---|---|---|---|
| Asphalt | 9930003180 | LeafyGrass | 9920558145 |
| Basalt | 9920482224 | Limestone | 9920561624 |
| Brick | 9920482992 | Marble | 9873292869 |
| Cobblestone | 9919719550 | Metal | 9920574966 |
| Concrete | 9920484334 | Mud | 9920578676 |
| CorrodedMetal | 9920589512 | Pavement | 9920580094 |
| CrackedLava | 9920485426 | Pebble | 9920581197 |
| DiamondPlate | 10237721036 | Rock | 10129366149 |
| Fabric | 9920517963 | Salt | 9920590478 |
| Foil | 9873266399 | Sand | 9920591862 |
| Glacier | 9920518995 | Sandstone | 9920596353 |
| Granite | 9920550720 | Slate | 9920600052 |
| Grass | 9920552044 | Snow | 9920620451 |
| Ground | 9920554695 | Wood | 9920625499 |
| Ice | 9920556429 | WoodPlanks | 9920626896 |

Plastic, SmoothPlastic, Neon, Glass and ForceField have no colour map (Plastic's and Glass's packs hold only a normal map), so
they show just the part's colour.

## sky/

Roblox's default sky (used when a place has no Sky), from the client's `PlatformContent/pc/textures/sky/sky512_*.tex` files
(DXT1 DDS images), decoded and saved as JPEG.

## content/

Classic `rbxasset://` textures (SpawnLocation.png, the old gear and hat textures, the classic sky), at their rbxasset paths.
Roblox no longer installs these files: its client serves them as ordinary assets through the table in `rbxasset.txt`.

## rbxasset.txt

That table, `asset-id rbxasset://path` per line, taken from RobloxStudioBeta.exe (each path follows its asset id). RoPorter
fetches any rbxasset:// image it doesn't ship by its asset id, through the API.
