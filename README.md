# 闲页 QuietReader

一个轻量的 Windows 本地 TXT 阅读器。把小说放进可缩放的小窗里阅读，记住进度，需要时一键退出。

**[下载最新版便携包](https://github.com/zimo-2233/quiet-reader/releases/latest)** · [详细使用说明](使用说明.md) · [验证记录](docs/verification.md)

## 使用

1. 从 Releases 下载 `QuietReader-v1.0.0-win-portable.zip` 并完整解压。
2. 双击 `QuietReader.exe`。
3. 点击「＋ 导入」、按 `Ctrl+O`，或把 TXT 文件拖入窗口。

运行环境：Windows 10 / 11，.NET Framework 4.8。无需安装 Python 或 Node.js，阅读功能不需要联网。

## 功能

- **本地 TXT 导入**：支持 UTF-8、GBK / GB18030、带 BOM 的 UTF-16 / UTF-32，自动识别常见中英文章节。
- **进度记忆**：每本书分别保存阅读位置；重新打开自动续读，原文件移动后仍可阅读导入副本。
- **长篇分段排版**：只排版当前位置附近最多 16,384 个字符，避免长篇全文布局造成明显卡顿；目录和进度使用全书位置。
- **外观设置**：默认 VS Code 暗色配色，自定义背景、文字颜色、字号和不透明度，窗口框和菜单随主题同步。
- **可调窗口和置顶**：拖动边缘缩放，默认不置顶，通过按钮或快捷键切换并记住选择。
- **无边框小窗**：只显示阅读区；顶部 24 像素透明区域用于拖动，只有这里显示移动光标，正文显示普通箭头。右键打开菜单。
- **老板键**：立即移除窗口、保存进度并退出进程。

## 快捷键

| 快捷键 | 功能 |
| --- | --- |
| `Ctrl+O` | 导入 TXT |
| `Ctrl+T` | 开关置顶 |
| `Ctrl+M` | 切换完整窗口 / 无边框小窗 |
| `Ctrl+滚轮` | 调整字号 |
| `←` / `→`、`PgUp` / `PgDn` | 翻页 |
| `↑` / `↓`、滚轮 | 逐行滚动 |
| `Ctrl+Home` / `Ctrl+End` | 全书开头 / 结尾 |
| `Esc` | 窗口内快速退出 |
| `Ctrl+Alt+Q` | 全局老板键 |

全局快捷键被占用时，依次尝试 `Ctrl+Alt+F12`、`Ctrl+Shift+F12`，以状态栏或外观面板显示为准。系统文件选择或颜色对话框内的 `Esc` 只关闭该对话框。

## 数据与限制

书架、进度和设置保存在 `%LOCALAPPDATA%\QuietReader\`，导入副本保存在其中的 `books\`。程序不修改原 TXT，不发送阅读数据，不包含联网阅读或账号系统。

单文件上限为 32 MB。无法可靠判断的编码请先另存为 UTF-8。运行时可以用 `--data-dir "D:\MyReaderData"` 指定独立书库。同一书库只能运行一个实例。

发布包仅附带原创示例文本，不包含个人小说、阅读记录或测试书库。

## 从源码构建

使用 Windows PowerShell，在仓库根目录执行：

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File .\build.ps1 -Test
```

脚本使用 Windows 自带的 .NET Framework C# 编译器，不下载第三方依赖。生成程序位于 `dist\QuietReader.exe`；`-Test` 执行编码、持久化和 WPF 控件回归检查。

性能测试使用自动生成的约 330 万字符文本：

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File .\build.ps1 -Benchmark
```

程序的全局热键和桌面交互需要在普通 Windows 桌面环境下验证；受限运行环境可能拒绝注册全局热键。

## 目录

```text
src/                     WPF 界面、阅读器逻辑、编码与书库持久化
tests/CoreTests.cs       核心回归检查
tests/WpfTests.cs        控件与布局回归检查
tests/PerformanceTests.cs 长篇排版与连续阅读性能检查
tests/示例小说.txt         原创示例文本
build.ps1               构建及测试入口
```

## 交互参考

浮窗、外观调节和进度记忆交互参考 [NovelReader](https://github.com/pgzs13/NovelReader) 与 [moyu-reader](https://github.com/gzg66/moyu-reader)。本项目独立实现，老板键采用直接退出行为。
