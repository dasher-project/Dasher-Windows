# Dasher-Windows TODO

## DasherCore Status

We are at `c74c210d`. Latest is `7b185cd6` — **31 commits behind**.

### New C API Functions (since `c74c210d`)

| Function | Purpose | P/Invoke Added | Wired Up |
|----------|---------|---------------|----------|
| `dasher_reset()` | Full model + output reset | ❌ | ❌ |
| `dasher_enter_game_mode()` | Enter game mode | ❌ | ❌ |
| `dasher_leave_game_mode()` | Leave game mode | ❌ | ❌ |
| `dasher_game_mode_active()` | Check if game mode is on | ❌ | ❌ |
| `dasher_game_set_canvas_text()` | Suppress canvas text in game mode | ❌ | ❌ |
| `dasher_game_get_target_text()` | Get target sentence | ❌ | ❌ |
| `dasher_game_get_correct_count()` | Correct symbols typed | ❌ | ❌ |
| `dasher_game_get_target_length()` | Total target symbols | ❌ | ❌ |
| `dasher_game_get_wrong_text()` | Wrong text since last correct | ❌ | ❌ |
| `dasher_set_output_callback()` | Real-time output/delete events | ❌ | ❌ |
| `dasher_set_message_callback()` | Engine warnings/errors to native UI | ❌ | ❌ |
| `dasher_get_language_model_count()` | LMRegistry count | ❌ | ❌ |
| `dasher_get_language_model_id_at()` | LM id at index | ❌ | ❌ |
| `dasher_get_language_model_name()` | LM display name | ❌ | ❌ |
| `dasher_get_language_model_description()` | LM description | ❌ | ❌ |
| `dasher_get_language_model_param_count()` | Param count for specific LM | ❌ | ❌ |
| `dasher_get_language_model_param_key()` | Param key for specific LM | ❌ | ❌ |
| `dasher_find_parameter_key()` | Look up param by enum name | ❌ | ❌ |

### Other DasherCore Changes

- All numeric params now have proper `uiType` (Slider/Step/Enum) with min/max/step
- `tier` values `expert`/`advanced` mapped to `advancedSetting` flag
- CTW crash fix (typo `<` instead of `<<`)
- Game mode fixes (arrow tracking, null pointer guards)
- Training data replaced with real AAC conversational text (34 languages)
- New alphabets generated for world languages
- Space symbol parsing fixed (unicode attribute from textCharAction)

---

## High Priority

### 1. `dasher_reset()` on New Button ✅ DONE
- Now calls `dasher_reset()` to reset model + output
- Location: `MainWindow.axaml.cs` → `OnNew`

### 2. Output Callback (`dasher_set_output_callback`) ✅ DONE
- Registered in `DasherCanvas.Initialize()`
- `OnOutputEvent` handles text insert/delete via `OutputText` property
- Fires on `dasher_frame()` thread, marshals to UI thread

### 3. Message Callback (`dasher_set_message_callback`) ✅ DONE
- Registered in `DasherCanvas.Initialize()`
- Fires `EngineMessage` event → MainWindow shows auto-dismissing dialog
- 5-second auto-close for info messages

### 4. LM-Aware Settings Filtering ✅ DONE
- `FilterByActiveLanguageModel()` in SettingsPanel uses LMRegistry API
- Shows only params relevant to active language model
- `BP_LM_ADAPTIVE` (key 15) always visible

---

## Medium Priority

### 5. Access Settings Redesign ✅ DONE (v1)
- Replaced hardcoded input source dropdown with proper AccessMethod + SelectionMethod pickers
- Compatibility matrix matching Apple's design (see `docs/ACCESS_SETTINGS_REDESIGN.md`)
- `AccessMethod.cs` — Windows methods: Pointer, Touch, Eye Gaze, Joystick, Switches Only
- `SelectionMethod.cs` — All 9 selection methods with `FilterName` → `SP_INPUT_FILTER` mapping
- `AccessConfiguration.cs` — Persisted to `%APPDATA%\Dasher\access.json`, calls `dasher_set_string_parameter`
- Updated `FilterToSubgroup` map with all DasherCore filter names (Press Mode, One Button Dynamic Mode, etc.)

**Still TODO:**
- Switch capture UI (key capture per switch for switch-based methods)
- SwitchProfile model (up to 4 switches, scan rate)
- Activate eye gaze / joystick services based on selected AccessMethod

### 6. Game Mode UI ✅ DONE
- Game mode toggle button in toolbar (dice icon)
- Target text bar in sidebar with correct/wrong/remaining display
- Canvas text suppression via `dasher_game_set_canvas_text(0)`
- State synced each frame via `SyncGameModeState()`
- Uses `dasher_game_get_target/correct/wrong/length` APIs

### 7. Enum Dropdowns for Long Params ✅ VERIFIED
- `BuildEnum` correctly handles long params with enum values
- `BuildStringDropdown` handles string params with permitted values
- Apple's fix was about ensuring `uiType=Enum` long params render as dropdowns — our code already does this

### 8. Auto-Speed Toggle Wiring ✅ DONE
- `AutoSpeed` property wired to `BP_AUTO_SPEEDCONTROL` (key 14)
- Initialized from engine on startup
- Uses CommunityToolkit.Mvvm `OnAutoSpeedChanged` partial method

---

## Low Priority

### 11. TTS Settings
- Apple added TTS rate/pitch/volume controls, preview button, SherpaOnnx engine option
- We have SAPI `Quick Speak` but no settings for rate/pitch/volume
- Could use `SpeechSynthesizer` from `System.Speech` or Windows.Media.SpeechSynthesis

### 12. Font Picker
- Apple added dynamic key resolution for font string params (SP_DASHER_FONT)
- Uses `dasher_get_parameter_string_values()` to get available fonts
- We need to verify our `BuildTextField` handles string params with `uiType=dropdown`

### 13. Device-Specific Input Services
- Joystick/Gamepad via Windows.Gaming.Input
- Bluetooth socket input (Windows has `BTSocketInput` in v5)
- Tilt not relevant for Windows (no accelerometer)
- These are Phase 3 in Apple's plan too

---

## Already Done
- ✅ New groups from manifest (Customization, Input, Language, Output, Game Mode)
- ✅ Subgroup filtering by input filter (basic — see item 5 for upgrade)
- ✅ Locale picker in Language section
- ✅ Design tokens / colors / spacing / border radius
- ✅ Sidebar editor with Copy/Copy All/Paste/Quick Speak
- ✅ Eye gaze via WinRT GazeInputSourcePreview
- ✅ Speed control in bottom bar
- ✅ App icon
