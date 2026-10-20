# <img width="500" height="200" alt="logo" src="https://github.com/user-attachments/assets/f463f437-3c63-48ff-b508-eef444b9cf54" />

Download **Roblox avatars and catalog items** as glTF 2.0 (`.glb` or
`.gltf`) or FBX models for Blender, Unity, VRChat, and other 3D software, or as a
**Garry's Mod** addon. Avatars are
exported **rigged** with a humanoid skeleton and, when the dynamic head provides
facial animation data, **face shape keys**. Materials include Roblox **PBR**
maps (normal, metalness, roughness, emissive).


<img width="902" height="612" alt="app" src="https://github.com/user-attachments/assets/d189c81a-23ca-47aa-b56c-9debd8980b56" />

## Windows, Linux and macOS

RoPorter runs on Windows (x64), Linux (x64) and macOS (Apple Silicon and Intel). Each download is a single program with
nothing else to install. On macOS the app is not signed: the first time, right-click it and choose **Open**.

GLB, glTF and FBX exports work everywhere. The **Garry's Mod** and **Source Filmmaker** exports compile with Valve's tools,
which are Windows programs: on Linux and macOS, install the Windows version of Garry's Mod (in Steam, *Properties >
Compatibility > Force the use of a specific Steam Play compatibility tool*) and [Wine](https://www.winehq.org/), and
RoPorter runs them through Wine.

To build it yourself, install the [.NET 10 SDK](https://dotnet.microsoft.com/download) and run
`dotnet publish Rbx2Source.csproj -c Release -r win-x64` (or `linux-x64`, `osx-arm64`, `osx-x64`).
