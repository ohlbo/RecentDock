# RecentDock 主题调整指南

**当前是浅色白玻璃主题。** 改完之后重新构建即可生效，不需要改任何 C# 代码：

```powershell
dotnet build src\RecentDock.App\RecentDock.App.csproj -c Release
dotnet publish src\RecentDock.App\RecentDock.App.csproj -c Release -r win-x64 --self-contained false -o dist
.\run.cmd
```

> 注意：`dist` 里的程序是发布产物，**必须重新 publish 才会更新**。
> 只 `build` 不 `publish`，`run.cmd` 跑的还是旧版本。

---

## 零、主题由两半组成（这是关键）

外观**不是**只由 XAML 决定。它有两个独立的部分，**两者不一致就会出现"改了没反应"**：

| 部分 | 位置 | 作用 |
|---|---|---|
| **DWM 材质变体** | `AcrylicBackdrop.cs` 第 78 行 | 决定亚克力本身是**浅色还是深色** |
| **XAML 调色板** | `App.xaml` 第 25–38 行 | 叠加在材质之上的颜色 |

**曾经的真实 bug**：`AcrylicBackdrop.cs` 里把 DWM 材质硬编码成深色，于是无论 XAML 怎么改，
窗口始终是一块黑板 —— 因为亚克力自己在提供黑色底。已修复，现在由
`config.json` 的 `UseDarkTheme` 控制。

**你的 Windows 系统主题（深色/浅色）不影响本程序** —— 材质变体由程序显式指定，
不跟随系统。这样保证外观可预期。

自检会同时报告两半的状态，排查时先看它：

```powershell
.\run.cmd --selftest
# theme : light (white glass), acrylic on
```

---

## 一、颜色与透明度 —— 主要改这里

**文件：`src/RecentDock.App/App.xaml`**（第 33–46 行）

```xml
<!-- 白色玻璃：浅色底 + 深色文字 -->
<SolidColorBrush x:Key="GlassTintBrush"     Color="#CCFFFFFF" />   <!-- 整体底色 -->
<SolidColorBrush x:Key="GlassHeaderBrush"   Color="#C0FFFFFF" />   <!-- 标题栏/状态栏 -->
<SolidColorBrush x:Key="GlassSurfaceBrush"  Color="#B3FFFFFF" />   <!-- 列表区 -->
<SolidColorBrush x:Key="GlassBorderBrush"   Color="#59FFFFFF" />   <!-- 外框线 -->

<SolidColorBrush x:Key="PrimaryTextBrush"   Color="#FF1C1C20" />   <!-- 主要文字（近黑） -->
<SolidColorBrush x:Key="SecondaryTextBrush" Color="#FF5F6068" />   <!-- 次要文字 -->
<SolidColorBrush x:Key="AccentBrush"        Color="#FF2F6FD8" />   <!-- 强调色 -->
<SolidColorBrush x:Key="WarningBrush"       Color="#FFB4651A" />   <!-- 失效标记（深琥珀） -->

<SolidColorBrush x:Key="HoverBrush"         Color="#14000000" />   <!-- 悬停：压暗 -->
<SolidColorBrush x:Key="SelectedBrush"      Color="#332F6FD8" />   <!-- 选中 -->
```

### 颜色格式：`#AARRGGBB`

**前两位是透明度，后六位是颜色。** 这是调整玻璃效果最常用的旋钮。

| Alpha | 效果 |
|---|---|
| `00` | 完全透明（看不见） |
| `4D` | 30% 不透明 |
| `80` | 50% |
| `B3` | 70% —— 当前列表区 |
| `CC` | 80% —— 当前整体底色 |
| `FF` | 完全不透明 |

**想让桌面更透出来** → 把三个 `Glass*` 的 alpha 调低，例如：

```xml
<SolidColorBrush x:Key="GlassTintBrush"    Color="#A6FFFFFF" />
<SolidColorBrush x:Key="GlassHeaderBrush"  Color="#99FFFFFF" />
<SolidColorBrush x:Key="GlassSurfaceBrush" Color="#8CFFFFFF" />
```

**觉得文字不够清楚** → 反过来调高。

