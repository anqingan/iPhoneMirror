# Preview focus and terminal USB cleanup fixes

This branch builds on upstream `829c5919f25718efc91f3fae4ee1e808f193964d`.

After changing an independent preview's fullscreen state, defer a foreground-window check until pending input messages have drained. Notify the input owner using the actual foreground HWND, including when Windows does not emit another activation message. Ignore the deferred callback after the preview is disposed. The capture and renderer lifetimes are preserved.

When direct USB HID recovery is exhausted, keep the device busy until the old bridge has finished cleanup. Publish the retryable failure only after that cleanup. A user cancellation that joins the same cleanup suppresses the later failure prompt and leaves control cancelled. Shutdown and repeated stops share the existing single-flight cleanup operation.

The keyboard focus fixture also supplies the new `readOnly` constructor parameter when creating its isolated device-binding store.

## Focused validation

Build the runtime test project in Release using the repository's prepared bridge and native payloads. Execute its test assembly directly with `dotnet`, as described in `DEVELOPMENT.md`:

```powershell
dotnet <runtime-test-assembly> --ipad-control-regressions
dotnet <runtime-test-assembly> --keyboard-focus
```

The recovery regression covers fullscreen entry/exit, foreground changes before a deferred callback, disposal before the callback, cleanup before retry, and cancellation through the production cancel entry point. Device input is not sent by these tests.

Release compilation and the focused recovery regression passed locally. The new regression currently has an explicit command-line entry; default-suite integration, the complete repository Release build, and real-device validation remain pending. Existing foreground-keyboard isolation and text clipboard support come from upstream and are not introduced by this branch.
