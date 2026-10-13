**English** | [简体中文](README.zh-CN.md)

# Agent Pointer for Windows

**A Windows desktop port of bigarrow: when an AI task needs your hand, show exactly where to act.**

When a task needs an approval, a verification code, or a step inside another app,
Claude Code / Codex can display an arrow with an action label. You decide and complete the action.
This unofficial port adapts layout algorithms and some CLI conventions from
[bigarrow](https://github.com/franzenzenhofer/big-arrow-on-the-screen), with a C# / WinForms / WPF overlay and desktop setup panel.

[Download for Windows x64](https://github.com/castorhrio/agent-pointer-desktop/releases/latest/download/agentpointer-0.1.0-win-x64.zip) · [All releases](https://github.com/castorhrio/agent-pointer-desktop/releases) · [Report an issue](https://github.com/castorhrio/agent-pointer-desktop/issues)

![Desktop app](docs/desktop-preview.png)

## Status

`v0.1.0` — first public release, with a self-contained Windows x64 download.

Verified with the actual package: installation into an isolated directory, visible desktop startup,
a real click through the demo, closing to the tray, and reopening a single instance. Separate-process
checks cover click-through, prompt return from `start`, owner isolation, display conversion, and exit codes.
Claude Code / Codex skill file status can be checked; complete tasks in a live agent session still need user validation.

## Features

| Feature | Behavior |
|---|---|
| On-screen guidance | An arrow and action label show where to complete the next step |
| Local demo | Try the guidance and completion flow before setting up an agent |
| Agent setup | Install Claude Code / Codex skills and check that files match the current app |
| Focus preservation | Guidance keeps your current input focus; explicit `--raise` requests foreground activation |
| Click-through | Mouse clicks reach the app beneath the overlay, verified across processes |
| Multiple agents | Stop guidance by owner; overlays disappear when their owner exits |
| Desktop and CLI | The panel handles setup and demos; agents can call the CLI independently |
| Two interface languages | Switch between English and Simplified Chinese |

## Install

Requires **Windows 10/11 x64**. Download from
[GitHub Releases](https://github.com/castorhrio/agent-pointer-desktop/releases/latest) and extract the entire archive.
No .NET installation or development tools are required on the target computer.

| Download | Purpose |
|---|---|
| [agentpointer-0.1.0-win-x64.zip](https://github.com/castorhrio/agent-pointer-desktop/releases/latest/download/agentpointer-0.1.0-win-x64.zip) | Complete desktop app for installation or portable use |
| [SHA-256 checksum](https://github.com/castorhrio/agent-pointer-desktop/releases/latest/download/agentpointer-0.1.0-win-x64.sha256) | Verify that the ZIP download is intact |

**Option A — install.** After extracting, double-click `Install.cmd`. It installs to
`%LOCALAPPDATA%\Programs\AgentPointer`, adds the user PATH and a Start-menu shortcut, and opens the desktop panel.
Choose whether to install agent skills in the panel; the installer leaves skills untouched by default.
No administrator rights are required. Restart open terminals to pick up the new PATH.

**Option B — portable.** After extracting, double-click `AgentPointer.Desktop.exe`.
Keep the entire extracted directory together: the app needs its adjacent CLI and runtime files.

The download includes:

- `AgentPointer.Desktop.exe`: desktop setup, demo, and tray entry point.
- `agentpointer.exe`: the guidance tool for agents, with embedded skills and Codex metadata.
- The self-contained runtime, installation scripts, both README languages, and license notices.

You can also install from PowerShell in the extracted directory:

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File .\Install.ps1
```

`Install.ps1 -NoPath -NoShortcut -NoLaunch` skips PATH changes, shortcuts, and launch.
Use `-Destination <directory>` to choose a location, or `-InstallSkills` to install skills during a headless setup.
`AgentPointer.Desktop.exe --background` starts only the tray app.

The package is not Authenticode signed; Windows may show a SmartScreen warning after download.
**Check for updates** compares the latest release in this repository and provides its page link when a newer version is available.
To update, download the package, exit the running app, then extract it or run the installer again.

To uninstall, remove the installation directory, its user PATH entry, the Agent Pointer Start-menu shortcut,
and the two agentpointer skill directories if you installed them.

## First run

1. Open **Agent Pointer** and click **Try the demo**. The arrow points at a demo button; clicking the completion button ends the demo.
2. Click **Install / update agent skills** to configure the usage instructions for Claude Code and Codex.
3. Restart your agent, click **Copy a test prompt**, and paste it into a new agent session.
4. Continue describing your task in the agent conversation, asking it to show guidance when a step needs your action.

The desktop demo is local. Its button only ends the demo; it does not approve or execute a real task.
“Skill installed” means the files are configured, without implying a running agent or a live connection.
The app does not read or monitor conversations. Actual guidance starts when an agent calls the tool.

Closing the window keeps the app in the tray. Opening it again returns to the same instance; use the tray menu to exit.
The CLI works independently, so ordinary guidance does not require the desktop panel to stay open.

## Claude Code / Codex integration

The desktop action uses the same installation path as these commands:

```powershell
agentpointer install-skill --dry-run --json
agentpointer install-skill --json
```

The skill comes from [skills/agentpointer/SKILL.md](skills/agentpointer/SKILL.md), with
`agents/openai.yaml` for Codex display metadata. Both are embedded in the CLI and need no separate download.
The default installation directories are:

- `%USERPROFILE%\.claude\skills\agentpointer\`
- `%USERPROFILE%\.agents\skills\agentpointer\`

Installation updates only the files inside these two tool-specific directories, leaving other agent settings and hooks intact.
It writes the current CLI's absolute path so portable use can work without PATH; reinstall the skills after moving the app.
`--dry-run --json` reports whether each agent's skill exists and matches the current program version and path.
The desktop uses this for “Not installed”, “Skill installed”, and “Update needed”; these describe file status.

The skill explains coordinates, non-blocking calls, action text, owner isolation, and limitations.
For a real task, include a complete action such as “Click Allow to let this app access your files”.
The default label when `--text` is omitted only identifies a location; it cannot explain the task.

## Commands

| Command | Behavior |
|---|---|
| `point` | Show guidance and wait until it ends; default 8 seconds |
| `start` | Return after the overlay appears; default 300 seconds, `--duration 0` has no time limit |
| `stop` | Stop only the same owner's overlays; without an owner, stop only unowned overlays |
| `stop --all` | Clear all overlays in the current Windows session |
| `stop --hook` | Require an owner environment variable and clear that owner's overlays; quiet if none exist |
| `doctor` | Report displays, DPI, desktop availability, overlay styles, and hit testing |
| `install-skill` | Install skill files for both agents |
| `elements` | Currently unsupported; exit code 5 |

Targets are mutually exclusive: `--at X,Y`, `--rect X,Y,W,H`, `--mouse`, or `--window App[:title]`.
`--app` is an alias for `--window`. Windows are matched by process name. By default, matching preserves focus;
`--raise` explicitly requests foreground activation, and `--no-raise` takes precedence.
`--dry-run` draws nothing and never changes focus.

Appearance options include `--text`, `--from`, `--color`, `--size S|M|L`, `--shape bend|straight`,
`--corners round|sharp`, `--border shadow|white-black|black`, `--border-color`, and `--text-color`.
`--no-animation` disables entrance, breathing, and fade animations. See `agentpointer --help` for all options.

## Coordinates and displays

All input and output coordinates use **physical pixels**. `(0,0)` is the primary display's top-left corner;
negative coordinates are valid. Display numbering starts at **1** in `doctor`, `--display`, text output, and JSON.

- With `--display N`, `--at` / `--rect` are relative to that display's top-left corner. Conversion happens before display selection and validation.
- `--window` / `--mouse` already produce global coordinates. `--display N` filters their target display without translating again.
- Targets outside every display, including gaps between displays, return exit code 3. `--dry-run` performs the same validation.
- An invalid display number returns exit code 2.

Upstream macOS uses logical points, so its coordinates cannot be reused directly. At 150% scaling,
multiply display-local logical coordinates by `1.5`, then add that display's global physical origin.
Resized screenshots also require conversion by their image scale. PerMonitorV2 keeps rendering and window
positioning in physical pixels; it does not convert logical coordinates supplied by the caller.

## Lifecycle and exit codes

`CLAUDE_PID` takes precedence over `BIGARROW_OWNER_PID`. Set it to the actual, long-lived agent PID
and keep it consistent across `start`, `point`, and `stop`. Overlays disappear when their owner exits.
Avoid binding an overlay to a temporary PowerShell process that runs only one command.

`stop --hook` requires an explicit owner. Calls without an owner belong to the unowned group;
set an owner when multiple agents need isolation.

| Exit code | Meaning |
|---|---|
| 0 | Success |
| 2 | Invalid arguments |
| 3 | Target not found / outside displays |
| 4 | Insufficient permissions / locked or UAC secure desktop |
| 5 | Unsupported feature |
| 6 | Internal error / background startup failure |

`--json` returns structured results. Internal exceptions are reported separately from permission failures.
Background startup has a 5-second limit; failure cleans up the child and returns an error.

## Limitations

Some option names, defaults, and visual rules follow bigarrow. Compatibility is partial:

- `elements` / `--element` are not yet connected to Windows UI Automation.
- `--follow`, `--until-click`, `--until-any-click`, `--close-button`, `--say`, `--voice`,
  `--png`, and edge/close color options are parsed only. JSON lists them in `ineffective`; text output warns about them.
- `ring` / `box` fall back to `arrow`, and `zigzag` / `spiral` fall back to `bend`. Window anchors currently support only `center` / `title`.
  These free upstream styles are pending implementation. The project has no delivered paid features.
- `--peekaboo` / `--snapshot` are macOS-specific and return exit code 2 on Windows.
- Guidance cannot be drawn on the lock screen or UAC secure desktop and may be hidden by exclusive fullscreen apps.

## Diagnostics and development

The overlay uses `UpdateLayeredWindow` for per-pixel transparency and combines
`WS_EX_LAYERED | WS_EX_TRANSPARENT` to pass mouse input to windows in other processes.
`HTTRANSPARENT` supplements same-thread hit testing; its return value alone cannot prove click delivery.
See [Microsoft's layered window documentation](https://learn.microsoft.com/en-us/windows/win32/winmsg/window-features#layered-windows).

`doctor` reports `ConfigurationValid` and `ClickDelivery: "not-tested"`. It checks styles and hit-test values.
Before release, click a button beneath the overlay in a separate test process to verify actual delivery.

Console output follows the host encoding; background processes without a console use UTF-8.
This avoids PowerShell 5 pipe decoding problems after a temporary code-page change. Text files use UTF-8.

Development requires Windows and the [.NET 10 SDK](https://dotnet.microsoft.com/download/dotnet/10.0):

```powershell
dotnet run --project src/AgentPointer.Cli -- start --at 600,400 --text "Click here to continue"
dotnet run --project src/AgentPointer.Tray
dotnet test tests/AgentPointer.Core.Tests/AgentPointer.Core.Tests.csproj
dotnet run --project tests/AgentPointer.WindowsChecks -- src/AgentPointer.Cli/bin/Debug/net10.0-windows/agentpointer.exe docs/overlay-preview.png
dotnet run --project tests/AgentPointer.WindowsChecks -- --desktop src/AgentPointer.Tray/bin/Debug/net10.0-windows/AgentPointer.Desktop.exe docs/desktop-preview.png
```

CLI/Core use .NET 10; the CLI uses WinForms and the tray app uses WPF. Update checks report “Up to date”
only after retrieving and comparing a valid version. HTTP and version-data errors report a failed check.

Build a release package containing the desktop app, CLI, runtime, installation entry points,
both README languages, and a SHA-256 checksum:

```powershell
powershell -NoProfile -File .\scripts\Publish.ps1
```

Output goes into `artifacts/`. `-Runtime win-arm64` builds an ARM64 package from source;
the current public download and actual runtime verification cover x64.

`WindowsChecks` opens a separate test process and sends real mouse input through an opaque part of the
guidance card. It checks click delivery, transparency, focus, background return and pipe closure,
owner/stop/hook isolation, display coordinates, and exit codes, then saves an actual window screenshot.
It briefly moves the mouse and restores its position and the foreground window afterward.
Desktop checks cover visible startup, component and skill states, demo interaction and completion,
closing to the tray, and reopening a single instance. These checks do not install skills or change user PATH or shortcuts.

## Attribution and license

Sizing and color rules are adapted from upstream `ArrowAppearance.swift`; placement scoring and avoidance
are adapted from `Placement.swift`. The implementation includes adapted upstream algorithms and does not
promise pixel-identical rendering between Windows and macOS.

MIT. See [LICENSE](LICENSE) for this project's license and
[THIRD_PARTY_NOTICES.md](THIRD_PARTY_NOTICES.md) for upstream copyright and the full license.
Both notices are included with builds and release packages.
