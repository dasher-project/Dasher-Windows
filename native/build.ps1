$env:MSVC_ROOT = "C:\Program Files\Microsoft Visual Studio\18\Community\VC\Tools\MSVC\14.44.35207"
$env:WIN_SDK = "C:\Program Files (x86)\Windows Kits\10"
$env:SDK_VER = "10.0.26100.0"
$env:PATH = "$env:MSVC_ROOT\bin\HostX64\x64;$env:WIN_SDK\bin\$env:SDK_VER\x64;$env:PATH"
$env:INCLUDE = "$env:MSVC_ROOT\include;$env:WIN_SDK\Include\$env:SDK_VER\ucrt;$env:WIN_SDK\Include\$env:SDK_VER\um;$env:WIN_SDK\Include\$env:SDK_VER\shared"
$env:LIB = "$env:MSVC_ROOT\lib\x64;$env:WIN_SDK\Lib\$env:SDK_VER\ucrt\x64;$env:WIN_SDK\Lib\$env:SDK_VER\um\x64"

$cmake = "C:\Program Files\Microsoft Visual Studio\18\Community\Common7\IDE\CommonExtensions\Microsoft\CMake\CMake\bin\cmake.exe"
$ninja = "C:\Program Files\Microsoft Visual Studio\18\Community\Common7\IDE\CommonExtensions\Microsoft\CMake\Ninja\ninja.exe"

Set-Location C:\github\DasherProjects\Dasher-Windows\native

if (Test-Path build) { Remove-Item -Recurse -Force build }

Write-Host "Configuring..."
& $cmake -B build -G Ninja -DCMAKE_C_COMPILER=cl -DCMAKE_CXX_COMPILER=cl -DCMAKE_BUILD_TYPE=Release "-DCMAKE_MAKE_PROGRAM=$ninja"
if ($LASTEXITCODE -ne 0) { Write-Host "CONFIGURE FAILED"; exit 1 }

Write-Host "Building..."
& $cmake --build build --config Release
if ($LASTEXITCODE -ne 0) { Write-Host "BUILD FAILED"; exit 1 }

Write-Host "Copying Strings to Data..."
$stringsSrc = Join-Path $PSScriptRoot "..\DasherCore\Strings"
$stringsDst = Join-Path $PSScriptRoot "..\DasherCore\Data\Strings"
if (Test-Path $stringsSrc) {
    if (Test-Path $stringsDst) { Remove-Item -Recurse -Force $stringsDst }
    Copy-Item -Recurse $stringsSrc $stringsDst
    Write-Host "  Copied $(Get-ChildItem $stringsDst -Filter *.json).Count locale files"
}

$dllSrc = Join-Path $PSScriptRoot "build\bin\dasher.dll"
$dllDst = Join-Path $PSScriptRoot "..\src\Dasher.Windows\dasher.dll"
Copy-Item $dllSrc $dllDst -Force
Write-Host "Copied dasher.dll to project"

Write-Host "SUCCESS"
Get-ChildItem build\bin\*.dll
