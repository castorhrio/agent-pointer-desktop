[English](README.md) | **简体中文**

# Agent Pointer for Windows

**bigarrow 的 Windows 桌面版：当 AI 做到需要你动手的那一步，直接在屏幕上指给你看。**

例如任务需要你批准授权、输入验证码，或完成软件中的一个步骤：
Claude Code / Codex 发起指引，箭头指出位置并说明该做什么，你来决定并完成操作。
产品基于 [bigarrow](https://github.com/franzenzenhofer/big-arrow-on-the-screen) 的布局算法与部分 CLI 约定。
这是非官方 Windows 移植，使用 C# / WinForms / WPF 实现屏幕覆盖层和桌面接入界面。

[下载 Windows x64](https://github.com/castorhrio/agent-pointer-desktop/releases/latest/download/agentpointer-0.1.0-win-x64.zip) · [全部版本](https://github.com/castorhrio/agent-pointer-desktop/releases) · [反馈问题](https://github.com/castorhrio/agent-pointer-desktop/issues)

![桌面应用](docs/desktop-preview.png)

## 当前状态

`v0.1.0` — 首个公开版本，提供 Windows x64 自包含下载包。

已通过实际安装包验证：隔离目录安装、桌面首次打开、体验按钮实际点击、关闭到托盘与
单实例重新打开；另通过独立进程验证点击穿透、`start` 立即返回、owner 隔离、显示器换算及错误码。
Claude Code / Codex 技能文件的安装状态可检查；真实 agent 会话中的完整任务仍需由使用者验证。

## 主要能力

| 能力 | 说明 |
|---|---|
| 屏幕指引 | 箭头配合动作说明，告诉你在哪里完成哪一步 |
| 本地体验 | 无需接入 agent 即可试用指引和完成流程 |
| Agent 接入 | 为 Claude Code / Codex 安装技能，并显示文件是否与当前程序匹配 |
| 不抢焦点 | 普通指引保留当前输入焦点；显式 `--raise` 才请求置前 |
| 点击穿透 | 覆盖层允许鼠标点击到下方应用，并经过跨进程实际点击验证 |
| 多 agent 隔离 | 按 owner 停止指引，owner 退出后自动清理 |
| 桌面与 CLI | 桌面面板提供设置和体验，CLI 供 agent 独立调用 |
| 中英文界面 | 支持简体中文与英文切换 |

## 安装

要求 **Windows 10/11 x64**。从 [GitHub Releases](https://github.com/castorhrio/agent-pointer-desktop/releases/latest)
下载并完整解压，目标电脑无需安装 .NET 或开发工具。

| 下载 | 用途 |
|---|---|
| [agentpointer-0.1.0-win-x64.zip](https://github.com/castorhrio/agent-pointer-desktop/releases/latest/download/agentpointer-0.1.0-win-x64.zip) | 完整桌面应用，可安装或便携运行 |
| [SHA-256 校验文件](https://github.com/castorhrio/agent-pointer-desktop/releases/latest/download/agentpointer-0.1.0-win-x64.sha256) | 校验 ZIP 下载是否完整 |

**方式一：安装使用。** 解压后双击 `Install.cmd`。安装到
`%LOCALAPPDATA%\Programs\AgentPointer`，添加用户 PATH 与开始菜单快捷方式，并打开桌面面板。
技能由用户在面板内选择安装；安装程序默认不改写 agent 技能。不需要管理员权限。
已打开的终端需重启以读取新 PATH。

**方式二：便携使用。** 解压后直接双击 `AgentPointer.Desktop.exe`。
保留整个解压目录；程序依赖同目录的 CLI 和运行时文件。

下载包包含：

- `AgentPointer.Desktop.exe`：桌面接入、体验和托盘入口。
- `agentpointer.exe`：供 agent 调用的屏幕指引工具，内嵌技能与 Codex 元数据。
- 自包含运行时、安装脚本、中英文使用说明和许可证。

也可在解压目录的 PowerShell 中安装：

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File .\Install.ps1
```

`Install.ps1 -NoPath -NoShortcut -NoLaunch` 可跳过 PATH、快捷方式和启动；
`-Destination <目录>` 指定安装位置；`-InstallSkills` 用于无界面接入，安装时一并配置技能。
`AgentPointer.Desktop.exe --background` 仅启动托盘。

发布包尚未进行 Authenticode 签名，网络下载分发时可能触发 SmartScreen。
桌面的「检查更新」对比本仓库最新 Release 的版本，有新版时提供发布页链接。
更新需手动下载、退出正在运行的应用，再解压或重新安装。

卸载时删除安装目录、用户 PATH 对应项、开始菜单的 Agent Pointer 快捷方式，
以及你安装的两个 agentpointer 技能目录。

## 第一次使用

1. 打开 **Agent Pointer**，点击「体验屏幕指引」。箭头会指向窗口中的演示按钮；点击「完成这一步」后收起。
2. 点击「安装 / 更新 agent 技能」，为 Claude Code 和 Codex 配置使用说明。
3. 重启你使用的 agent，点击「复制试用提示词」，粘贴到新的 agent 会话中。
4. 继续在 agent 对话里描述任务，需要人工操作时让它发起屏幕指引。

桌面上的体验是本地演示，点击只会结束体验，不会批准或执行实际任务。
「技能已安装」表示文件已配置，不表示 agent 正在运行或已建立实时连接。
应用不会读取或监控聊天内容，实际指引由 agent 调用工具发起。

关闭主窗口会保留在托盘，再次打开会回到原来的实例。托盘菜单提供退出入口。
CLI 可由 agent 独立调用，普通指引不依赖桌面面板持续打开。

## 接入 Claude Code / Codex

桌面按钮与下列命令使用同一条安装逻辑：

```powershell
agentpointer install-skill --dry-run --json
agentpointer install-skill --json
```

技能来自 [skills/agentpointer/SKILL.md](skills/agentpointer/SKILL.md)，包含
`agents/openai.yaml` 的 Codex 展示信息，内嵌 CLI，无需另外下载。
默认安装到：

- `%USERPROFILE%\.claude\skills\agentpointer\`
- `%USERPROFILE%\.agents\skills\agentpointer\`

只更新这两个工具专用技能目录中的文件，不修改其他 agent 设置或 hook。
命令自动写入当前 CLI 的绝对路径，便携版无需依赖 PATH；移动程序后需重新安装技能。
`--dry-run --json` 同时报告各 agent 的技能是否存在、是否与当前程序版本和路径匹配。
这些状态用于桌面界面的「尚未安装」「技能已安装」「需要更新」，不代表实时 agent 连接。

技能说明坐标空间、非阻塞调用、动作文字、owner 隔离和功能边界。
真实任务应给出完整的动作说明，如“点击 Allow，允许此应用访问文件”；
未传 `--text` 时的默认说明只用于查看位置，不能解释具体任务。

## 命令

| 命令 | 行为 |
|---|---|
| `point` | 显示并等待结束，默认 8 秒 |
| `start` | 上屏后返回，默认 300 秒，`--duration 0` 不限时 |
| `stop` | 只停止同 owner 的箭头；无 owner 时只清未归属箭头 |
| `stop --all` | 清除当前 Windows 会话的全部箭头 |
| `stop --hook` | 要求 owner 环境变量，按 owner 清除；没有箭头时静默 |
| `doctor` | 显示器、DPI、桌面可用性、覆盖层样式和命中测试 |
| `install-skill` | 安装两个 agent 的技能文件 |
| `elements` | 暂不支持，退出码 5 |

常用目标：`--at X,Y`、`--rect X,Y,W,H`、`--mouse`、`--window App[:title]`，互斥。
`--app` 是 `--window` 的别名。窗口匹配按 Windows 进程名进行；默认不置前，
`--raise` 才主动请求置前，`--no-raise` 优先。`--dry-run` 不绘制，也不改变焦点。

外观：`--text`、`--from`、`--color`、`--size S|M|L`、`--shape bend|straight`、
`--corners round|sharp`、`--border shadow|white-black|black`、`--border-color`、`--text-color`。
`--no-animation` 关闭入场、呼吸和淡出。完整参数见 `agentpointer --help`。

## 坐标与显示器

所有输入和输出坐标均为**物理像素**，`(0,0)` 是主屏左上角，允许负坐标。
显示器编号在 `doctor`、`--display`、普通输出及 JSON 中统一从 **1** 开始。

- `--at` / `--rect` 配合 `--display N`：坐标相对于该屏左上角，先换算再选屏和校验。
- `--window` / `--mouse` 已返回全局坐标；`--display N` 仅过滤目标所在屏，不重复平移。
- 屏外坐标、显示器之间空隙中的目标：退出码 3，`--dry-run` 同样校验。
- 不存在的显示器编号：退出码 2。

上游 macOS 使用逻辑点，不能直接复用其坐标。150% 缩放下，屏内逻辑坐标需乘 `1.5`
再加该屏的全局物理原点。截图如有缩放，还需按截图比例换算。
PerMonitorV2 保证渲染与系统窗口定位使用物理像素，不会自动转换调用方提供的逻辑坐标。

## 生命周期与退出码

`CLAUDE_PID` 优先于 `BIGARROW_OWNER_PID`。将它设为实际、长期存活的 agent PID，
并在 `start`、`point`、`stop` 调用中保持一致；owner 退出时箭头也消失。
不要绑定只执行一次命令的临时 PowerShell PID。

`stop --hook` 不猜测 hook 的 owner，也不静默清除其他 agent 的箭头。
未设置 owner 的调用归入未归属组；需要多 agent 隔离时必须设置 owner。

| 退出码 | 含义 |
|---|---|
| 0 | 成功 |
| 2 | 参数错误 |
| 3 | 目标未找到 / 屏外 |
| 4 | 权限不足 / 锁屏或 UAC 安全桌面 |
| 5 | 功能未支持 |
| 6 | 内部错误 / 后台启动失败 |

`--json` 返回结构化结果；内部异常不再归为权限错误。
后台启动限时 5 秒，失败会清理子进程并返回错误。

## 能力边界

部分参数名、默认值与视觉规则沿用 bigarrow，**并非完全兼容**：

- `elements` / `--element` 尚未接入 Windows UI Automation。
- `--follow`、`--until-click`、`--until-any-click`、`--close-button`、`--say`、`--voice`、
  `--png` 及 edge/close 颜色参数目前仅解析；JSON 的 `ineffective` 字段会列出，普通输出会提示。
- `ring` / `box` 回退 `arrow`，`zigzag` / `spiral` 回退 `bend`；窗口 anchor 只实现 `center` / `title`。
  这些上游免费样式**不是 Pro 功能**，当前缺少实现。项目没有已交付的收费功能。
- `--peekaboo` / `--snapshot` 为 macOS 专属，在 Windows 返回退出码 2。
- 锁屏与 UAC 安全桌面无法绘制；独占全屏应用上方可能不可见。

## 诊断与开发

覆盖层用 `UpdateLayeredWindow` 绘制逐像素透明窗口，并组合
`WS_EX_LAYERED | WS_EX_TRANSPARENT` 让鼠标事件穿透到其他进程的下层窗口。
`HTTRANSPARENT` 只补充同线程命中测试，不能单独证明跨进程点击送达。
依据：[微软分层窗口文档](https://learn.microsoft.com/en-us/windows/win32/winmsg/window-features#layered-windows)。

`doctor` 报告 `ConfigurationValid` 和 `ClickDelivery: "not-tested"`，
仅自检样式与消息返回值。发布前需在独立测试窗口中点击被箭头覆盖的按钮，确认点击真正送达。

中文输出沿用宿主控制台编码；无控制台的后台进程输出 UTF-8，避免 PowerShell 5 管道
在退出恢复代码页后误解码。文件均使用 UTF-8。

从源码开发需要 [.NET 10 SDK](https://dotnet.microsoft.com/download/dotnet/10.0) 与 Windows：

```powershell
dotnet run --project src/AgentPointer.Cli -- start --at 600,400 --text "你好"
dotnet run --project src/AgentPointer.Tray
dotnet test tests/AgentPointer.Core.Tests/AgentPointer.Core.Tests.csproj
dotnet run --project tests/AgentPointer.WindowsChecks -- src/AgentPointer.Cli/bin/Debug/net10.0-windows/agentpointer.exe docs/overlay-preview.png
dotnet run --project tests/AgentPointer.WindowsChecks -- --desktop src/AgentPointer.Tray/bin/Debug/net10.0-windows/AgentPointer.Desktop.exe docs/desktop-preview.png
```

CLI/Core 使用 .NET 10，CLI 为 WinForms，托盘为 WPF。托盘的检查更新只在成功取得
有效版本且完成比较后才显示“已是最新”；HTTP 或版本数据错误显示检查失败。

生成包含桌面、CLI、运行时、安装入口、中英文说明和 SHA-256 校验文件的发布包：

```powershell
powershell -NoProfile -File .\scripts\Publish.ps1
```

输出位于 `artifacts/`。`-Runtime win-arm64` 可从源码生成 ARM64 包；当前公开下载与实际运行验证为 x64。

`WindowsChecks` 会临时显示独立进程的测试按钮，在指引卡片的不透明区域发送真实鼠标输入，
验证跨进程点击送达、透明背景、焦点、后台返回及管道关闭、owner/stop/hook 隔离、显示器坐标
与错误码，并保存实际窗口截图。它会短暂移动鼠标，结束后恢复鼠标位置和前台窗口。
桌面检查验证首屏可见、组件与技能状态、体验按钮实际点击与收起、关闭到托盘及单实例重新打开。
这些检查不安装技能、不修改用户 PATH 或快捷方式。

## 算法与许可

尺寸/色彩规则改编自上游 `ArrowAppearance.swift`，方向避让与评分布局改编自
`Placement.swift`。不是完全独立重写，也不承诺 Windows 渲染逐像素等同于 macOS。

MIT。项目许可证见 [LICENSE](LICENSE)，上游版权与完整许可见
[THIRD_PARTY_NOTICES.md](THIRD_PARTY_NOTICES.md)。两者随构建及发布产物一起分发。
