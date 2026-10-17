# RecentDock

RecentDock 是一个轻量的 Windows 最近项目面板，用于集中展示最近打开的文件和访问过的文件夹。它常驻系统托盘，数据只在本机读取和保存。

当前版本：**v0.5.2**

## 主要功能

- 自动读取并解析 Windows 最近项目记录，文件和文件夹按最近使用时间排列。
- 在软件里或资源管理器里重新打开某个文件后，它的打开时间和排序会随之更新。
- 收藏置顶常用项目，收藏状态会在重启后保留。
- 按名称或完整文件地址快速搜索，即使隐藏了地址行仍可搜索路径。
- 双击打开目标，右键可打开所在位置、收藏置顶或移除最近记录。
- 支持键盘操作，减少鼠标操作。
- 窗口靠近屏幕边缘时自动吸附，可选左右边缘自动隐藏。
- 支持拖动窗口四边及四角调整大小。
- 可调面板透明度、文字大小和图标大小。
- 可切换文件地址显示、窗口置顶和开机自启动。
- 自动监听最近项目变化（Recent 目录与 `RecentDocs` 注册表记录），并在后台刷新列表。
- 支持系统托盘显示、隐藏、设置和退出。
- 主窗口不占用任务栏，也不会出现在 `Alt+Tab` 应用切换列表中。

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
powershell -ExecutionPolicy Bypass -File tools\publish.ps1
```

该脚本先 `dotnet publish` 到 `dist`，再用本机自签名证书给 exe 签名。二者绑在一起是有意为之：
**每次重新发布都会产生一个全新的未签名二进制，而 SmartScreen 会拦截它从没见过的未签名程序**
（就是那个"Windows 已保护你的电脑 / 发布者未知"的全屏蓝色提示）。签名后提示不再出现。

也可以手动执行原来的两步：

```powershell
dotnet publish src\RecentDock.App\RecentDock.App.csproj `
  -c Release `
  -r win-x64 `
  --self-contained false `
  -o dist

powershell -ExecutionPolicy Bypass -File tools\sign.ps1
```

`tools\sign.ps1` 第一次运行会创建自签名代码签名证书 `CN=RecentDock Local Build`，装进**当前用户**
的"受信任的根证书颁发机构"和"受信任的发布者"，之后每次复用同一张证书。
注意：这只在你本机有效——把 exe 发给别人，对方仍会看到"未知发布者"，正式分发需要用可信 CA
签发的代码签名证书。

发布完成后可双击 `dist\RecentDock.exe`，或运行：

```powershell
.\run.cmd
```

请**从资源管理器双击启动**（或从开始菜单快捷方式）。不要在沙箱、受限终端或杀毒软件的
"受限运行"里拉起它：那样程序会以低完整性令牌运行，Windows 会拒绝注册托盘图标，也会拒绝
打开文件夹和由系统应用（如"照片"）处理的文件。

程序能做的只有一半：进程**无法**提升自己的完整性级别，令牌是创建时由父进程给的，
提升的唯一方式是让 Windows 以你的身份新建一个进程——而这必须经过 UAC 同意。所以
RecentDock 会在启动时立刻检测并**自动发起一次提权请求**；取消后不会再反复询问，状态栏会
显示"受限环境：托盘与打开文件被系统拒绝"，并保留**"以正常权限重启"**按钮随时重试。
`dist\RecentDock.exe --selftest` 的输出里也有 `run context` 一行可用于确认当前权限级别。

## 数据来源与隐私

RecentDock 读取 Windows 当前用户的 Recent 目录以及相关的本地最近项目记录。程序不会上传文件内容、文件地址或使用记录。

如果 Windows 的“显示最近打开的项目”功能被关闭，新项目可能不会继续写入系统记录。RecentDock 会提示当前状态，但不会擅自修改该系统设置。

程序自己的设置、列表快照、收藏记录和推断出的最近打开时间保存在：

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

- Windows 的最近项目记录里没有"最后打开时间"字段：记录文件只在第一次打开时创建，之后不再改写。
  RecentDock 显示的时间由记录的先后顺序推断：某个项目的名次比上次更靠前，就说明上次之后被打开过。
  因此**重新打开"本来就是同类最靠前"的项目时名次不变，时间可能保持上一次的推断结果**；
  从面板里打开的项目不受此影响。
- 最近访问时间与文件本身的修改时间无关，两者可能完全不同。
- 无法解析或已损坏的快捷方式会被跳过。
- 从列表移除只删除 Windows 的最近访问记录，不会删除原文件。
- 收藏项目仍需要存在于 Windows 可读取的最近项目结果中，才会显示在面板内。
