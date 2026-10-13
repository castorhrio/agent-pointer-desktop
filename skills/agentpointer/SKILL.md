---
name: agentpointer
description: Show a click-through arrow on the Windows desktop when the user needs to locate a control or perform a manual step. Use it to point at a verified screen location or application window.
---

# Agent Pointer for Windows

Use a short label in the user's language to explain the requested manual action.
Pass it with `--text`, for example "Click Allow to continue installation". The fallback
label only asks the user to inspect a location; it cannot explain your intended action.
The arrow does not click or approve anything. Keep the task's existing authorization boundaries.

The installer substitutes the executable's absolute path into these PowerShell examples.
After moving or reinstalling the executable, run `agentpointer install-skill` again.

Inspect the environment when display geometry is unknown:

```powershell
__AGENTPOINTER_COMMAND__ doctor --json
```

Coordinates are **physical screen pixels**, with `(0,0)` at the primary display's
top-left. Secondary screens can have negative coordinates. Use verified coordinates
from a screenshot or another desktop tool; do not guess a button's location.
Logical coordinates must be converted using the relevant display's scale.
All display numbers in `doctor`, `--display`, and results start at **1**.
With `--at` or `--rect`, `--display N` makes the coordinates local to display N.
With `--window` or `--mouse`, it filters the globally resolved target to that display.

Prefer `start` so the agent can continue after the arrow appears:

```powershell
__AGENTPOINTER_COMMAND__ start --at 600,400 --text "Click Allow to continue" --duration 30 --json
__AGENTPOINTER_COMMAND__ start --window "Notepad" --text "Continue here" --duration 30 --json
```

`--window` identifies a window, not a button inside it. It preserves focus by default;
use `--raise` only when bringing that window forward is appropriate.
`point` waits until the arrow disappears (8 seconds by default).
`start` returns once its background overlay is ready (300 seconds by default).
`--dry-run --json` resolves and validates the target without displaying an arrow.

To isolate cleanup between agents, set `BIGARROW_OWNER_PID` to your actual long-lived
agent process PID before both `start` and `stop`; an existing `CLAUDE_PID` takes priority.
Do not use a short-lived shell PID as the owner. The arrow disappears when the owner exits.
Without an owner, default `stop` clears only unowned arrows in the Windows session.

```powershell
__AGENTPOINTER_COMMAND__ stop --json
```

`stop --all` clears every agent's arrow in the current Windows session; reserve it for
explicit global cleanup. `stop --hook` uses the same owner scope, requires an owner
environment variable, and stays quiet if there is no arrow. Do not edit hook settings
unless the user requests that integration.

Exit codes: `0` success, `2` invalid input, `3` target outside all screens or missing
window, `4` access denied / secure desktop, `5` unsupported feature, `6` internal error.
`elements` and `--element` return `5`; use `--at`, `--rect`, `--mouse`, or `--window`.
Treat `ineffective` entries in JSON as explicit limitations. `doctor` checks styles and
hit-test responses; it does not prove that a cross-process click reached a button.

Windows implementation of [bigarrow](https://github.com/franzenzenhofer/big-arrow-on-the-screen).
