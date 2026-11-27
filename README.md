<img width="500" height="200" alt="logo" src="https://github.com/user-attachments/assets/f463f437-3c63-48ff-b508-eef444b9cf54" />

Export your Roblox avatar into games such as Garry's Mod, Source Filmmaker, Left 4 Dead 2, Team Fortress 2 and Deadlock or any model file format such as FBX, gLTF or GLB. This project is a work in progress so feel free to make a bug report or help with the code. Thanks.

<img width="902" height="632" alt="Screenshot 2026-10-27 101719" src="https://github.com/user-attachments/assets/ecff281a-db51-482e-9332-7ecdb2c97998" />

## Building

You need the [.NET 10 SDK](https://dotnet.microsoft.com/download/dotnet/10.0).

On Windows, run `build.bat` in the repository folder. It builds self-contained single-file programs for Windows and Linux into the `build` folder:

- `build\RoPorter-Windows-x64.exe`
- `build\RoPorter-Linux-x64`

`build.bat Debug` makes a debug build instead. Rebuilding only replaces the two programs. Settings and the Library are kept in `%APPDATA%\RoPorter` (`~/.config/RoPorter` on Linux, `~/Library/Application Support/RoPorter` on macOS). Downloaded files are cached in `%LOCALAPPDATA%\RoPorterbx_cache` (`~/.local/share/RoPorter/rbx_cache` on Linux, `~/Library/Application Support/RoPorter/rbx_cache` on macOS); **Open cache folder** and **Clear cache** in Settings open and empty it. On other systems, build one platform at a time:

```
dotnet publish Rbx2Source.csproj -c Release -r linux-x64 -o out
```

Use `win-x64`, `linux-x64`, `osx-x64` or `osx-arm64` as the runtime.

## Notes

- The **Outfit** export type exports a saved outfit by its outfit ID (yours or anyone's), so the account doesn't have to be wearing it. It exports like a bundle: Roblox's render of the outfit with the outfit's items. Garry's Mod lists outfits in their own Outfits category.
- Garry's Mod, Source Filmmaker, Left 4 Dead 2 and Team Fortress 2 exports compile with Valve's Windows tools. On Linux and macOS these run in [Wine](https://www.winehq.org/), which must be installed.
- Left 4 Dead 2 exports replace one survivor (pick which in the survivor list) and that survivor's first-person arms. They need Left 4 Dead 2 and the Left 4 Dead 2 Authoring Tools (for `studiomdl.exe` and `vpk.exe` in `Left 4 Dead 2\bin`). Copy the exported `.vpk` into `Left 4 Dead 2\left4dead2\addons`. Only avatars, bundles and outfits can be exported. The survivor's HUD portrait (the avatar's Roblox headshot), incapacitated portrait (the avatar rendered in the survivor's `Idle_Incap_Elites` pose) and lobby (or character select) picture (rendered in the survivor's idle stance) show the avatar (their templates are from [this Workshop item](https://steamcommunity.com/sharedfiles/filedetails/?id=1994377774)). The survivor keeps their name and voice, and hits anywhere count as body shots (no headshot hitgroups yet).
- Team Fortress 2 exports replace one class (pick which in the class list) and that class's first-person arms, using the game's own `studiomdl.exe` and `vpk.exe` (in `Team Fortress 2\bin`). Copy the exported `.vpk` into `Team Fortress 2\tf\custom`. Only avatars, bundles and outfits can be exported. The class's HUD pictures and class portraits show the avatar, and headshots work. Custom player models only show on servers that allow them (a local server, or community servers with `sv_pure 0`); Valve's Casual and Competitive servers use the original models. Weapons and cosmetics still attach to the class's bones, so they may not line up with the avatar.
- Team Fortress 2 place exports become a `trade_<name>` map with its materials and models packed into the `.bsp`. Copy the `.bsp` from the export's `maps` folder into `Team Fortress 2\tf\maps`, then enter `map trade_<name>` in the console. Both teams spawn at every spawn location in the place, and there are no objectives.
- Turn off **Export first-person hands** (under Export options) to keep the game's original first-person arms in Garry's Mod, Left 4 Dead 2 and Team Fortress 2 exports.- Face Poser and facial flexes need an avatar with a dynamic head (one with facial animation). Classic faces are flat pictures and can't be posed.
- Turn on **Make Garry's Mod NPCs** (under Export options) to also add Garry's Mod avatars, bundles and outfits as NPCs: a friendly and a hostile one, in the spawn menu's NPC tab under RoPorter. They are citizens using Half-Life 2's citizen animations, holding an SMG by default.- Face Poser and facial flexes need an avatar with a dynamic head (one with facial animation). Classic faces are flat pictures and can't be posed.