### 浅色主题的 alpha 预算

```
0xCC (80%)  GlassTintBrush    整体底色，决定纵深
0xC0 (75%)  GlassHeaderBrush  标题栏/状态栏，稍重，读起来像"窗口装饰"
0xB3 (70%)  GlassSurfaceBrush 列表区，最轻，让内容区显得通透
```

**白色底比黑色底更能"扛"低不透明度**：深色文字在浅色玻璃上，透明度降到 70% 仍能保持对比；
而深色主题下白色文字在 70% 的黑色玻璃上就已经开始糊了。所以浅色主题可以比深色主题更透。

**注意**：这套 alpha 同时是 **Windows 10 上唯一的外观来源**（那里没有系统亚克力），
所以不要把内容区压到 `80` 以下，否则在 Windows 10 上文字会失去对比。

---

## 二、按钮、右键菜单、分隔线

同样在 **`App.xaml`**：

| 位置 | 说明 |
|---|---|
| 按钮背景 | 常态 `#14000000`（浅压暗），悬停 `#22000000`，按下 `#38000000` |
| 按钮圆角 | `CornerRadius="4"` |
| 右键菜单背景 | `#F7F7F8FA` —— **必须接近不透明**，见下 |
| 右键菜单圆角 | `CornerRadius="6"` |
| 菜单项高亮 | `#332F6FD8` |
| 分隔线 | `#26000000` |

**右键菜单的 alpha 不要学面板调低**：它是独立弹出的窗口，背后**没有**亚克力材质，
透明度低会直接透出桌面内容，文字就没法读了。深色主题下同理。

### 悬停/选中的方向

浅色主题下，悬停是**压暗**（`#14000000` = 黑色 8%）；深色主题下是**提亮**（`#1FFFFFFF` = 白色 12%）。
换主题时这两个方向容易搞反 —— 在白色玻璃上叠白色会让行"消失"。

---

## 三、玻璃材质的种类（Windows 11 特有，改动影响最大）

**文件：`src/RecentDock.App/AcrylicBackdrop.cs`**（第 42 行）

```csharp
private const int DwmsbtTransientWindow = 3;
```

这个值决定用哪种 DWM 材质：

| 值 | 常量名 | 观感 |
|---|---|---|
| `2` | `DWMSBT_MAINWINDOW` | **Mica** —— 更细腻、更不透明，像 Windows 11 设置窗口 |
| `3` | `DWMSBT_TRANSIENTWINDOW` | **Acrylic** —— 当前值，更明显的毛玻璃、更透 |
| `4` | `DWMSBT_TABBEDWINDOW` | 介于两者之间 |

想换成 Mica：把第 42 行的 `= 3` 改成 `= 2`，并把常量名一起改掉（仅为了可读性）。

**其他相关项：**

| 位置 | 行号 | 说明 |
|---|---|---|
| 深色标题栏 | 31, 78 | `DwmwaUseImmersiveDarkMode`，设 `1` |
| 圆角开关 | 39, 77 | `DwmWindowCornerPreferenceRound = 2`；改 `1` 为不圆角 |
| 版本门槛 | 49 | `Windows11_22H2Build = 22621` —— 低于此版本无亚克力 |

整套亚克力可以整体关掉：`config.json` 里把 `UseAcrylicBackdrop` 设为 `false`
（见下文），此时只剩 `App.xaml` 的半透明面板。

---

## 四、窗口外框与阴影

**文件：`src/RecentDock.App/MainWindow.xaml`**

| 位置 | 行号 | 说明 |
|---|---|---|
| 外框圆角 | 39 | `CornerRadius="8"` |
| **投影** | 42 | `<DropShadowEffect BlurRadius="18" ShadowDepth="0" Opacity="0.45" Color="#000000" />` |
| 标题栏圆角 | 56 | `CornerRadius="7,7,0,0"`（上圆下直，配合外框） |
| 状态栏圆角 | 245 | `CornerRadius="0,0,7,7"` |
| 跟踪关闭提示条 | 97 | `Background="#33E0A44A"` |
| 窗口默认尺寸 | 6 | `Height="560" Width="720"` |

