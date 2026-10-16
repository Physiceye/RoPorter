# RoPorter

Download **textured Roblox avatars and catalog items** as glTF 2.0 (`.glb` or
`.gltf`) or FBX models for Blender, Unity, VRChat, and other 3D software, or as a
**Garry's Mod** addon. Avatars are
exported **rigged** with a humanoid skeleton and, when the dynamic head provides
facial animation data, **face shape keys**. Materials include Roblox **PBR**
maps (normal, metalness, roughness, emissive). Garry's Mod (installed through
Steam) is needed only for the Garry's Mod format; Discord is no longer used.

## Using the exporter

1. Run the newly built `RoPorter.exe` (see below). The checked-in executable
   and old upstream releases predate this glTF workflow.
2. Choose **Avatar** and enter a username, user ID, or profile URL; or choose
   **Catalog item** and enter an item ID or catalog URL. Bundle IDs are not item IDs.
   A preview (the avatar's headshot or the item's thumbnail) appears on the
   right once you stop typing; if it can't be loaded, a broken-image
   placeholder is shown.
3. Select an output folder and choose an **Export format**:
   - **GLB (single file)**, the default: `{name}.glb` embeds geometry, materials,
     skeleton, shape keys, and textures. Only this file is needed.
   - **glTF (separate files)**: `{name}.gltf`, `{name}.bin`, and `textures/`.
     Keep these together when moving or importing the model.
   - **FBX (Unity, Blender)**: `{name}.fbx` (binary FBX 7.4) with the same
     skeleton, skinning, blend shapes (shape keys) and materials, plus
     `textures/`. Colour, normal and emissive textures are embedded in the FBX
     and also referenced from `textures/`; keep the folder next to the FBX so
     Unity assigns them. FBX materials have no metalness/roughness inputs, so
     those maps are left in `textures/` (`metal-rough-*.png`, glTF packing:
     roughness in G, metalness in B). Unity imports it as a Humanoid with every
     bone mapped and all face blend shapes, facing +Z.
   - **Garry's Mod**: an addon folder with a compiled Source model (see
     [Garry's Mod](#garrys-mod)). Avatars become a playermodel that also
     works as a ragdoll; catalog items become physics props.
   - Tick **Single texture** for games that allow only one material: every
     material is merged into one, with one texture set (see
     [Single texture](#single-texture)).
   - Drag **Scale** to shrink the model: 1 (the default and maximum) is full
     size, down to 0.1. Geometry, skeleton, shape keys, and Garry's Mod physics,
     hitboxes, and attachments all scale together; Garry's Mod first-person
     arms stay full size so they fit weapon viewmodels.
4. For VRChat, keep **Add VRChat shape keys** ticked (the default) for the
   `vrc.*` visemes, blinks, and eyelid shapes. For VRChat face tracking, tick
   **Add FT support**; the two options are independent, so face tracking can be
   added with or without the VRChat shape keys
   (avatars only, not for Garry's Mod; see [Face tracking](#face-tracking-vrcfacetracking)).
5. Click **Export**. Everything is named after the export's **name**: the
   avatar's Roblox username (for example `Ocublox\Ocublox.glb`), or the item ID
   for catalog items (`1102995667\1102995667.glb`). The username comes from the
   API's `/v1/users?userId=` route (or Roblox's users API without one); if it
   can't be looked up, the user ID is used and the log says so. Exporting the
   same name again replaces that folder. If another program is using the
   previous export (for example Garry's Mod while it runs with the addon
   installed), its files are overwritten in place instead.
   The log lists which body parts were recognised and whether face shape keys
   were added (or why not).
6. In Blender (including Blender 5), use **File > Import > glTF 2.0 (.glb/.gltf)**
   or **File > Import > FBX (.fbx)**. In Unity, drop the export folder into
   `Assets` and set the model's *Rig* to *Humanoid*.

The output folder, export format, scale, and the **Single texture**, **Add VRChat shape
keys**, and **Add FT support** options are remembered in
`%LocalAppData%\RoPorter\settings.json` (settings saved under the old
`Rbx2Source` name are picked up automatically). The API address is not shown in
the window; it defaults to `https://api.ocublox.tv` and can be changed with the
`apiBase` value in that file (see [Using your own API](#using-your-own-api)).
Garry's Mod is found through Steam; if it is installed somewhere Steam doesn't
list, add `"garrysModPath": "D:\\Games\\GarrysMod"` to that file.

## What you get

### Placement and scale

Models are moved out of Roblox's world coordinates: they stand on the ground
plane (lowest point at Y = 0), are centred on the origin (avatars on their
hips), and face **+Z**, the glTF/Unity forward direction. Scale is real-world:
0.28 m per Roblox stud, so a standard R15 avatar is about 1.6 m tall (more with
hats). Blender shows glTF +Z forward as -Y. FBX files use the same layout
(metres, Y up, facing +Z).

### Humanoid rig (avatars)

The skeleton is a full Unity/VRChat humanoid with all 55 `HumanBodyBones`
names, so Unity's Humanoid auto-mapping assigns every bone (required and
optional) without manual fixes:

- `Hips` › `Spine` › `Chest` › `UpperChest` › `Neck` › `Head` › `Jaw`,
  `LeftEye`, `RightEye`
- `UpperChest` › `Left/RightShoulder` › `UpperArm` › `LowerArm` › `Hand` › five
  fingers (`Thumb`, `Index`, `Middle`, `Ring`, `Little`), each
  `Proximal` › `Intermediate` › `Distal` (for example `LeftIndexProximal`)
- `Hips` › `Left/RightUpperLeg` › `LowerLeg` › `Foot` › `Toes`

- Joints are placed at the seams between the rendered R15 body parts; the spine
  bones are spaced evenly from the hips to the neck seam. Eyes and jaw come from
  the dynamic head's own face bones when available.
- Body parts are weighted rigidly to their bone, like Roblox's R15 joints
  (`Hips`, `Chest`, `Head`, and the arm and leg bones).
- The extra humanoid bones (`Spine`, `UpperChest`, `Neck`, shoulders, toes,
  eyes, jaw, fingers) don't deform the avatar. Unity only recognises bones that
  skin something, so each one carries a 1% weight on a single nearby vertex.
- The bind pose is a **T-pose**: the arms are raised straight out to the sides
  (upper arm and forearm exactly horizontal), with the mesh and layered
  sleeves following. Unity reads animation relative to this pose, so arms
  lowered by animation hang beside the body like on a standard humanoid.
- **Accessories** (hats, hair, glasses, back items, shoulder pets…) are attached
  rigidly — 100% to one bone — exactly as Roblox welds them: to the body part
  holding the accessory's attachment. For example `HatAttachment` → `Head`,
  `BodyBackAttachment` and `LeftCollarAttachment` → `Chest`,
  `WaistBackAttachment` → `Hips`, `LeftShoulderAttachment` → `LeftUpperArm`.
- **Layered clothing** (shirts, jackets, pants, dresses, shoes) keeps **Roblox's
  own skin weights**: the item's mesh (including Draco-compressed mesh v7) is
  downloaded, matched to its rendered geometry by triangles and UVs, and its
  weights on the R15 skeleton are carried over (`LowerTorso` → `Hips`,
  `UpperTorso` → `Chest`, limbs to their bones; custom bones use their nearest
  R15 parent). The log shows "layered, Roblox skin weights" for these items.
- If an item's mesh cannot be downloaded or does not match the render, the
  clothing is **auto-rigged** instead: each vertex is weighted to the body part
  it covers, blending across joints and smoothed over the mesh. Clothing only
  blends between connected parts (a left shoe never moves with the right foot).
- Which render groups are accessories and which are layered clothing comes from
  the avatar's worn items: each accessory's model is read for its attachment and
  for a `WrapLayer` (layered clothing). Accessories are matched to render groups
  by where their attachment sits on the body; the export log lists each match.
  If an accessory's model cannot be downloaded, its asset type's default
  attachment is used, and groups that match no worn item are weighted from their
  shape.
- Roblox removes body geometry hidden under layered clothing. Hidden parts are
  rebuilt from the skeleton and the other side of the body, so auto-rigged
  clothing (for example pants over the thighs) still follows the right bones.

Roblox's blocky arms touch the torso, so poses that swing an arm inward past
vertical (Unity's lowest "Arm Down-Up" muscle values cross the hands in front
of any humanoid) push the hands into the body. If an animation does that, raise
the minimum of *Arm Down-Up* in Unity's *Configure Avatar* › *Muscles &
Settings* (for example from -60 to -45).

### Face shape keys (dynamic heads)

If the avatar wears a dynamic head whose mesh contains Roblox FACS data, the
exporter adds shape keys (glTF morph targets, named via `extras.targetNames`) on
a separate `Face` mesh:

- One shape key per FACS control the head defines, named after Roblox's
  `FaceControls` properties (for example `JawDrop`, `LeftEyeClosed`, `Pucker`,
  `Funneler`), even though meshes store them abbreviated (`c_JD`, `l_EC`, ...).
  Each key is evaluated by posing the head's face bones exactly as Roblox does,
  including its corrective poses.
- With **Add VRChat shape keys** ticked: VRChat visemes `vrc.v_sil`, `vrc.v_pp`, `vrc.v_ff`, `vrc.v_th`, `vrc.v_dd`,
  `vrc.v_kk`, `vrc.v_ch`, `vrc.v_ss`, `vrc.v_nn`, `vrc.v_rr`, `vrc.v_aa`, `vrc.v_e`,
  `vrc.v_ih`, `vrc.v_oh`, `vrc.v_ou`, plus `vrc.blink`, `vrc.blink_l`, and `vrc.blink_r`.
  Each viseme follows the mouth shape in Oculus Lipsync's
  [viseme reference](https://developers.meta.com/horizon/documentation/native/audio-ovrlipsync-viseme-reference/)
  (for example `vrc.v_pp` presses the lips shut, `vrc.v_ff` tucks the lower
  lip, `vrc.v_th` and `vrc.v_nn` show the tongue, `vrc.v_oh` and `vrc.v_ou`
  round the lips), built from the head's FACS controls. Roblox's
  `LipsTogether` only closes the lips while the jaw is open, so it is always
  paired with an equal `JawDrop`. A viseme is created when the head has its
  main controls; minor ones (tongue, lip press) are used when available.
- With **Add VRChat shape keys** ticked: VRChat eyelid shapes `vrc.lookingup` and `vrc.lookingdown` (Roblox's
  `EyesLookUp` / `EyesLookDown`). In the VRChat Avatar Descriptor, set *Eye
  Look* › *Eyelids* to *Blendshapes* with `vrc.blink`, `vrc.lookingup`, and
  `vrc.lookingdown`, and assign the `LeftEye`/`RightEye` bones. The eye bones
  don't deform the mesh, so these shapes move both the eyes and the lids as
  VRChat looks up and down.

To keep shape keys accurate, the rendered head is replaced with the head mesh in
its neutral pose, fitted to the render and textured with the head's own texture
over the avatar's skin colour. If that texture cannot be downloaded, the shape
keys are transferred onto the rendered head instead (the log says so).

**Mood (`Default`):** if the avatar wears a Mood (for example a happy face), its
expression is added as the first shape key, `Default`, evaluated from the
mood's FACS values with the same face rig as the other keys. It is **on by
default** (value 1) in GLB/glTF and in Blender, so the avatar looks as it does
in Roblox; visemes, blinks and face tracking shapes add on top of it, as they
do in-game. Unity's FBX importer ignores initial blend shape values, so for FBX
in Unity set `Default` to 100 on the Face *Skinned Mesh Renderer*. Set it to 0
for the neutral face. Animated moods use their first keyframe.

By default Roblox serves a legacy build of dynamic heads (a `SpecialMesh` whose
mesh has no skeleton or FACS data). The exporter therefore requests heads with
the `Roblox-AssetFormat: avatar_meshpart_head` header, which returns the real
`MeshPart` head with `FaceControls`, and falls back to the default build for
heads that do not have one.

Face shape keys are skipped, with the reason in the log, when:

- the head has no FACS data (classic heads);
- the head asset needs authentication and the API can't download it;
- the model file is Zstandard-compressed, which this exporter cannot decode yet.

### Face tracking (VRCFaceTracking)

With **Add FT support** ticked (with or without **Add VRChat shape keys**), avatars also get face shapes named after
[Unified Expressions](https://docs.vrcft.io/docs/tutorial-avatars/tutorial-avatars-extras/unified-blendshapes),
the face shape standard that [VRCFaceTracking](https://docs.vrcft.io/docs/intro/getting-started)
uses for avatars. Each shape is evaluated from the matching Roblox `FaceControls` pose:

| Unified Expressions | Roblox FaceControls |
| --- | --- |
| `EyeLookUp/Down Left/Right` | `EyesLookUp/Down`, split per eye |
| `EyeLookOutLeft`, `EyeLookInRight` | `EyesLookLeft`, split per eye |
| `EyeLookInLeft`, `EyeLookOutRight` | `EyesLookRight`, split per eye |
| `EyeClosed`, `EyeWide` (Left/Right) | `EyeClosed`, `EyeUpperLidRaiser` |
| `BrowPinch` (Left/Right) | `Corrugator`, split per side |
| `BrowLowerer`, `BrowInnerUp`, `BrowOuterUp` (Left/Right) | `BrowLowerer`, `InnerBrowRaiser`, `OuterBrowRaiser` |
| `NoseSneer`, `CheekSquint`, `CheekPuff` (Left/Right) | `NoseWrinkler`, `CheekRaiser`, `CheekPuff` |
| `JawOpen`, `JawLeft`, `JawRight`, `MouthClosed` | `JawDrop`, `JawLeft`, `JawRight`, `LipsTogether` |
| `LipSuckUpper`, `LipSuckLower`, `LipFunnel`, `LipPucker` | `UpperLipSuck`, `LowerLipSuck`, `Funneler`, `Pucker` |
| `MouthUpperUp`, `MouthLowerDown`, `MouthSmile`, `MouthFrown`, `MouthStretch`, `MouthDimple` (Left/Right) | `UpperLipRaiser`, `LowerLipDepressor`, `LipCornerPuller`, `LipCornerDown`, `LipStretcher`, `Dimpler` |
| `MouthRaiserUpper`, `MouthRaiserLower`, `MouthPress`, `MouthLeft`, `MouthRight` | `ChinRaiserUpperLip`, `ChinRaiser`, `LipPresser`, `MouthLeft`, `MouthRight` |
| `TongueOut`, `TongueUp`, `TongueDown` | same names |

Both standards name sides from the character's point of view. Roblox moves both
eyes with one control, so eye shapes are split using the head's left and right
eye bones. Shapes are only created when the head defines the Roblox control
and the pose actually moves the face. Roblox has no equivalent for some
Unified Expressions shapes (for example `EyeSquint`, `JawForward`, pupil
dilation, and cheek suck); VRCFaceTracking works without them.

This adds the face shapes only. To drive them in VRChat, set the avatar up in
Unity with VRCFaceTracking's parameters and animator, as described in its
[avatar setup guide](https://docs.vrcft.io/docs/category/avatar-setup-guide).
The `LeftEye`/`RightEye` bones exist for the humanoid but don't move the eyes
(Roblox eyes move through FACS), so eye movement uses the `EyeLook…` shapes
rather than VRChat's built-in eye-bone tracking. Face tracking needs a dynamic head with
facial animation; for other heads the log says no shapes were added.

### Materials

UVs, normals, base colours, and textures are preserved. Body textures (classic
clothing composites) and Roblox "overlay" textures show the part colour through
their transparent areas, as in Roblox, and are exported opaque. Accessory
textures are not tinted by the part colour. Their transparency is exported as
`MASK` (cut-out) or `BLEND` based on the actual alpha values. Materials are
double-sided.

Items with a SurfaceAppearance keep their **PBR maps**, as Roblox's renderer
provides them:

| Roblox | glTF material |
| --- | --- |
| `NormalMap` | `normalTexture` (OpenGL convention), with generated tangents |
| `MetalnessMap`, `RoughnessMap` | `metallicRoughnessTexture` (roughness in G, metalness in B) |
| `EmissiveMaskContent` × colour × `EmissiveTint`, `EmissiveStrength` | `emissiveTexture`, with `KHR_materials_emissive_strength` above 1 |

Parts without these maps are non-metallic and fully rough.

### Single texture

With **Single texture**, every material is merged into one material
(`{name}_material`) and one texture set, saved next to the model:

- `{name}_color.png` (always)
- `{name}_normal.png`, `{name}_metalness.png`, `{name}_roughness.png`,
  `{name}_emissive.png` when any part of the model has those maps

`{name}` is the username (avatars) or item ID (catalog items). Each material gets its own region of the atlas
(tiled UVs are repeated inside it), materials that share textures share a
region, and the UVs are remapped. Parts without a map are filled with neutral
values (flat normal, no metal, full roughness, no emission). The atlas is a
power of two up to 4096 × 4096; larger texture sets are scaled down evenly. A
glTF export also writes `{name}_metallicRoughness.png`, the glTF-packed copy its
material uses; a GLB embeds it. An FBX uses the colour, normal and emissive
maps.

## Garry's Mod

The **Garry's Mod** format builds an addon and compiles it with Garry's Mod's
own `studiomdl`, so Garry's Mod must be installed. The export folder (named
after the username or item ID) is the addon; copy it into `garrysmod\addons` and restart the game.

```text
Ocublox\
  addon.json                              (titled with the account's username)
  lua\autorun\roporter_ocublox.lua        (avatars: registers the playermodel and its arms)
  materials\models\roporter\ocublox\     (VMT + VTF per material)
  models\player\roporter\ocublox.mdl     (avatars; .vvd, .vtx, .phy beside it)
  models\weapons\roporter\c_arms_ocublox.mdl  (avatars: first-person arms)
  models\roporter\items\{id}.mdl         (catalog items, as props)
  src\                                      (SMD/QC sources and studiomdl logs)
```

- **Listed under the username.** The playermodel is registered under the
  Roblox account's username (for example `Ocublox`), so players can find their
  avatar in the *Player Model* list. If the username can't be looked up, it is
  listed as `RoPorter {id}`. Paths inside the addon use the lowercase name, as
  Source expects.
- **Custom first-person arms.** The avatar's own arms, sleeves, and hands (the
  triangles carried mainly by the arm, hand, and finger bones) are compiled as a
  `c_arms` model on the same bones Valve's arms use, and registered with
  `player_manager.AddValidHands`, so weapons show the avatar's arms. Each arm is
  scaled to Valve's arm length so it meets the weapon viewmodel's joints; the
  blocky Roblox shape is kept. If no arm geometry is found or it fails to
  compile, the citizen arms are used and the log says so.
- **One model for both uses.** An avatar is rigged to Valve's `ValveBiped`
  skeleton (the 56 bones every player animation in Garry's Mod uses), with
  Kleiner's attachments (eyes, hands, ...) and hand/foot IK chains. Pick it
  under *Player Model* in the context menu, or spawn
  `models/player/roporter/{name}.mdl` (for example from the spawn menu's model
  browser) and it falls as a ragdoll with per-limb physics and joint limits
  taken from Garry's Mod's own player ragdolls.
- The Roblox body keeps its proportions in every animation (the "proportions
  trick": Valve's reference skeleton is subtracted from ours and layered under
  all animations). The model is scaled so the eyes are at Valve's eye height,
  and the arms are re-posed to Valve's reference pose.
- Weights come from the same Roblox skin weights as the other formats.
- The avatar's mood expression (the **Default** shape key) is baked into the
  mesh. Source models get no shape keys, flexes, or visemes, so the VRChat and
  face-tracking options are unavailable for this format.
- Materials are `VertexLitGeneric` with colour, normal (`$bumpmap`) and emissive
  (`$selfillum` mask) textures. Source has no metalness/roughness, so those
  maps are not used. Textures are resized to powers of two, at most 2048
  pixels per side, keeping the aspect ratio. Transparent materials use
  `$alphatest` or `$translucent` and are drawn double-sided.
- **Single texture** works here too: one material and one texture set; the
  merged PNGs are kept in `src`.
- Catalog items are real-size props (1 stud = 1 foot) with a collision model.

## Using your own API

Roblox requires authentication for its 3D thumbnail endpoints and for many
assets (including most dynamic head meshes). The exporter never asks for
account cookies. Instead, the API base (default `https://api.ocublox.tv`, set by
`apiBase` in `settings.json`)
points to a service that mirrors these routes. It must use HTTPS, except for a
locally running copy (`http://localhost:...`, `http://127.0.0.1:...`):

| Route | Used for | Expected response |
| --- | --- | --- |
| `GET {base}/v1/users/avatar-3d?userId={id}` | Avatar 3D render | Same JSON as Roblox's thumbnails API (`state`, `imageUrl`) |
| `GET {base}/v1/assets-thumbnail-3d?assetId={id}` | Catalog item 3D render | Same JSON as Roblox's thumbnails API |
| `GET {base}/v1/users?userId={id}` | Avatar export name | Same JSON as Roblox's users API; `name` (the username, not the display name) is used |
| `GET {base}/v1/users/headshot?userId={id}` | Avatar preview | Same JSON as Roblox's thumbnails API, with a `*.rbxcdn.com` `imageUrl` |
| `GET {base}/v1/catalog/thumbnail?assetId={id}` | Catalog item preview | Same JSON as Roblox's thumbnails API, with a `*.rbxcdn.com` `imageUrl` |
| `GET {base}/v1/asset/?id={id}` | Accessory models and meshes (rigging) and head model, mesh, and texture (face shape keys) | The asset bytes, or a redirect to `*.rbxcdn.com` / the API host. Forward the `Roblox-AssetFormat` request header to Roblox when present. |

The asset route is only used when Roblox's public asset delivery returns HTTP
401/403. Redirects are followed only to Roblox hosts or the API's own host, and
render files must still come from `*.rbxcdn.com`. Avatar asset lists come from
Roblox's public avatar API. **Never put `.ROBLOSECURITY` cookies or other
account credentials into this application or its settings.**

## Limitations

- Geometry is Roblox's rendered 3D thumbnail (OBJ/MTL), not the original source
  assets. Animations, layered-clothing cages, and editable clothing layers are
  not exported.
- The rig is reconstructed from the render. Unusual bodies (non-humanoid
  packages, extreme proportions) may need joint adjustments. If no Roblox body
  parts are recognised, a skeleton with standard R15 proportions is used and the
  log says so.
- FACS face shapes are computed from Roblox's mesh format specification; heads
  with unusual bone setups may need touch-ups.
- Moderated, missing, or non-renderable items cannot be exported, and Roblox
  API changes may affect availability.

CDN references are supported as plain content hashes, lifetime-prefixed hashes
(`30DAY-...`), and labelled render outputs (`30DAY-Avatar-...-Obj`), including
gzip-compressed content with or without a `Content-Encoding` header.
Re-exporting a name replaces its folder only after the new export has been
written; cancellation or a failed download removes only that export's
temporary files and keeps the previous export. When the previous folder can't
be renamed because a program holds files inside it (Garry's Mod watches its
addons' Lua folders), the new files are copied over it and old files are
removed where possible; a file that can't be overwritten stops the export with
a message naming it.

## Building and testing

- Windows, .NET Framework 4.7.2 runtime and targeting pack.
- Visual Studio 2022 with .NET desktop development, or a current .NET SDK
  with the framework targeting pack installed.
- No sibling `Roblox-File-Format` checkout is needed.

```powershell
dotnet build Rbx2Source.sln --configuration Release
dotnet test Tests\Rbx2Source.Tests.csproj --configuration Release
```

Run `bin\Release\RoPorter.exe`. The build embeds managed dependencies using
Costura. Draco-compressed meshes are decoded with
[Openize.Drako](https://github.com/openize-com/openize-drako-net) (MIT). The
old launcher/auto-updater is deliberately not used: it would replace
this application with an upstream Source exporter.

The project explicitly compiles only the GLB / glTF interface and export pipeline.
Legacy Rbx2Source assembly/compiler code and resources remain in the repository
for historical reference, but are not compiled, packaged, or accessible from the
app; the Garry's Mod format is a new exporter (`Export\SourceExporter.cs`).
Tests use deterministic HTTP responses and spec-built mesh fixtures. They check
glTF structure, skinning, the full humanoid hierarchy, helper-bone anchoring,
the T-pose, rig placement and weights, hidden-limb recovery,
accessory matching with rigid and layered weighting, Roblox skin-weight
transfer, FACS parsing (mesh v5/v6/v7 Draco,
raw and quantised) and evaluation, Unified Expressions face-tracking shapes,
face fitting and replacement, PBR materials and tangents, single-texture
atlases, binary FBX structure (skeleton, skin clusters, blend shapes, embedded
textures), VTF and VPK files, Garry's Mod playermodel, first-person arms, and prop compiles (these
run studiomdl and are reported inconclusive when Garry's Mod isn't installed),
viseme recipes, model-file parsing, API routing, access errors,
decompression, and cleanup.

Only download and use content you have permission to use, in accordance with
Roblox's terms and the creator's rights.
