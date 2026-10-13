# 第三方声明 / Third-Party Notices

Agent Pointer for Windows 包含从 **bigarrow** 的 Swift 源码移植、改写的算法。
这属于衍生实现，并非仅参考接口或完全独立重写。

## bigarrow

- 项目：[big-arrow-on-the-screen](https://github.com/franzenzenhofer/big-arrow-on-the-screen)
- 作者与版权所有者：Franz Enzenhofer
- 许可证：[MIT](https://github.com/franzenzenhofer/big-arrow-on-the-screen/blob/main/LICENSE)
- 移植范围：`ArrowAppearance.swift` 的尺寸、色彩与文字布局规则，
  `Placement.swift` 的方向选择、避让与布局评分；对应本项目的
  `ArrowMetrics.cs`、`ArrowPalette.cs`、`Placement.cs` 及覆盖层布局实现。
- CLI 借鉴上游命令名和参数名。Windows 版有明确的平台差异，并非完全兼容。
  本仓库的 Windows 技能说明按实际支持能力编写。

以下保留上游版权声明与 MIT 许可全文。源码及二进制分发均需随附本文件；
CLI 与托盘项目均将本文件和项目 LICENSE 复制到构建与发布目录。

```
MIT License

Copyright (c) 2026 Franz Enzenhofer

Permission is hereby granted, free of charge, to any person obtaining a copy
of this software and associated documentation files (the "Software"), to deal
in the Software without restriction, including without limitation the rights
to use, copy, modify, merge, publish, distribute, sublicense, and/or sell
copies of the Software, and to permit persons to whom the Software is
furnished to do so, subject to the following conditions:

The above copyright notice and this permission notice shall be included in all
copies or substantial portions of the Software.

THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR
IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF MERCHANTABILITY,
FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE
AUTHORS OR COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER
LIABILITY, WHETHER IN AN ACTION OF CONTRACT, TORT OR OTHERWISE, ARISING FROM,
OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN THE
SOFTWARE.
```

## .NET 运行时与开发依赖

本项目使用 Microsoft .NET 10、WinForms / WPF；.NET 的许可证及第三方声明见
[dotnet/runtime](https://github.com/dotnet/runtime)。自包含包附带 .NET 运行时，
其许可适用于随包分发的运行时组件。

测试使用 Microsoft.NET.Test.Sdk、xUnit 与 coverlet.collector，仅作为开发依赖，
不随 CLI 安装包分发。
