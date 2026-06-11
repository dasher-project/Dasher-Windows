# Dasher-Windows TODO

## DasherCore Status

Submodule at `7b185cd6` — **up to date** with `origin/feature-CAPI`.

### New C API Functions (all added)

| Function | Purpose | P/Invoke | Wired |
|----------|---------|----------|-------|
| `dasher_reset()` | Full model + output reset | ✅ | ✅ New button |
| `dasher_enter_game_mode()` | Enter game mode | ✅ | ✅ Game toggle |
| `dasher_leave_game_mode()` | Leave game mode | ✅ | ✅ Game toggle |
| `dasher_game_mode_active()` | Check if game mode is on | ✅ | ✅ |
| `dasher_game_set_canvas_text()` | Suppress canvas text | ✅ | ✅ |
| `dasher_game_get_target_text()` | Target sentence | ✅ | ✅ SyncGameModeState |
| `dasher_game_get_correct_count()` | Correct symbols | ✅ | ✅ |
| `dasher_game_get_target_length()` | Total target symbols | ✅ | ✅ |
| `dasher_game_get_wrong_text()` | Wrong text | ✅ | ✅ |
| `dasher_set_output_callback()` | Real-time output/delete | ✅ | ✅ DasherCanvas |
| `dasher_set_message_callback()` | Engine warnings | ✅ | ✅ Toast dialog |
| `dasher_get_language_model_count()` | LMRegistry count | ✅ | ✅ Settings filtering |
| `dasher_get_language_model_id_at()` | LM id at index | ✅ | ✅ |
| `dasher_get_language_model_name()` | LM display name | ✅ | ✅ |
| `dasher_get_language_model_description()` | LM description | ✅ | — |
| `dasher_get_language_model_param_count()` | Param count for LM | ✅ | ✅ FilterByActiveLM |
| `dasher_get_language_model_param_key()` | Param key for LM | ✅ | ✅ FilterByActiveLM |
| `dasher_find_parameter_key()` | Look up param by name | ✅ | — |

---

## Completed

1. **`dasher_reset()`** — New button resets model + output
2. **Output callback** — Real-time text insert/delete via `OnOutputEvent`
3. **Message callback** — Engine warnings as auto-dismissing dialog
4. **LM-aware settings filtering** — `FilterByActiveLanguageModel()` uses LMRegistry
5. **Access Settings v1** — AccessMethod + SelectionMethod pickers with compatibility matrix
6. **Game Mode UI** — Toggle, target text bar, canvas suppression, per-frame sync
7. **Enum dropdowns** — Verified for long and string params
8. **Auto-Speed toggle** — Wired to `BP_AUTO_SPEEDCONTROL` (key 14)
9. **Font picker** — String params with `uiType=Enum` now route to `BuildStringDropdown` automatically (SP_DASHER_FONT, SP_JOYSTICK_XAXIS, SP_GAME_TEXT_FILE, etc.)
10. **Joystick/Gamepad** — `JoystickInputService` via `Windows.Gaming.Input.Gamepad`, velocity-based input feeding `dasher_mouse_move()`, dead zone support
11. **DasherCore updated** — 31 commits integrated, DLL rebuilt, training data updated

---

## Still TODO

### High Priority

- **Switch capture UI** — "Press key or switch now..." key capture for switch-based selection methods
- **SwitchProfile model** — Up to 4 switches, scan rate (`LP_BUTTON_SCAN_TIME`), persisted alongside AccessConfiguration
- **Activate input services on AccessMethod change** — When user picks Eye Gaze or Joystick in Access Settings, actually start the tracker/joystick service (currently only sets `SP_INPUT_FILTER`)
- **Runtime testing** — Game mode, settings panel, access settings all need manual testing

### Medium Priority

- **Bottom bar cleanup** — Consider removing palette picker from bottom bar (Apple moved it to settings only)
- **Game Mode text file picker** — Settings UI for custom game text files (SP_GAME_TEXT_FILE)
- **Game Mode target bar with color runs** — Current implementation uses plain text; Avalonia 12 doesn't have `Run` inlines. Need to use a `StackPanel` of colored `TextBlock`s instead
- **Access Settings activation wiring** — `AccessConfiguration.Apply()` sets the filter but doesn't start/stop eye gaze or joystick services

### Low Priority

- **TTS settings** — Rate/pitch/volume controls for SAPI Quick Speak
- **Bluetooth socket input** — Windows v5 had `BTSocketInput` for switch access
- **Installer update** — WiX installer needs to account for new `-windows` TFM and WinRT dependencies
- **GitHub Actions CI** — Update build workflow for `net10.0-windows10.0.18362.0` TFM

---

## Already Done (prior sessions)

- ✅ New groups from manifest (Customization, Input, Language, Output, Game Mode)
- ✅ Subgroup filtering by input filter
- ✅ Locale picker in Language section
- ✅ Design tokens / colors / spacing / border radius
- ✅ Sidebar editor with Copy/Copy All/Paste/Quick Speak
- ✅ Eye gaze via WinRT GazeInputSourcePreview
- ✅ Speed control in bottom bar
- ✅ App icon
- ✅ ParameterKeys constants (BP_AUTO_SPEEDCONTROL=14, BP_LM_ADAPTIVE=15, etc.)
