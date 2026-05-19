# Dasher-Windows

!! Warning "Work in Progress"
    This project is in early development. Don't look at this repo for working builds yet. 

Native Windows Dasher application built with Avalonia UI.

Uses the MIT-licensed [DasherCore](https://github.com/dasher-project/DasherCore) engine via a native C++ DLL with a command-buffer rendering bridge to C#.

## Architecture

- **Native layer** (`native/`): C++ code that wraps DasherCore into a DLL. Implements the command-buffer screen pattern and exposes a flat C API.
- **Avalonia app** (`src/Dasher.Windows/`): C# frontend using Avalonia UI. Calls the native DLL via P/Invoke, decodes draw commands, and renders them.

## Plan
- Migrate v5 core features to this new architecture
- Implement a app and on-screen keyboard with this app
- Use SAPI for TTS in this codebase for now

## Prerequisites

- .NET 10 SDK
- CMake 3.20+
- Visual Studio 2022 (with C++ desktop development workload) or Build Tools

## Building

### 1. Build the native DLL

```batch
cd native
cmake -B build -G "Visual Studio 17 2022" -A x64
cmake --build build --config Release
```

This produces `build/bin/dasher_native.dll`.

### 2. Build and run the Avalonia app

```batch
cd src\Dasher.Windows
dotnet run
```

The native DLL must be in the application's output directory. You can copy it:
```batch
copy ..\..\native\build\bin\Release\dasher_native.dll .
```

## Project Structure

```
Dasher-Windows/
  DasherCore/                    Git submodule (MIT DasherCore engine)
  native/                        C++ native bridge
    CMakeLists.txt               CMake build for dasher_native.dll
    WinCommandScreen.cpp/.h      CDasherScreen → command buffer
    WinDasherInterface.cpp/.h    CDashIntfScreenMsgs subclass
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
        MainWindow.axaml/.cs     Main application window
      ViewModels/
        MainWindowViewModel.cs   MVVM view model
```

## License

MIT
