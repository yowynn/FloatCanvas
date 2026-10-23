# Fullscreen relative-input fixture

Self-owned native Win32/D3D11/DXGI input client. This combines the existing fullscreen probe's swap-chain path with the relative receiver's game-like input behavior. It does not inspect FloatCanvas, inject input, change target processes, elevate, modify compatibility settings or adjust behavior when an overlay appears.

```powershell
./scripts/Build-FullscreenInputFixture.ps1 -OutputDirectory <external-output>
& <external-output>/FullscreenInputFixture.exe
# Request the traditional path only for this child process, then measure with PresentMon.
./scripts/Start-FullscreenInputFixture.ps1 -ExecutablePath <external-output>/FullscreenInputFixture.exe -TraditionalExclusive
```

The fixture starts windowed with confinement disabled. Choose a controlled case with the plain function keys while FloatCanvas editing is off:

| Key | Requested display | Pointer confinement |
| --- | --- | --- |
| F5 | Windowed | Off |
| F6 | Windowed | On; Alt temporarily releases |
| F7 | DXGI fullscreen | Off |
| F8 | DXGI fullscreen | On; Alt temporarily releases |

Every case uses the same device, raw-input registration and keyboard/mouse handling. The native log includes a case ID/name alongside actual `GetFullscreenState`, request HRESULT, resolution, foreground, own elevation/integrity, Alt transitions and camera counters. A failed fullscreen request is visibly labelled and is not an accepted exclusive test. Actual deactivation releases confinement and suspends the exclusive request; reactivation reapplies the selected fullscreen case. This lifecycle is explicit host behavior, not an injected overlay fix or an attempt to emulate a passing test by minimizing on a timer.

F9 retries the current fullscreen request. Esc restores an unlocked window and a second Esc exits. Ctrl+Shift+F12 is a registered emergency shortcut that closes only this host, including while another overlay owns input; its registration must succeed at startup. Alt+F4 also closes normally. Interactive launch has no timeout. `--seconds N` is only for bounded unattended smoke runs.

In locked cases while foreground, the fixture repeatedly confines and warps the pointer to its client center. Received Alt down releases it; received Alt up restores the lock. Keyboard and mouse raw devices register with flags zero, preserving normal window messages. Raw relative movement changes the camera counters only while foreground and with neither received Alt state held, in all four cases. Alt edge counts exclude repeats. The fixture does not use asynchronous physical state to repair a missing received release; that state is diagnostic only. Actual deactivation clears its own received modifier state and releases confinement. Late background raw packets are counted but do not restore stale modifier state. This models a game behavior; it is not evidence of Genshin's implementation.

Test production R9 with all keyboard/avatar-entry and keyboard/toolbar-exit combinations, including holding modifiers briefly before releasing. Observe whether editing waits for release, the pointer moves freely, raw camera/input stops during editing, and lock/camera response returns after exit without another Alt press. Logs are written beside the executable under `logs/` at 250 ms plus state transitions. The fixture never reads page content or global key history.

`scripts/Watch-FullscreenInputFixture.ps1` is a separate read-only recorder, bound to verified fixture and overlay executable paths/PIDs. It can start the existing PresentMon executable for this fixture PID and samples the real FloatCanvas native input-window visibility, own UI focus/capture and foreground PID. It does not alter either program or replay input. Visibility is not exposed as a native delegation ACK or End HRESULT. The renderer/input client remains unaware of FloatCanvas. When the fixture exits, the recorder cooperatively stops its unique owned ETW session and writes a presentation summary, including an explicit zero-frame result if no frame capture was established.

If external UI is invisible during a Legacy Flip interval, record that presentation failure; do not ask the user to judge visible editing or avatar-entry timing in that state. Input ownership can be measured separately, but it cannot establish a passed visual-editor or mouse-button-exit test. First compare the actual target's foreground presentation with the fixture. A failure only in F8 demonstrates the combination can trigger it in this implementation. Four passes do not by themselves prove Genshin-specific code: compare actual presentation mode, integrity and input APIs before making that attribution.

`GetFullscreenState(TRUE)` establishes DXGI's fullscreen state, not a proven hardware presentation mode. Windows Fullscreen Optimizations and composition can affect the latter; strict legacy exclusive presentation requires separate PresentMon/ETW evidence. No registry or FSO setting is changed here. A passing ordinary-permission fixture does not establish behavior against a higher-integrity game.

The optional launcher sets `__COMPAT_LAYER=DISABLEDXMAXIMIZEDWINDOWEDMODE` in the child's environment only, matching the prior controlled demo's measured setup. It preserves any inherited layer tokens. This is a compatibility request, not proof that it took effect; verify a foreground interval with PresentMon `Hardware: Legacy Flip` and correlate that same interval with input/exit observations. Opening system UI or an external overlay may change the path. The green marker is driven by raw camera movement; it is not a progress bar or a fullscreen classifier.

On the current machine, a completed capture with that request includes 14,990 Hardware Legacy Flip frames (plus composed/Other phases). The user reported that FloatCanvas's avatar and other external UI were not visible above the fixture, preventing avatar-based editing acceptance. This is a negative presentation result for the current R9 external-window path, not a successful strict-fullscreen editor test. Do not infer compatibility from the ordinary receiver's successful input tests. If PresentMon does not exit with the target, stop only its verified, dedicated named session through its CLI before reading the CSV.

API references: [SetFullscreenState](https://learn.microsoft.com/en-us/windows/win32/api/dxgi/nf-dxgi-idxgiswapchain-setfullscreenstate), [GetAsyncKeyState return-value restrictions](https://learn.microsoft.com/en-us/windows/win32/api/winuser/nf-winuser-getasynckeystate).

Current-machine calibration (2026-10-23): Genshin's observed before/during/after editor intervals were `Composed: Copy with GPU GDI`, with game foreground preserved. The same fixture launched without `-TraditionalExclusive` retained DXGI fullscreen at 3840×2160, but its corresponding stable intervals were `Composed: Flip`. Removing the forced legacy launch condition therefore restores a composed fullscreen test, not an identical Genshin client or a physical Alt-handoff acceptance. The recorder uses raw QPC timestamps for phase correlation. See `Docs/Research/System-Overlay.md` for the measured boundaries and limitations.
