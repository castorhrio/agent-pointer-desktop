---
name: agentpointer
description: "Point at something on the human's Windows screen with a big arrow and a sign (\"Click Allow to continue\") using the agentpointer CLI. Use whenever the human has to look at, click, type into or choose a specific spot, button, field, window, tab or menu: show me where, point to it, which button, where do I click, guide me through this UI, the human must approve or sign something, or you need the human's hand and they may not be looking at the terminal. Mandatory when it is time-critical: a code that expires, a login or payment page that times out, a build waiting on the human."
---

# Agent Pointer for Windows

`agentpointer` draws a click-through arrow with a sign on the Windows desktop. It never takes the
focus, needs no permission, and never clicks, types or approves anything. Point at the thing itself;
a line in the terminal is easy to miss. Keep the task's existing authorization boundaries.

The installer substitutes the executable's absolute path for `__AGENTPOINTER_COMMAND__`. After moving
or reinstalling the executable, run `agentpointer install-skill` again: missing files are always
written and a skill whose only difference is that path is updated, both without `--force`. `--force`
overwrites skill files you edited yourself, so use it deliberately.

```powershell
__AGENTPOINTER_COMMAND__ doctor --json
```

## Rules

- Open the window yourself (`--window` / `--app` raise it by default), then point at the one control
  left for the human. Never walk them through menus. The human clicks; you point.
- The sign is one full sentence naming the action ("Click Save", never "here"); when the click
  approves, grants, pays, signs, sends or deletes, add what happens: "Click Pay: 49 EUR to Hetzner".
  The facts go on the sign, not only in the terminal. Without `--text` the label only says where to look.
- `--say` when the human may not be looking, `--notify` to add a Windows notification. Time-critical
  (code expires, page times out, job waits): say by when, "Enter the 2FA code now, it expires in 60 s".
- Something to paste (a command you may not run, a URL, a code): put it on the sign as `{{value}}`.
  The braces are not drawn; one click copies all of it and the arrow stays. Say where it goes:
  "Copy {{482913}} and paste it here", then check that it was pasted.
- Prefer `start` (returns once the arrow is up) over `point` (waits until it ends). Defaults: `point`
  8 s, `start` 300 s, `--duration 0` = no limit.
- One arrow per step; `stop` the moment the human acted.
- A click on the sign or the shaft removes the arrow (target clicks pass through). A removed arrow was
  seen: check the result, do not redraw. Use `--close-button` only if a refusal button was asked for.
  `--until-click` ends the arrow when the human clicks the target, `--until-any-click` on any click;
  both leave the click reach the app.

## Targets

| You know | Use |
|---|---|
| Global physical pixels | `--at X,Y` |
| A rectangle in physical pixels | `--rect X,Y,W,H` (points at its centre) |
| macOS logical points | `--at` / `--rect` with `--logical` (96 DPI, relative to `--display`) |
| A label in a native app | `--element "Allow" --app "System Settings"`, plus `--role button` for a type |
| An app, window or tab | `--window "Notepad"` / `--app "Google Chrome:Acme sign-in"` |

`(0,0)` is the primary display's top-left, secondary screens can have negative coordinates, and
display numbers start at 1 in `doctor`, `--display` and JSON. With `--at` / `--rect`, `--display N`
makes the coordinates local to that display; `--mouse` takes the current cursor position. `--logical`
uses that one display's scale (96 DPI relative to `--display`, default primary), not upstream's global
logical point space: on a mixed-DPI desktop convert each display yourself and do not reuse macOS
coordinates. Never guess:
verify with `doctor --json`, `elements --app X --match "file" --json` or `--dry-run --json` instead of
inventing coordinates. `--dry-run` draws nothing and changes no focus.

`--app` is an alias for `--window`: it matches the process name, then the executable's file
description, then the window title; `App:title` matches a window title. Friendly macOS names are
mapped (`--app "Google Chrome"` matches `chrome`, `--app Finder` matches `explorer`). Window and
element targets are raised unless `--no-raise` is given; `--at` / `--rect` / `--mouse` never change
the foreground window. `--element` uses UI Automation: native apps and window chrome work, browser
page content needs Chrome started with `--force-renderer-accessibility`.

```powershell
__AGENTPOINTER_COMMAND__ start --app notepad --element "File" --role menuitem --text "Click File to continue" --duration 30 --json
__AGENTPOINTER_COMMAND__ start --at 600,400 --text "Click Allow to continue" --duration 30 --json
__AGENTPOINTER_COMMAND__ stop --json
```

## Ownership

Set `BIGARROW_OWNER_PID` to your real long-lived agent PID before both `start` and `stop`; an existing
`CLAUDE_PID` takes priority. Never use a short-lived shell PID: the arrow dies with its owner. Without
an owner, `stop` clears only unowned arrows; `stop --all` clears every arrow in the session (global
cleanup only). Clear leftover arrows the moment the human sends a new prompt:

```json
{"hooks": {"UserPromptSubmit": [{"hooks": [{"type": "command", "command": "agentpointer stop --hook"}]}]}}
```

`stop --hook` needs an owner (`CLAUDE_PID` / `BIGARROW_OWNER_PID`) and cannot be combined with
`--all`; anything else exits 2. It reads no stdin, is silent, never writes stdout and exits 0. Edit
hook settings only when the user asks for that integration.

## Errors

0 success, 2 invalid input, 3 target not found or outside all displays, 4 access denied / locked or
UAC secure desktop, 5 unsupported feature, 6 internal error / background startup failure. On 2 fix
the command; on 3 re-resolve the target; on 4 tell the human (the overlay cannot draw on the secure
desktop); on 5 drop that option and retry without it (`--follow`, `--png`). `doctor` checks styles
and hit-test responses; it does not prove that a cross-process click reached a button.

## Not supported on Windows

`--follow` and `--png` are not supported on Windows yet and exit 5: drop the option and retry.
`--edge-color` is accepted but ineffective (JSON reports it under `ineffective`);
`--style ring|box`, `--shape zigzag|spiral` and other anchors fall back to
`arrow` / `bend` / `center|title`; `--peekaboo` / `--snapshot` are macOS-only and exit 2.

Windows implementation of [bigarrow](https://github.com/franzenzenhofer/big-arrow-on-the-screen).
