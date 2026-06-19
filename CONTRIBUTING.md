# Contributing to Dasher-Windows

Thank you for your interest in improving Dasher for Windows! This guide covers
the specifics of this repository. For project-wide conventions (code of
conduct, security, RFCs), see the
[organisation CONTRIBUTING](https://github.com/dasher-project/.github/blob/main/CONTRIBUTING.md).

## Quick start

```bash
git clone --recurse-submodules https://github.com/dasher-project/Dasher-Windows.git
cd Dasher-Windows
dotnet build src/Dasher.Windows/Dasher.Windows.csproj
```

Requirements:

- **.NET 10 SDK** (preview)
- **Windows 10 18362+** (TargetFramework is `net10.0-windows10.0.18362.0`)
- **Visual Studio 2022** or **Rider** (optional, for IDE support)

## What lives where

| Directory | Purpose |
| :-- | :-- |
| `src/Dasher.Windows/` | Main application (Avalonia UI + DasherCore P/Invoke) |
| `src/Dasher.Windows/Views/` | AXAML windows and controls |
| `src/Dasher.Windows/Engine/` | Native bridge (`NativeBridge.cs`), DasherCore interop |
| `src/Dasher.Windows/Services/` | Analytics, updates, platform services |
| `src/Dasher.Windows/Controls/` | Reusable UI controls (settings panel, etc.) |
| `DasherCore/` | **Submodule** — the C++ engine (do not edit here; PR upstream) |

## Code style

- C# 13+ features are welcome (target framework is net10.0).
- Enable `<Nullable>enable</Nullable>` — no `null` warnings.
- 4-space indentation; no trailing whitespace.
- Use `var` when the type is obvious from the right-hand side.
- Use file-scoped namespaces.
- AXAML uses `x:Static` for resource lookups and compiled bindings.

## DasherCore changes

DasherCore is a git submodule pointing to
[dasher-project/DasherCore](https://github.com/dasher-project/DasherCore).
**Do not modify it inside this repo.** If you need an engine change, open a PR
against DasherCore directly, then bump the submodule pin here once merged.

## Definition of Done

- [ ] Builds cleanly (`dotnet build` with zero warnings)
- [ ] Commits are signed off (DCO) — `git commit -s`
- [ ] If you changed a user-visible capability, update the
      [feature status matrix](https://dasher.at/status/) (`website` repo:
      `src/data/feature-status.json`) — the PR template has a checkbox for this
- [ ] If you changed UX/hardware interaction across platforms, check whether an
      [RFC](https://github.com/dasher-project/governance/tree/main/rfcs) is needed

## Pull request process

1. Fork and branch from `main`.
2. Open a PR — the org-level PR template will prompt you on parity, RFCs, and
   the feature matrix.
