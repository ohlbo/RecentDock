# RecentDock

RecentDock 是一个轻量的 Windows 最近项目面板，用于集中展示最近打开的文件和访问过的文件夹。它常驻系统托盘，数据只在本机读取和保存。

当前版本：**v0.5.0**

## 主要功能

- 自动读取并解析 Windows 最近项目记录，文件和文件夹按最近使用时间排列。
- 收藏置顶常用项目，收藏状态会在重启后保留。
- 按名称或完整文件地址快速搜索，即使隐藏了地址行仍可搜索路径。
- 双击打开目标，右键可打开所在位置、收藏置顶或移除最近记录。
- 支持键盘操作，减少鼠标操作。
- 窗口靠近屏幕边缘时自动吸附，可选左右边缘自动隐藏。
- 支持拖动窗口四边及四角调整大小。
- 可调面板透明度、文字大小和图标大小。
- 可切换文件地址显示、窗口置顶和开机自启动。
- 自动监听最近项目变化，并在后台刷新列表。
- 支持系统托盘显示、隐藏、设置和退出。

## 键盘操作

| 按键 | 功能 |
|---|---|
| `Ctrl + F` | 聚焦搜索框 |
| `Ctrl + R` | 刷新最近项目 |
| `Ctrl + D` | 收藏或取消收藏当前项目 |
| `↑` / `↓` | 切换当前项目 |
| `Enter` | 打开当前项目；搜索时可直接打开第一个结果 |
| `Delete` | 从 Windows 最近列表移除当前记录，不删除原文件 |
| `Esc` | 清空搜索；搜索框为空时隐藏到托盘 |

## 界面与行为设置

点击主窗口右上角的齿轮按钮可设置：

- 面板透明度
- 文字大小
- 图标大小
- 是否显示文件地址
- 是否保持窗口置顶
- 是否启用屏幕边缘吸附
- 是否在左右边缘吸附后自动隐藏
- 是否开机自启动

透明度只作用于面板背景，文字和图标会保持清晰。主窗口的文字大小设置不会影响设置窗口本身。

## 运行要求

- Windows 10 或 Windows 11
- [.NET 8 Desktop Runtime](https://dotnet.microsoft.com/download/dotnet/8.0)

透明效果和系统合成表现可能因 Windows 版本、显卡驱动及系统视觉设置而略有不同。

## 从源码构建

安装 [.NET 8 SDK](https://dotnet.microsoft.com/download/dotnet/8.0)，在仓库根目录执行：

```powershell
dotnet build RecentDock.sln -c Release
dotnet test RecentDock.sln -c Release --no-build
```

发布 Windows x64 版本：

```powershell
dotnet publish src\RecentDock.App\RecentDock.App.csproj `
  -c Release `
  -r win-x64 `
  --self-contained false `
  -o dist
```

发布完成后可双击 `dist\RecentDock.exe`，或运行：

```powershell
.\run.cmd
```

## 数据来源与隐私

RecentDock 读取 Windows 当前用户的 Recent 目录以及相关的本地最近项目记录。程序不会上传文件内容、文件地址或使用记录。

如果 Windows 的“显示最近打开的项目”功能被关闭，新项目可能不会继续写入系统记录。RecentDock 会提示当前状态，但不会擅自修改该系统设置。

程序自己的设置、列表快照和收藏记录保存在：

```text
%APPDATA%\RecentDock
```

## 项目结构

```text
src/RecentDock.App/          WPF 界面、托盘及窗口行为
src/RecentDock.Core/         最近项目扫描、解析、过滤和本地存储
tests/RecentDock.Core.Tests/ 核心逻辑自动化测试
dist/                        本地发布产物（不提交到 Git）
```

更详细的设计说明参见 [DESIGN.md](DESIGN.md)，透明主题和排查记录参见 [THEME.md](THEME.md)。

## 当前限制

- 最近访问时间主要来自 Windows 最近项目记录，可能与文件本身的修改时间不同。
- 无法解析或已损坏的快捷方式会被跳过。
- 从列表移除只删除 Windows 的最近访问记录，不会删除原文件。
- 收藏项目仍需要存在于 Windows 可读取的最近项目结果中，才会显示在面板内。
