# Dasher-Windows

> **Work in Progress** — Early development. Not ready for production use.

Native Windows Dasher application built with Avalonia UI.

Uses the MIT-licensed [DasherCore](https://github.com/dasher-project/DasherCore) engine via a native C++ DLL with a command-buffer rendering bridge to C#.

## Architecture

### Why a command buffer?

DasherCore is a pure C++ engine. It computes the full zooming tree layout — box positions, sizes, colours, text labels — and renders through an abstract `CDasherScreen` interface with methods like `DrawRectangle()`, `DrawString()`, `DrawCircle()`.

Because DasherCore is C++ and Avalonia is C#, we can't subclass `CDasherScreen` directly from C#. Instead, a thin C++ layer inside the native DLL implements `CDasherScreen` and serialises each draw call into a flat `int[]` buffer. C# reads this buffer via P/Invoke and replays the draw calls into Avalonia's `DrawingContext`. This uses Avalonia's native Skia rendering pipeline — we get DPI scaling, anti-aliasing, and composition for free.

```
DasherCore (C++)                    Avalonia (C#)
┌──────────────────────┐            ┌─────────────────────────┐
│ CDasherScreen        │            │ CommandRenderer.cs      │
│  DrawRectangle()     │            │   decodes [op,a,b,c,d]  │
│  DrawString()        │──int[]────►│   → DrawingContext      │
│  DrawCircle()        │  P/Invoke  │   .DrawRectangle()      │
│                      │            │   .DrawText()           │
│ WinCommandScreen     │            │   .DrawEllipse()        │
│  implements the      │            │                         │
│  abstract interface  │            │ DasherCanvas.cs         │
│  and serialises each │            │   hosts the renderer,   │
│  draw call into:     │            │   handles pointer input,│
│  [opcode,a,b,c,d,   │            │   drives the frame loop │
│   argb]              │            │                         │
└──────────────────────┘            └─────────────────────────┘
```

This is the same pattern used by [Dasher-Android](https://github.com/dasher-project/Dasher-Android) (JNI + Canvas).

### Command protocol

Each command is 6 ints: `[opcode, a, b, c, d, argb]`

| Op | Meaning | Fields |
|---|---|---|
| 0 | Clear screen | argb = background colour |
| 1 | Circle | a=x, b=y, c=radius, d=1 filled / 0 outline |
| 2 | Line | a=x1, b=y1, c=x2, d=y2 |
| 3 | Rectangle outline | a=x1, b=y1, c=x2, d=y2 |
| 4 | Rectangle filled | a=x1, b=y1, c=x2, d=y2 |
| 5 | Text | a=x, b=y, c=fontSize, d=stringIndex |

The native layer returns a `FrameData` struct with pointers into its internal buffers. No heap allocations per frame. C# copies the data out via `Marshal.Copy`. The pointers are valid until the next `dasher_frame()` call.

### Component map

| Layer | Files | Purpose |
|---|---|---|
| Native C API | `DasherCore/src/CAPI.cpp` (submodule) | Exported C API for P/Invoke, `CDasherScreen` command-buffer serialisation, engine lifecycle |
| Native build glue | `native/CMakeLists.txt` | Builds DasherCore with `BUILD_CAPI ON` into `dasher.dll` |
| P/Invoke | `src/.../Engine/NativeBridge.cs` | C# declarations for the native DLL |
| Command renderer | `src/.../Engine/CommandRenderer.cs` | Decodes command buffer → Avalonia `DrawingContext` |
| Canvas control | `src/.../Controls/DasherCanvas.cs` | Frame loop, pointer input, rendering |
| Main window | `src/.../Views/MainWindow.axaml` | Top nav, bottom nav, canvas + message pane |

## Plan

- Migrate v5 core features to this new architecture
- Implement both standalone app and on-screen keyboard mode
- Use SAPI for TTS

## Prerequisites

- .NET 10 SDK
- CMake 3.20+
- Visual Studio 2026 Community (with C++ desktop development workload, CMake tools, and Windows SDK)

### PATH setup

Add these to your system PATH (or use a Developer Command Prompt):

```
C:\Program Files\Microsoft Visual Studio\18\Community\Common7\IDE\CommonExtensions\Microsoft\CMake\CMake\bin
C:\Program Files\Microsoft Visual Studio\18\Community\Common7\IDE\CommonExtensions\Microsoft\CMake\Ninja
```

## Building

### 1. Build the native DLL

From a Developer Command Prompt for VS, or if you've added the PATH entries above:

```powershell
cd native
.\build.ps1
```

This runs CMake configure + build using Ninja + MSVC. Produces `build/bin/dasher.dll`.

### 2. Build and run the Avalonia app

```batch
cd src\Dasher.Windows
dotnet run
```

The native DLL must be in the application output directory. The build doesn't automate this yet, so copy it manually:

```batch
copy ..\..\native\build\bin\dasher.dll .
```

## Project Structure

```
Dasher-Windows/
  DasherCore/                    Git submodule (MIT DasherCore engine)
  native/                        Native build glue (DasherCore CAPI -> dasher.dll)
    CMakeLists.txt               CMake build for dasher.dll (BUILD_CAPI ON)
    build.ps1                    One-command build (sets up MSVC env)
  src/
    Dasher.Windows/              Avalonia MVVM application
      Engine/
        NativeBridge.cs          P/Invoke declarations
        CommandRenderer.cs       Decodes command buffer → Avalonia draw calls
      Controls/
        DasherCanvas.cs          Custom control (rendering + input + frame loop)
      Views/
        MainWindow.axaml/.cs     Main window (nav bars + canvas + message pane)
      ViewModels/
        MainWindowViewModel.cs   MVVM view model (language, speed, colours)
```

## License

MIT
