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

## Test the installed bridge

Install the x64 MSI, launch Dasher from the Start menu, and enter Direct
mode. Dasher activates its TSF profile for the supported English (US and UK)
keyboard languages and restores the prior profile on exit when possible.
The DLL uses a hidden window on the editor thread to receive edit requests.

1. Open a scratch document in a non-elevated Notepad window.
2. Use Direct mode to insert and delete text.
3. Minimize Dasher, move the caret in Notepad, restore Dasher, and type at
   the new position.

The automated tests exercise the text context and the hidden window's
activation and teardown. The MSI and the editor integration require an
interactive Windows session. Keyboard diagnostics and the
dasher-tsf-probe.log file record operation results without document text.
