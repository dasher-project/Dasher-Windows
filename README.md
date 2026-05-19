# Dasher-Windows

> **Work in Progress** — Early development. Not ready for production use.

Native Windows Dasher application built with Avalonia UI.

Uses the MIT-licensed [DasherCore](https://github.com/dasher-project/DasherCore) engine via a native C++ DLL with a command-buffer rendering bridge to C#.

## Architecture

### Overview

DasherCore is a pure C++ engine with no UI framework dependencies. It computes everything — box positions, sizes, colours, text labels, the full zooming tree layout — and renders through an abstract `CDasherScreen` interface with methods like `DrawRectangle()`, `DrawString()`, `DrawCircle()`.

Because DasherCore is C++ and Avalonia is C#, they cannot call each other directly. We use a **command-buffer bridge** (Pattern B) to connect them:

```
DasherCore (C++)                    Avalonia (C#)
┌──────────────────────┐            ┌─────────────────────────┐
│ CDasherScreen        │            │ CommandRenderer.cs      │
│  DrawRectangle()     │            │   decodes [op,a,b,c,d]  │
│  DrawString()        │──serialise─│   → DrawingContext      │
│  DrawCircle()        │  via int[] │   .DrawRectangle()      │
│                      │            │   .DrawText()           │
│ WinCommandScreen     │            │   .DrawEllipse()        │
│  implements the      │            │                         │
│  abstract interface  │            │ DasherCanvas.cs         │
│  and serialises each │            │   hosts the renderer,   │
│  draw call into:     │            │   handles pointer input,│
│  [opcode,a,b,c,d,   │            │   drives the frame loop │
│   argb]              │            │                         │
└──────────────────────┘            └─────────────────────────┘
         ↕ P/Invoke (~15 C functions)
```

This is the same pattern used by [Dasher-Android](https://github.com/dasher-project/Dasher-Android) (JNI + Kotlin Canvas). The tradeoff is that the C# side contains rendering code — translating integers into Avalonia draw calls — which is plumbing Avalonia doesn't normally need.

### Two Integration Patterns

Across the Dasher ecosystem, frontends use one of two patterns to integrate with DasherCore:

**Pattern A: Direct C++ Subclassing** — used by Dasher-GTK and planned for Dasher-iOS/Dasher-macOS. The frontend is C++ (or Obj-C++) and directly subclasses DasherCore's abstract classes. Zero serialisation overhead, full type safety, full API access. Only possible when the platform language can interop with C++ natively.

**Pattern B: Command Buffer + FFI** — used by Dasher-Android and currently by Dasher-Windows. DasherCore is compiled as a native DLL. A C++ shim serialises draw calls into a flat array. The platform language (Kotlin, C#) receives the buffer over FFI and renders using its own canvas API. Necessary when the platform language cannot subclass C++ classes.

### Future: Migrating to Pattern A

The current command-buffer approach works but adds complexity. A cleaner option for Windows would be to switch to Pattern A by having the native C++ layer render directly into a shared pixel buffer:

1. The native DLL creates a `WriteableBitmap`-compatible pixel buffer (shared memory)
2. A C++ subclass of `CDasherScreen` draws directly into it using Cairo or Skia
3. Avalonia displays the bitmap via a simple `Image` control — no drawing code in C#

This would eliminate `CommandRenderer.cs` entirely, reduce the P/Invoke surface, and make the Avalonia side pure UI chrome. The native DLL would need a drawing dependency (Cairo or Skia), but the overall architecture would be simpler. This is worth investigating once the core functionality is stable.

### Component Map

| Layer | Files | Purpose |
|---|---|---|
| Native bridge | `native/WinBridge.cpp` | Exported C API (~15 functions) for P/Invoke |
| Native screen | `native/WinCommandScreen.cpp/.h` | `CDasherScreen` subclass that serialises draw calls |
| Native interface | `native/WinDasherInterface.cpp/.h` | `CDashIntfScreenMsgs` subclass — engine lifecycle, settings |
| Native file utils | `native/WinFileUtils.cpp` | Windows filesystem implementation for DasherCore |
| P/Invoke | `src/.../Engine/NativeBridge.cs` | C# declarations for the native DLL |
| Command renderer | `src/.../Engine/CommandRenderer.cs` | Decodes `[op,a,b,c,d,argb]` → Avalonia `DrawingContext` calls |
| Canvas control | `src/.../Controls/DasherCanvas.cs` | Rendering, pointer input, frame loop |
| Main window | `src/.../Views/MainWindow.axaml` | Top nav, bottom nav, canvas + message pane layout |

## Plan

- Migrate v5 core features to this new architecture
- Implement both standalone app and on-screen keyboard mode
- Use SAPI for TTS
- Investigate Pattern A rendering (shared pixel buffer) to simplify the Avalonia side

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

```batch
cd native
build.cmd
```

This runs CMake configure + build using Ninja + MSVC. Produces `build/bin/dasher_native.dll`.

### 2. Build and run the Avalonia app

```batch
cd src\Dasher.Windows
dotnet run
```

The native DLL must be in the application output directory. The build doesn't automate this yet — copy it manually:

```batch
copy ..\..\native\build\bin\dasher_native.dll .
```

## Project Structure

```
Dasher-Windows/
  DasherCore/                    Git submodule (MIT DasherCore engine)
  native/                        C++ native bridge
    CMakeLists.txt               CMake build for dasher_native.dll
    build.cmd                    One-command build (sets up MSVC env)
    WinCommandScreen.cpp/.h      CDasherScreen → command buffer serialisation
    WinDasherInterface.cpp/.h    CDashIntfScreenMsgs subclass (engine lifecycle)
    WinFileUtils.cpp             FileUtils for Windows filesystem
    WinBridge.cpp                Exported C API for P/Invoke
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
