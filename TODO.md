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

### 1. `dasher_reset()` on New Button
- Currently `OnNew` only clears the output text box
- Should call `dasher_reset()` to also reset the language model state
- Location: `MainWindow.axaml.cs` → `OnNew`

### 2. Output Callback (`dasher_set_output_callback`)
- Real-time output/delete events for Direct Mode (keyboard mode)
- Event types: 0 = text output, 1 = text delete
- Fires on the thread calling `dasher_frame()`
- Needed for: keyboard mode text injection via `SendInput`
- Apple uses this for Direct Mode where text goes into other apps

### 3. Message Callback (`dasher_set_message_callback`)
- Engine warnings/errors displayed as native UI (toast/banner)
- Event types: 0 = informational (non-modal), 1 = warning (modal)
- Apple shows a non-blocking `MessageBanner` that auto-dismisses after 5 seconds
- We should show an Avalonia toast/notification

### 4. LM-Aware Settings Filtering
- Apple filters Language section params based on active language model via LMRegistry
- PPM shows alpha/beta/max_order, Word shows word_alpha/max_order, Mixture shows all, CTW shows max_order only
- `BP_LM_ADAPTIVE` toggle always visible regardless of model
- Uses `dasher_get_language_model_param_count/key()` to get relevant params for active model
- Our SettingsPanel currently shows all Language params unfiltered

---

## Medium Priority

### 5. Access Settings Redesign (Input System Overhaul)

**Current approach**: We have a hardcoded `FilterToSubgroup` dictionary that maps input filter names to C++ class subgroup names. This is a v1 shortcut.

**Apple's approach**: Split input into two orthogonal concerns:
- **AccessMethod** (steering): Pointer, Touch, Eye Gaze, Tilt, Joystick, Hand Tracking, Switches Only
- **SelectionMethod** (confirmation): Continuous, Press to Move, Click to Zoom, Dwell, 1 Switch, 2 Switches, 2 Push, Scanning, Direct Boxes

These are separate single-choice pickers with a compatibility matrix. See `docs/ACCESS_SETTINGS_REDESIGN.md` and `docs/INPUT_SYSTEM.md` in Dasher-Apple.

**Windows-specific AccessMethods** (from Apple's matrix):
| Method | Valid Selections |
|--------|-----------------|
| Pointer (mouse/trackpad) | All selections |
| Eye Gaze | Continuous, Dwell, 1 Switch, 2 Switches, Scanning, Direct Boxes |
| Joystick/Gamepad | Continuous, Press to Move, Click to Zoom, 1 Switch, 2 Switches, Scanning, Direct Boxes |
| Switches Only | 1 Switch, 2 Switches, 2 Push, Scanning, Direct Boxes |

**SelectionMethod.filterName** maps to `SP_INPUT_FILTER` string value:
| Selection | SP_INPUT_FILTER |
|-----------|----------------|
| Continuous | Normal Control |
| Press to Move | Press Mode |
| Click to Zoom | Click Mode |
| Dwell | Normal Control (+ BP_STOP_OUTSIDE) |
| 1 Switch | One Button Dynamic Mode |
| 2 Switches | Two Button Dynamic Mode |
| 2 Push | Two Push Dynamic Mode |
| Scanning | Menu Mode |
| Direct Boxes | Direct Mode |

**Files to create** (C# equivalents):
1. `AccessMethod.cs` — enum with displayName, subtitle, validPlatforms, hasContinuousInput
2. `SelectionMethod.cs` — enum with displayName, filterName, isSwitchBased, compatibility matrix
3. `SwitchProfile.cs` — SwitchSlot + SwitchProfile models
4. `AccessConfiguration.cs` — persisted config (method + selection + switchProfile)
5. `AccessSettingsView.axaml` — Steering picker → Selection picker → Switch setup → Method settings
6. `SwitchCaptureView.axaml` — "Press key or switch now..." key capture UI

**Files to modify**:
1. `SettingsPanel.cs` — replace Input Source dropdown with AccessSettingsView
2. `MainWindow.axaml.cs` — activate input services based on AccessConfiguration

**Subgroup filtering note**: Apple's `FilterToSubgroup` mapping is used to show/hide advanced Input params based on the active filter. Each `SelectionMethod.filterName` maps to a set of C++ subgroup class names. Our current dictionary covers this but needs updating:
- `"Normal Control"` → `["CDefaultFilter", "CDynamicFilter", "CDynamicButtons"]`
- `"Click Mode"` → `["CDefaultFilter", "CClickFilter"]`
- `"Press Mode"` → `["CDefaultFilter", "CPressFilter"]` (missing from our map)
- `"One Button Dynamic Mode"` → `["CDefaultFilter", "COneButtonDynamicFilter"]` (different key name)
- etc.

When we build the Access Settings UI, the `SelectionMethod.filterName` → `FilterToSubgroup` lookup replaces the raw SP_INPUT_FILTER string matching.

### 6. Game Mode UI
- Game mode toggle in toolbar
- Target text bar with correct (green) / wrong (red) / remaining (gray) text in output pane
- Game text file picker in settings (Game Mode category)
- Canvas text suppression (`dasher_game_set_canvas_text(0)`) since we render native UI
- Apple implementation: `bc9dfde` (iOS), `f22fd62` (macOS)

### 7. Enum Dropdowns for Long Params
- Apple fixed long params with `uiType=dropdown` (e.g. Language Model, Node Shape) to render as Picker dropdowns
- Our `BuildEnum` should handle this but needs verification
- Also: string params with `uiType=dropdown` should use `dasher_get_parameter_string_values()` for the dropdown list

### 8. Auto-Speed Toggle Wiring
- We have an `AutoSpeed` checkbox in bottom bar bound to ViewModel but not connected to `BP_AUTO_SPEEDCONTROL`
- Need to wire the checkbox to `dasher_set_bool_parameter`

### 9. Bottom Bar Cleanup
- Apple removed palette picker and font size stepper from bottom bars (moved to settings only)
- We should consider: keep palette in bottom bar (it's useful for quick switching) or move to settings only?
- Speed control and auto-speed are the main bottom bar items per Apple

### 10. Training Data Update
- DasherCore replaced char-frequency training with real AAC conversational text
- 34 languages, ~30k utterances each
- This comes with the submodule update automatically

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
