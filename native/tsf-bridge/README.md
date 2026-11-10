# TSF text context prototype

This standalone C++ library is the edit-session layer of a Windows input method.
It has no Dasher dependencies. A TSF host supplies its client ID and current
editable context on the owning COM apartment. A host should clear or replace
the context on every document focus transition, including loss of focus.

- Read returns the selection and up to 512 UTF-16 units on either side.
  Selections larger than 512 units return a buffer overflow result.
- Replace takes an offset into the combined snapshot, the expected text, and
  the replacement. It compares the current selection and surrounding text,
  checks the exact destination range, then calls SetText in the same write
  session. The edit attempt consumes the token. Read again for the next edit.
- S_FALSE means the token, current snapshot, or expected range did not match.
  TSF supplies HRESULTs for unavailable sessions, locks, and provider failures.

Use synchronous requests only from an appropriate TSF host callback. TSF can
deny a synchronous lock, so the host must propagate failure and arrange any
retry or asynchronous request at the appropriate boundary. The snapshot check
covers the visible window and current selection. A host that needs strict
revision invalidation should also invalidate on text-edit notifications.

## Build and test

From a Visual Studio developer shell with the Windows SDK and Ninja:

```powershell
cmake -S native/tsf-bridge -B build/tsf-bridge -G Ninja
cmake --build build/tsf-bridge
ctest --test-dir build/tsf-bridge --output-on-failure
```

The test executable creates a real TSF thread manager and an in-memory document
context. It verifies insertion, replacement of the second of two matching words,
deletion, a context change, and UTF-16 range validation.

## Try the standalone probe

The build also produces `tsf_probe.dll`, an experimental Windows input method
with a small non-activating panel. The panel runs on the editor's TSF thread.
It uses asynchronous edit sessions and invalidates pending operations when the
editor context changes. It reads document text only when you click **Read** or
perform a test edit. It displays the preview locally and writes no document log.

Use the x64 build with a 64-bit editor. The profile is registered under English
(United States). From an elevated 64-bit PowerShell, copy the DLL to a stable,
administrator-writable location and register it:

```powershell
$probeDir = Join-Path $env:ProgramFiles 'Dasher TSF Probe'
New-Item -ItemType Directory -Force $probeDir | Out-Null
Copy-Item 'C:\path\to\tsf_probe.dll' $probeDir
& "$env:WINDIR\System32\regsvr32.exe" (Join-Path $probeDir 'tsf_probe.dll')
```

1. Open a scratch document in Notepad and type a short sentence. Leave a
   collapsed caret somewhere in the sentence.
2. With Notepad focused, use **Win+Space** to select **Dasher TSF Probe**.
   If the profile is missing, check that English (United States) or English (United Kingdom) is installed
   and look for the probe in that language's keyboard settings.
3. Click **Read** and verify the before/selection/after display. Click
   **Insert test word** and check that `[dasher-test]` appeared at the caret.
4. Click **Minimize for 2s**. The panel should return without activating.
   Click **Undo test insert**. Verify the original text is intact.
5. Insert again, move the caret to a different position, then click Undo.
   The edit should be rejected. Repeat in the editor you want to validate.

The undo action applies to the last verified insertion in the current editor.
Read, focus loss, or another insertion invalidates it. After switching back
to your normal input method, close the test editors and unregister the probe
from elevated PowerShell:

```powershell
& "$env:WINDIR\System32\regsvr32.exe" /u (Join-Path $probeDir 'tsf_probe.dll')
```

The automated probe test loads the DLL through its COM factory and exercises
service activation, creation of the non-activating panel, and deactivation.
The bridge tests also exercise asynchronous reads, edits, and invalidation.
Installing and selecting the profile and testing editor compatibility require
an interactive Windows session.

## Exercise the Dasher connection

Rebuild and register the updated DLL, then restart the scratch editor so it
loads that DLL. Select **Dasher TSF Probe** in the editor with **Win+Space** and click **Allow Dasher in this editor** on the panel.
Set `$env:DASHER_EXPERIMENTAL_TSF = '1'` before launching the matching Dasher
build. Enter Direct mode and focus the scratch editor. Zoom in to insert text,
then zoom back to delete it. Use an editor running without elevation.

This opt-in path covers engine inserts, deletes, and context reads. Toolbar actions still use the existing implementation. Dasher stops the queue after a rejected edit or timeout and resumes after a fresh TSF context read. Check the keyboard diagnostics for a TSF output stop and the
probe log in the Windows temporary directory for the edit result.