**投影**：`BlurRadius` 调大更柔和，`Opacity` 调低更轻。设 `Opacity="0"` 即无阴影。
注意阴影是向内绘制的（`ShadowDepth="0"`），因为无边框窗口无法把阴影画到窗口之外。

**改圆角要三处同步**：外框（39）、标题栏（56）、状态栏（245）—— 标题栏是
`7,7,0,0`、状态栏是 `0,0,7,7`，它们的值应当比外框小一档（外框 8 → 内层 7），
否则会露出直角。

**窗口缩放边框的抓取宽度**在 `AcrylicBackdrop.cs` 第 149 行
（`BorderThickness = 6`，单位是像素）。

---

## 五、窗口位置和大小（不用改代码）

这些存在配置里，程序每次隐藏/退出时写回：

**`%APPDATA%\RecentDock\config.json`**

```json
{
  "WindowLeft": 724,
  "WindowTop": 260,
  "WindowWidth": 720,
  "WindowHeight": 560,
  "MaxItems": 50,
  "ShowMissingTargets": false,
  "UseAcrylicBackdrop": true,
  "StartHidden": false
}
```

| 字段 | 作用 |
|---|---|
| `WindowLeft` / `WindowTop` | 位置。**设为 `null` 则回到屏幕中央** |
| `WindowWidth` / `WindowHeight` | 大小。小于 420×220 会被忽略 |
| `MaxItems` | 列表最多显示多少条（1–500） |
| `ShowMissingTargets` | 是否显示目标已不存在的记录 |
| `UseAcrylicBackdrop` | **设为 `false` 关闭亚克力玻璃** |
| `StartHidden` | 启动时直接进托盘不显示面板 |

直接改文件也行，但**先完全退出程序**，否则它退出时会把内存里的值写回去覆盖你的修改。

想恢复默认外观：**删掉 `config.json`**，下次启动就回到屏幕中央、720×560。

---

## 六、快速对照：我想改 X，去哪里

| 我想要… | 改哪里 |
|---|---|
| **在浅色/深色主题之间切换** | `config.json` 的 `UseDarkTheme`（**不是**改 Windows 主题） |
| 面板更透明 / 更实 | `App.xaml` 的 `Glass*` alpha 前两位 |
| 换配色 | `App.xaml` 的 `Glass*` / 文字 / 强调色 |
| 换玻璃材质（亚克力 ↔ Mica） | `AcrylicBackdrop.cs` `DwmsbtTransientWindow = 3` → `= 2` |
| 关掉玻璃效果 | `config.json` 的 `UseAcrylicBackdrop: false` |
| 去圆角 | `AcrylicBackdrop.cs` `DwmWindowCornerPreferenceRound = 2` → `= 1` |
| 改阴影 | `MainWindow.xaml` 的 `DropShadowEffect` |
| 改默认窗口大小 | `MainWindow.xaml` 的 `Height`/`Width`，或 `config.json` |
| 改字号 | `MainWindow.xaml` 的 `FontSize="13"`（全局），个别元素各自有 `FontSize` |
| 改字体 | `MainWindow.xaml` 的 `FontFamily` |
| 改行高/内边距 | `MainWindow.xaml` 里 `ListViewItem` 样式的 `Padding` |
| 改窗口缩放抓取范围 | `AcrylicBackdrop.cs` 的 `BorderThickness = 6` |

---

## 七、三个容易踩的点

**1. 只 `build` 不 `publish`，改动看不到。** `run.cmd` 跑的是 `dist\RecentDock.exe`，
那是 publish 的产物。

**2. 程序运行时会锁住 `dist` 里的 dll。** 改完重新 publish 前先退出程序，
否则报 `MSB3027: 文件被 RecentDock 锁定`。

```powershell
Get-Process RecentDock -ErrorAction SilentlyContinue | Stop-Process -Force
```

**3. 改了 XAML 颜色但窗口还是黑的 → 检查 DWM 材质变体。**
`UseDarkTheme: true` 时材质本身是黑的，XAML 再白也没用。先看 `--selftest` 的 `theme` 行。
