# RecentDock 技术设计（v1）

> 本文档取代 `readme.txt` 中的原始计划，已落实两项决策：
> **① 放弃 `SetParent` 贴桌面；② 数据源升级为 `Recent 目录 + RecentDocs 注册表` 双源。**

---

## 〇、实测结论：双源的适用条件（必读）

本节全部结论来自两次真机取证（工具见 `tools/`），不再是推测。

### 第一次实测：开关关闭（`Start_TrackDocs = 0`）

| 数据源 | 实测结果 |
|---|---|
| `%APPDATA%\Microsoft\Windows\Recent\*.lnk` | **0 个** |
| `HKCU\...\Explorer\RecentDocs`（根键） | 键存在，**0 值 0 子键** |
| `Recent\AutomaticDestinations\*.automaticDestinations-ms` | 12 个，多为 2560 字节空壳 |
| `Recent\CustomDestinations\*.customDestinations-ms` | 50 个，多数 12–24 字节空壳 |

目录里只剩 `AutomaticDestinations\`、`CustomDestinations\` 两个子目录和 `desktop.ini`，
**没有任何 `.lnk`**。

### 第二次实测：开关开启（`Start_TrackDocs = 1`），打开 5 个文档 + 1 个文件夹

| 数据源 | 实测结果 |
|---|---|
| `Recent\*.lnk` | **8 个**，命名规则 = `<文件名>.<扩展名>.lnk`（含中文名，无同名冲突） |
| `RecentDocs` 根键 | 8 条数据值（索引 0–7）+ `MRUListEx` |
| `MRUListEx` | 36 字节 = 8 个索引 + `0xFFFFFFFF`；链 = `7 4 3 6 5 2 1 0` |
| `RecentDocs` 子键 | `.jpg` `.pdf` `.pptx` `.txt` + **`Folder`**（文件夹记录专用桶） |
| PIDL 解析 | 8 条全部解析成功，得到 `.lnk` 文件的完整路径 |

### 修正后的铁律（推翻了本节初稿的过度概括）

> **该开关的准确语义是"停止跟踪"，而非"删除已有记录"。**
>
> - 开关**从开到关**：实测 `Start_TrackDocs` 改为 `0` 后，已有的 8 个 `.lnk` 与 9 个
>   `RecentDocs` 值**全部保留**。关掉它只是不再新增记录。
> - 开关**关闭状态下的清空**：另有一次独立实测（初始状态即 `Start_TrackDocs = 0`）显示
>   两源均为空。推测由显式的"清除"操作触发 —— 设置界面里紧邻开关的那个清除按钮，
>   或存储感知（Storage Sense）的定期清理。
>
> 因此：**开关关闭 ≠ 读到空列表**。RecentDock 在关闭状态下仍可展示已有历史，只是不再有新记录。
> 但当记录确实被清空后，两个源会同时为空，此时无任何"绕开"的余地 —— 数据已不存在。

任何声称能"绕开开关"的说法仍必须从产品文案中删除，但理由是"数据可能已被清空"，
而不是"关闭即清空"。

### 由此推导出的产品模型（三态）

| 状态 | 判定条件 | 行为 |
|---|---|---|
| **A. 记录已开启** | `NoRecentDocsHistory` 不存在且 `Start_TrackDocs` 为 1 或缺失 | 双源正常读取，面板展示混合列表 |
| **B. 记录已关闭，但有历史** | `Start_TrackDocs = 0` 或 `NoRecentDocsHistory = 1`，且两源非空 | **正常展示历史**，但顶部提示"记录已关闭，正在展示已有历史；开启后可继续跟踪新记录"，附**一键跳转设置**按钮 |
| **C. 记录已关闭且无历史** | 状态 B 的判定条件下，两源均为空 | 显示引导页："系统未记录最近项目，RecentDock 无法补全未记录的历史"，附一键跳转设置 |
| **D. 已开启但确实无记录** | 状态 A 且两源均为空 | 常规空状态 + 提示"打开任意文档后此处会自动出现" |

**状态 B 是本次验证新增的、也可能是最常见的情形**，初稿把它和 C 混为一谈了。
空状态引导页是 v1 的一等公民功能，不是边角料：它承担了原本虚假卖点"绕开开关"的职责。

### 开关的准确位置与相关注册表值（实测澄清）

排查过程中确认了三件事，都会影响实现和用户指引：

**1. `Start_TrackDocs` 就是那个开关。** 实测把
`HKCU\Software\Microsoft\Windows\CurrentVersion\Explorer\Advanced\Start_TrackDocs`
从 `0` 改为 `1`，程序立刻从 `DisabledByUser` 变为 `Enabled`，随后打开的文件夹被立即记录。
**端到端确认，非推测。**

**2. `Start_TrackProgs` 与 RecentDock 无关。** 同一台机器上它是 `0`，但记录功能工作正常。
它管的是"跟踪应用启动以改进开始和搜索结果"，与 Recent 目录无关。
**不要把它当成前置条件** —— 看到它是 0 而以为有问题，是很容易犯的误判。

**3. 开关不在隐私页面。** 初稿的实现把设置链接指向 `ms-settings:privacy-recentdocs`，
**该页面不存在**。核实方式：把 `SystemSettings.dll` 里全部 39 个 `privacy-*` 页面标识符
列出来，其中有 `privacy-general`、`privacy-activityhistory`、`privacy-speech` 等，
**但没有 `privacy-recentdocs`**。正确位置是 **个性化 → 开始**
（`ms-settings:personalization-start`）。用户会先去找"隐私和安全性 → 常规"并找不到，
这是必然的。

| 注册表值 | 作用 | 是否为前置条件 |
|---|---|---|
| `Advanced\Start_TrackDocs` | **控制 Recent 目录与 RecentDocs 记录** | ✅ 是 |
| `Advanced\Start_TrackProgs` | 跟踪应用启动以改进搜索 | ❌ 否 |
| `Start\ShowRecentList` | 开始菜单"推荐的项目"是否显示 | ❌ 否（独立机制） |
| `Policies\NoRecentDocsHistory` | 组策略，为 1 时一票否决且设置页变灰 | ✅ 是（优先） |

三个原始值都通过 `EnvironmentProbe.GetDiagnostics()` 暴露，并打印在 `--selftest` 输出里，
以便日后排查"我明明开了却没反应"时不必靠猜。

### 可选增强（v2，不进 v1）

实测发现 `Explorer\ComDlg32`（`OpenSavePidlMRU` / `LastVisitedPidlMRU`）、`UserAssist`、
`TypedPaths` 等键在开关关闭时**依然存在数据**，因为它们是公共文件对话框与 Shell 的独立记录，
不归 `Start_TrackDocs` 管。它们可作为"开关关闭时的旁路数据源"。

**但必须标注**：这类取证级数据源（PIDL 二进制、ROT13 编码的 UserAssist、无统一时间戳）
解析成本高、语义不统一、易误报，且有隐私敏感度。**v1 明确不做**，仅在 README 的路线图里提及。

---

## 一、技术选型（修订）

| 项目 | 选择 | 变更说明 |
|---|---|---|
| 语言 | C# / .NET 8 | 不变 |
| UI | WPF | 不变 |
| 快捷方式解析 | `IShellLinkW` COM（自行封装） | 明确 Unicode 版；**禁止调用 `Resolve()`** |
| 注册表读取 | `Microsoft.Win32.Registry`（BCL，非第三方） | **新增** |
| 目标验证 | `File.Exists` / `Directory.Exists` | 加超时与离线路径快速跳过 |
| 进程模型 | 单进程、常驻托盘 | 补单实例 `Mutex` |
| 窗口挂载 | **普通顶层窗口 + 扩展样式** | **取代 `SetParent`** |

### 发布方式（必须写进 README）

WPF 依赖 **Windows Desktop 运行时**，不在 .NET 8 基础运行时内：

```
dotnet publish -c Release -r win-x64 --self-contained
```

**WPF 不支持 Native AOT**，禁止在 README 中承诺 AOT 或激进裁剪（`PublishTrimmed` 对 WPF 不可用）。

---

## 二、模块设计

### M0. EnvironmentProbe（新增，最先执行）

```
输入：无
输出：TrackingState { Enabled, DisabledByPolicy, DisabledByUser, Unknown }
```

判定顺序（policy 优先于用户设置）：

1. `HKCU\...\CurrentVersion\Policies\Explorer\NoRecentDocsHistory == 1` → `DisabledByPolicy`
2. `HKCU\...\Explorer\Advanced\Start_TrackDocs == 0` → `DisabledByUser`
3. 否则 → `Enabled`

实现要点：

- 两个键都需要 `try/catch`：键不存在是**正常情况**（= 启用），不是错误。
- 必须检查进程位数与 WOW64：用 `Registry.CurrentUser` 走托管 API 即可，不要手写
  `RegOpenKeyEx` 带错误 `samDesired`。
- 该探测结果**每次刷新时重算**（用户可能中途改设置），Watcher 无法感知注册表变化，
  所以状态 A→B 的切换靠定时重探（见 M3）。

### M1. RecentScanner（源 A：Recent 目录）

```csharp
record RecentItem(
    string DisplayName,
    string TargetPath,
    bool   IsDirectory,
    DateTimeOffset LastAccessTime,   // 来自 .lnk 的 LastWriteTime
    ItemValidity Validity,           // Valid | Missing | Unreachable | Unparsable
    ItemSource   Source              // RecentLnk | Registry
);
```

流程：

1. 路径获取：`Environment.GetFolderPath(SpecialFolder.Recent)`
   或 `SHGetKnownFolderPath(FOLDERID_Recent)`。前者更简单且够用；**注意它返回的是
   `%APPDATA%\...\Recent` 的展开路径**，若用户做了文件夹重定向也能正确返回。
2. `Directory.EnumerateFiles(recent, "*.lnk", SearchOption.TopDirectoryOnly)`
   —— `.lnk` 带 `Hidden|System` 属性，托管 API 不受影响（实测 `desktop.ini` 即为
   `Hidden, System, Archive`，能被正常枚举）；但**不要**用 `SearchOption.AllDirectories`，
   否则会把 `AutomaticDestinations` 下的跳转列表文件也扫进来。
3. 逐个解析（下节详述）。
4. 有效性判定，产出 `Validity`。

### M2. LinkResolver（`IShellLinkW` 封装规范）

这是最容易踩坑的一块，逐条固化：

1. **接口必须用 Unicode 版**：`IShellLinkW`（IID `000214F9-0000-0000-C000-000000000046`），
   路径参数一律 `[MarshalAs(UnmanagedType.LPWStr)]`。用 ANSI 版中文路径必乱码。
2. **`IPersistFile` 的 IID** 为 `0000010b-0000-0000-C000-000000000046`。
   创建实例：`Activator.CreateInstance(Type.GetTypeFromCLSID(new Guid("00021401-0000-0000-C000-000000000046")))`。
3. **禁止调用 `Resolve()`**。它会触发 Shell 搜索目标（含网络路径解析），在离线共享或
   已断开的映射盘上会阻塞数秒。只用 `GetPath`。
4. **用 `SLGP_RAWPATH`**：取链接内存储的原始路径，不做搜索与环境展开，更快更可预测。
   代价是可能返回未展开形式，需自行 `Environment.ExpandEnvironmentVariables`。
5. **HRESULT 显式检查**：`GetPath` 失败可能返回 `S_FALSE` 而非异常，
   不能只靠 `Marshal.ThrowExceptionForHR`。手写 `if (hr < 0) → Unparsable`。
6. **COM 单元状态**：WPF UI 线程已是 STA，但**扫描必须放后台线程**，后台线程入口需
   `CoInitializeEx(IntPtr.Zero, COINIT_APARTMENTTHREADED)`，`finally` 中 `CoUninitialize`。
   不要用 `Task.Run` 的线程池线程直接碰 COM 而不初始化。
7. **及时释放**：每个 `.lnk` 处理完立即 `Marshal.FinalReleaseComObject`，不要累积。
   （v1 每次刷新全量解析，不缓存 COM 对象。）

**性能与超时（必须处理）**：

- Recent 目录几百到上千个 `.lnk` 是常态。冷启动（磁盘缓存未预热）逐条 COM + 探测可能到
  数百毫秒甚至更久；指向**离线网络路径或已拔出移动硬盘**的记录会让 `File.Exists` 阻塞到超时。
- 对策：
  - 扫描全程异步，带 `CancellationToken`；新事件到来时取消上一次扫描。
  - 首次启动**先渲染上次的缓存快照**（落盘于配置目录），后台刷新完成后再替换。
  - 目标路径以 `\\` 开头（UNC）或盘符不存在时，**跳过磁盘探测直接标 `Unreachable`**。
  - 每 N 条（如 64 条）向 UI 批量推送一次，避免长时间白屏。
- **并发度**：不要并行解析。COM 单元约束 + 磁盘随机读，串行通常更快且更可预测。

### M3. RegistryScanner（源 B：RecentDocs）

**本节格式已由真机验证确认，不再是推测。** 取证样本：`DESIGN.md` 第〇节的第二次实测。

#### 键结构（实测）

```
HKCU\Software\Microsoft\Windows\CurrentVersion\Explorer\RecentDocs
├── MRUListEx      REG_BINARY   // 访问顺序索引链
├── 0              REG_BINARY   // 单条记录
├── 1 .. 7         REG_BINARY
├── .jpg  .pdf  .pptx  .txt     // 按扩展名分桶，各自也有 MRUListEx + 编号值
└── Folder                      // 文件夹记录的专用桶
```

实测确认：

- **文件夹记录落在名为 `Folder` 的子键**。这是本次验证解决的最大未知项，
  也是"文件与文件夹混排"能否实现的关键 —— 主键里同时包含两类，
  `Folder` 子键可用于**判定某条记录是否为文件夹**。
- 子键名是**英文** `Folder`（非本地化），可安全硬编码匹配；扩展名桶以 `.` 开头，
  因此 `name.StartsWith(".")` 即可区分两类桶。
- `ValueCount` 于根键返回 **9**，而数据值是 8 个 —— 9 包含了 `MRUListEx` 本身。
  **不要用 `ValueCount` 判断记录数**，必须用 `GetValueNames()` 枚举。

#### `MRUListEx`（实测）

- `REG_BINARY`，**4 字节小端整数序列，以 `0xFFFFFFFF` 终止**。
- 实测 36 字节 = 8 个索引 + 1 个终止符 → 链为 `7 4 3 6 5 2 1 0`，
  即 value[7] 最新、value[0] 最旧。
- **值是"访问顺序排名"的权威来源**，比 `.lnk` 的 `LastWriteTime` 更可靠，
  且能解决同秒批量打开的排序抖动问题。
- 链上索引不一定连续覆盖全部值：实测根键 8 个值全部在链上，但**不要假定如此**，
  未出现在链上的值应排在最后。

#### 单条记录的字节布局（实测，以 152 字节的 `value[7]` 为例）

```
0x00  [UTF-16LE 文件名][00 00]              26 字节  "新建 文本文档.txt"
0x1A  [元数据: 运行计数 + 字符串长度 + 填充]  20 字节
0x26  [UTF-16LE 文件名 + ".lnk"][00 00]     36 字节
0x4A  [ITEMIDLIST: cb=0x0050 ... 00 00]     76 字节
0x96  缓冲区结束（152 字节）
```

十六进制取证（关键片段）：

```
0000  b0 65 fa 5e 20 00 87 65 2c 67 87 65 63 68 2e 00   .e.^ ..e,g.ech..
0010  74 00 78 00 74 00 00 00 7e 00 36 00 00 00 00 00   t.x.t...~.6.....
0040  6e 00 6b 00 00 00 50 00 09 00 04 00 ef be 00 00   n.k...P.........
```

#### PIDL 偏移的正确取法（实证结论）

**不要用启发式扫描找 PIDL 起点。** 实测过两种启发式（"首个偶数偏移且 cb 合理且
type 字节已知"）都会锁在文件名/元数据区内部的 `00 00` 配对上，导致解析结果为空。
扫描全部偶数偏移的实证结果显示：**唯一产生出正确完整路径的偏移是
"文件名终止符偏移 + 2"**。

正确规则：

```
pidlOffset = indexOfFirstDoubleNull(bytes) + 2
```

即紧跟在文件名 UTF-16LE 字符串的 `00 00` 终止符之后。实测该偏移在全部
8 条记录上均解析成功。

#### PIDL 解析的硬约束：解析出的是 `.lnk` 文件本身

实测 8 条记录全部解析为形如
`D:\Users\Public\Desktop\新建 文本文档.txt.lnk` 的路径 —— 即**记录指向的是 Recent
目录里那个 `.lnk` 文件在源目录的对应物，而不是文档本身**。

**因此 `RecentDocs` 不能作为 `TargetPath` 的来源。** 它的职责被限定为：

| 职责 | 说明 |
|---|---|
| 提供**访问顺序** | `MRUListEx` 是排序权威 |
| 提供**文件名 + 扩展名** | 用于与 Recent 目录的 `.lnk` 做匹配 |
| 提供**文件夹判定** | 是否出现在 `Folder` 子键 |

真实 `TargetPath` 仍必须由 `IShellLink` 从 `Recent\*.lnk` 解析 —— **双源的分工就此确定：
`RecentDocs` 管顺序与元数据，`.lnk` 管真实路径。**

#### 与源 A 的合并规则（实测可行）

实测 `.lnk` 命名规则为 `<源文件名>.<源扩展名>.lnk`，而 `RecentDocs` 的 name 字段
为 `<源文件名>.<源扩展名>` —— 两者**去掉 `.lnk` 后缀后完全一致**，可直接字符串匹配。

| 情形 | 处理 |
|---|---|
| 两源都有同名记录 | 路径取 `.lnk` 解析结果；排序取 `MRUListEx` 排名 |
| 仅 `RecentDocs` 有 | 解析 PIDL 得到 `.lnk` 路径；若该 `.lnk` 不在 Recent 目录，标记 `RegistryOnly` |
| 仅 `.lnk` 有 | 保留，排序回退到 `LastWriteTime` |
| 同名但不同目录（实测未出现） | 按 `.lnk` 的 `LastWriteTime` 就近匹配，UI 不做虚假精确承诺 |

#### 必须记录的踩坑清单（两条都是"看起来正常但会静默出错/崩溃"）

**1. `-shl` 对 `[byte]` 操作数会截断结果（PowerShell 5.1 实测）**

```powershell
$b = [byte[]]@(0xb0, 0x65)
$b[0] -bor ($b[1] -shl 8)              # => 176   (0x00B0)  错！
[int]$b[0] -bor ([int]$b[1] -shl 8)    # => 26032 (0x65B0)  对
```

`0x65 -shl 8` 在 `[byte]` 语义下静默变成 `0`。只有当字节 ≥ `0x80` 时
PowerShell 才提升为 `[int]`，所以**纯 ASCII 测试用例发现不了这个 bug**。
后果：CJK 文件名被解成垃圾，且后续 `cp < 0x20` 的控制字符检查一并失效。
C# 移植版不受此影响（`int` 提升是规范的），但脚本版必须逐项 `[int]` 强转。

**2. `new StringBuilder(600)` 会导致 `AccessViolationException` 崩溃进程（实测）**

```csharp
// 崩溃：Capacity=600 但 MaxCapacity 仍为默认的 16，native 写入越界
var sb = new StringBuilder(600);
SHGetPathFromIDListW(pidl, sb);
// exit code 3221225477 (0xC0000005)

// 正确：同时指定 capacity 与 maxCapacity
var sb = new StringBuilder(260, 260);
```

这个坑会**原样出现在真正的 C# 代码里**，且表现为进程直接崩溃而非异常。
`SHGetPathFromIDListW` 的 `StringBuilder` 参数一律用 `new StringBuilder(260, 260)`。

**3. 推荐做法**：CJK 名字解码直接用 `Encoding.Unicode.GetString(bytes, 0, term)`，
不要手写逐字节循环。

#### 访问注意事项

- `RecentDocs` 在 `HKCU` 下 → 必须与目标用户同一身份运行；**不要以管理员身份运行**，
  提权到另一个令牌会读到不同的 hive。
- 只读，不写。清除历史绝不代劳（宁可引导用户去系统设置）。

### M4. FilterEngine

**方向修正：黑名单为主，白名单为可选开关。**

原计划把"白名单扩展名"作为第一道关，会误杀两类目标：

- **文件夹记录**没有扩展名 → 与"文件与文件夹混排"的核心定位**直接冲突**；
- 无扩展名文件（`LICENSE`、`Makefile`、`Dockerfile` 等）会被丢弃。

修订后的规则：

1. **黑名单路径**（始终生效）：`%Temp%`、`%TEMP%`、`C:\Windows\`、
   `C:\Program Files*`、`*\AppData\Local\Temp\*`、`$Recycle.Bin`。
2. **白名单扩展名**（**可选，默认关闭**）：仅在用户主动开启"仅显示文档类型"时生效；
   **开启时文件夹仍然保留**。
3. **去重**：按 `TargetPath`（不区分大小写、统一 `Path.GetFullPath` 归一化）保留最新一条。
4. **排序**：`OrderIndex`（来自注册表）优先；无则用 `LastAccessTime` 倒序；
   最终以 `TargetPath` 序作为 tiebreaker —— **必须有 tiebreaker**，
   否则批量打开（同一秒多条）会导致每次刷新顺序抖动，UI 视觉闪烁。
5. **数量上限**：默认 50，可配置。
6. **失效记录不丢弃**：`Validity != Valid` 的记录折叠进"失效记录 (N)"分组，
   用户可展开查看并手动清理。否则用户会困惑"我昨天打开的文件为什么消失了"，
   而软件给不出任何解释。

### M5. Watcher

**已实现于 `src/RecentDock.App/RecentWatcher.cs`，并已实测验证。**

- `InternalBufferSize = 64 * 1024`（文档上限 64 KB，必须 4 KB 倍数；默认 8 KB 偏小）。
- **必须订阅 `Error` 事件**：缓冲溢出时 `FileSystemWatcher` 会**静默丢失后续变更**，
  只发一次 blanket notification。处理方式：重建 watcher + 立即全量重扫。
- `Filter = "*.lnk"`，`NotifyFilter = FileName | LastWrite | CreationTime`，
  `IncludeSubdirectories = false`（否则子目录跳转列表会引发无意义刷新）。
- 事件响应加 **500ms 防抖**（事件合并），回调在**线程池线程**，必须封送回 Dispatcher。
- **注册表变化无法被 Watcher 感知** → 30 秒定时轮询兜底，同时负责跟踪状态翻转与
  相对时间文案刷新。

#### 实测验证结果（`RecentDock.exe --watchtest`）

```
probe template: 2512.15694v1.pdf.lnk
phase 1: single change ... written
refresh requested (#1)
phase 1: 1 refresh(es) after 516 ms          ← 单次变更 → 1 次刷新
phase 2: 6 rapid changes ...
phase 2: 18 raw event(s), 1 refresh(es) for 6 changes
OK: burst coalesced into a single refresh.   ← 18 个原始事件被合并成 1 次刷新
```

516 ms ≈ 500 ms 防抖窗口，符合预期；18:1 的合并比正是防抖要解决的问题（否则一次批量
操作会触发 18 次全量扫描）。

**测试自身的两个坑（都已修，值得记录）：**

1. **用空 `.lnk` 文件做探针不可靠** —— Windows 会回收格式错误的快捷方式，探针可能在测试
   中途被删除，导致"0 事件"的假失败。改为**拷贝一个真实存在的 `.lnk`** 作为模板。
2. **`Dispatcher.InvokeShutdown()` 不可逆** —— 第一版测试在 phase 1 结束时关闭了 dispatcher，
   phase 2 再调 `Dispatcher.Run()` 直接抛异常且被外层 catch 吞掉，现象与"防抖失效"完全一样。
   正确做法是用 **`DispatcherFrame` + `PushFrame`**：可反复进出，无需关闭 dispatcher。

**教训**：当测试报告失败时，先区分"被测代码坏了"和"测试脚手架坏了"。这次靠**加插桩**
（`RawEventObserved` 暴露原始事件数）在一步之内定位 —— 18 个原始事件说明 watcher 完好，
问题必然在测试侧。若没有插桩，很容易去错误地"修"一个本来正确的 watcher。

### M6. DockPanel（替代 `SetParent` 方案）

**已放弃 `SetParent(Progman/WorkerW)`**，原因固化为设计约束：

- Explorer 崩溃/重启（Windows 上很常见）会导致窗口被销毁或成孤儿，必须重新枚举重挂；
- 挂 `Progman` 后 Win+D 窗口会**跟着被隐藏**，与原意图相反；挂 `WorkerW` 会被壁纸层遮挡；
- 跨进程 `SetParent` 官方不推荐，输入焦点、z-order、混合 DPI 多显示器下行为异常；
- 调试成本不可控，会把项目拖入无底洞。

**替代实现**：

| 需求 | 实现 |
|---|---|
| 无边框 | `WindowStyle="None"` + `ResizeMode="CanResizeWithGrip"` |
| 不出现在 Alt+Tab / 任务栏 | `ShowInTaskbar="False"` + `WS_EX_TOOLWINDOW` |
| 点击不抢焦点（"贴桌面"体感的真正来源） | `WS_EX_NOACTIVATE` |
| 总在最前 | 可配置开关，**默认开**（沿用原风险的"提供总在最前选项"对策） |
| 拖动 | 标题区域 `DragMove()`，位置/大小持久化 |
| 半透明 | 优先 `WindowChrome` + `DwmExtendFrameIntoClientArea`；`AllowsTransparency="True"` 走软件渲染，长列表滚动掉帧，**谨慎使用** |

**列表性能**：

- 刷新时**不要整体替换 `ItemsSource`**（会重置滚动位置）。用 `ObservableCollection`
  按 `TargetPath` 做 key 的差量更新。
- 图标：`SHGetFileInfo(SHGFI_ICON | SHGFI_SMALLICON)` 取 HICON，
  用 `Imaging.CreateBitmapSourceFromHIcon` 转换，**按扩展名缓存**，
  并对每个 HICON 调用 `DestroyIcon`。不缓存会导致滚动时疯狂分配 GDI 对象。
  避免 `System.Drawing`（.NET 8 已限制且与 WPF 互操作麻烦）。
- 相对时间需定时器（60 秒）刷新文案。

**交互语义澄清（必须写入 README）**：

- "从列表移除" —— **不要删用户的 `.lnk` 文件**。改为写入 RecentDock 自己的忽略列表
  （持久化在配置目录）。写用户数据目录风险高且不可撤销。
- "打开所在位置" —— `explorer.exe /select,"<path>"`。
- 单击打开 —— `ShellExecute`；目标已失效时提示"文件可能已被移动或删除"，
  并提供"定位"入口。

### M7. Config & Tray

**已实现并验证。**

| 项 | 实现 | 状态 |
|---|---|---|
| 单实例 | `Local\RecentDock.SingleInstance` 命名 Mutex；第二次启动通过 `PostMessage` 通知已有实例显示面板，然后自身退出 | ✅ 实测 |
| `ShutdownMode` | `OnExplicitShutdown` —— 默认的 `OnLastWindowClose` 会让关闭面板时整个托盘程序退出 | ✅ |
| 托盘图标 | WinForms `NotifyIcon`（WPF 无内置托盘，且计划禁止第三方包）。图标从**自身 exe** 经 `Icon.ExtractAssociatedIcon` 提取并 `Clone()`，避免重复打包同一份图 | ✅ |
| 关闭行为 | 点 ✕ **隐藏**而非退出：托盘常驻工具的预期行为。退出走托盘菜单 | ✅ |
| 幽灵图标 | `Dispose` 中先 `Visible = false` 再释放，且 `Icon` 单独 `Dispose` | ✅ |
| 双击/左键托盘图标 | 切换面板显示 | ✅ |
| 开机自启 | `HKCU\...\Run`，值写**带引号的 exe 路径**（路径含空格时不加引号是众所周知的可利用点，会被安全软件标记） | ✅ |
| 配置写入/读取 | 见下方"未实现" | ⏳ 未做 |
| 兜底快照 | 见下方"未实现" | ⏳ 未做 |

#### 实测验证

```
=== single-instance test ===
after first launch, RecentDock processes: 1
after second launch, RecentDock processes: 1     ← 没有产生第二个面板/托盘图标
  second process exited: True                    ← 第二次启动立即退出
```

#### 本轮踩到的三个坑

**1. `UseWindowsForms` 导致 `Application` / `MessageBox` 命名歧义（CS0104）**

WPF 与 WinForms 各有一个 `Application` 和 `MessageBox`。用**全局别名**一次解决，
而不是在每个调用点加限定：

```csharp
global using Application = System.Windows.Application;
global using MessageBox = System.Windows.MessageBox;
```

**2. `UseWindowsForms` 触发分析器 WFAC010，禁止 manifest 里的 DPI 声明**

```
error WFAC010: 从 app.manifest 中删除高 DPI 设置，并通过 ApplicationHighDpiMode 项目属性进行配置
```

分析器无法分辨"WinForms 只用于 `NotifyIcon`、真正的可视界面是 WPF 窗口"。
解决方式：manifest 里移除 DPI 声明，改为**运行时调用
`SetProcessDpiAwarenessContext(PER_MONITOR_AWARE_V2)`**（必须在创建任何窗口之前），
同时设置 `ApplicationHighDpiMode` 让分析器满意。

**3. `.csproj` 的 XML 注释里不能出现 `--`**

写注释时顺手写了 `--selftest`，导致 `MSB4025: XML 注释不能包含 "--"`，**项目文件直接加载失败**。

#### 交互约定（已按用户要求调整）

| 操作 | 行为 |
|---|---|
| **双击**条目 | 打开目标文件/文件夹 |
| **右键**条目 | 打开 / 打开所在位置 / **从列表移除** |
| 左键单击托盘图标 | 显示或隐藏面板 |
| 点面板 ✕ | 隐藏到托盘（不退出） |
| 托盘菜单 → 退出 | 真正退出 |

"从列表移除"会**删除 `Recent` 目录中对应的 `.lnk`**（等同资源管理器自己的"从列表中移除"），
**不动目标文件本身**。因为这是对用户配置目录的写操作，实现时：先弹确认框、文案明确说明
"文件本身不会被删除"、删除失败的单条跳过而不中断整批。
同理还有"清理失效"按钮，仅当存在失效记录时才显示。

注意 `RemoveMissingTargets` **不把"不可达"当作"已失效"** —— 离线网络共享的目标必须保留，
否则清理会静默丢弃有效记录。

#### 尚未实现（诚实标注）

M7 范围内原列的两项已完成，见下节。当前唯一未兑现的交付项是**面向用户的 README**
（计划第六节要求，含隐私说明与"不保证与资源管理器完全一致"的免责）。

#### 配置持久化与首屏快照（已完成）

状态位置：`%APPDATA%\RecentDock\`（**不是**安装目录 —— framework-dependent 部署可能位于
只读位置，且每用户状态不该和二进制放一起）。

| 文件 | 内容 |
|---|---|
| `config.json` | 窗口位置/大小、数量上限、是否显示失效项、是否启用玻璃背景、是否启动即隐藏 |
| `snapshot.json` | 上次扫描结果，用于首屏秒开 |

**原子写入**：先写 `.tmp` 再 `File.Replace`。直接 `File.WriteAllText` 在崩溃时会留下半截
JSON，下次启动读失败。读取全程容错，损坏的 `config.json` 会被**改名为 `.corrupt` 保留**
（供诊断），然后回退默认值 —— 绝不能因为自己的配置文件坏了就启动失败。

**位置校验是被两个测试逼出来的**：`Sanitized()` 会在位置跑出虚拟桌面时丢弃它。
拔掉显示器后保存的坐标可能落在屏幕外，**面板将永远不可见且无法找回**。
规则要求窗口在两个轴上都至少有 80px 在屏内：
`Sanitize_SliverOnScreen_IsDiscarded` 覆盖"只露一条缝"的情形 —— 技术上可点，但**标题栏
（拖拽手柄）在屏外，用户无法把它拖回来**，所以也必须丢弃。
负坐标（显示器在主屏左侧）由 `Sanitize_NegativeVirtualScreenOrigin_IsHonoured` 保护。

**首屏快照**三条规则：

1. **空结果不写入** —— 缓存"没有记录"会让下次启动先画出空面板，正是快照要避免的闪烁。
   保留旧快照更好，因为真实结果一秒内就会到达。
2. **恢复的条目标记为 `Unreachable` 而非 `Valid`** —— 快照无法知道目标是否还存在，
   声称有效会让过期行短暂地表现为可点击的真实条目。
3. **超过 7 天不显示** —— 比"先闪一下空面板"更易误导。

**实测验证：**

```
config  : Left=724 Top=260 Width=720 Height=560
窗口实际 : Left=724 Top=260 Width=720 Height=560   ← 精确还原

snapshot 中 LastAccessTime = 17:36:09（打开时间），CapturedAt = 17:44:56（保存时间）
  → 时间戳被正确保留，相对时间不会全部变成"刚刚"
```

**发现并修复的 bug：持久化不能只挂在退出路径上**

第一版把 `SaveState()` 只放在 `OnClosed`。但**✕ 是隐藏而非关闭**，`OnClosed` 根本不执行 ——
实测 `WM_CLOSE` 后进程仍在运行，且**状态目录完全没有创建**。若用户从不使用托盘菜单退出
（关机时进程被直接终止），窗口位置和快照将永远丢失。

修复：`SaveState()` 改为在**隐藏窗口时、`ExitApplication` 时、`OnExit` 时**三处调用，
且是幂等的。状态保存的机会必须有多条，不能依赖单一退出路径。

#### 半透明玻璃主题（已完成）

**两个显而易见的做法都是错的**，这一点值得记下来：

| 做法 | 为什么不行 |
|---|---|
| `AllowsTransparency="True"` + `WindowStyle="None"` | 强制 WPF 走**软件渲染**（长列表掉帧），且 WPF 自行合成窗口，**DWM 材质无法透出** |
| `WindowChrome`（改动前的做法） | `GlassFrameThickness` 让客户区**不透明**，DWM 背景被画在一块不透明表面之后，等于没开 |

正确做法：`WindowStyle="None"` + 窗口 `Background="Transparent"`，**不加 `AllowsTransparency`**，
让 DWM 在客户区后方绘制亚克力材质；XAML 中的面板用半透明色控制材质透出的强度。

代价是**窗口装饰全部要自己补**：

- `WindowChrome` 被移除后没有任何东西提供缩放边框 → `WindowResizer` 用
  `WM_NCHITTEST` 实现（这是 Windows 自身的契约）。注意 `lParam` 里的屏幕坐标是
  **两个有符号 16 位值**，必须先转 `short`，否则主屏左侧显示器上的负坐标会算错。
- 圆角由 `DWMWA_WINDOW_CORNER_PREFERENCE` 提供。

**版本门槛**：`DWMWA_SYSTEMBACKDROP_TYPE` 需要 Windows 11 22H2（build ≥ 22621）。
更早的版本用另一套未公开属性，已失效，因此直接回退到半透明面板。
`Environment.OSVersion` 对 Windows 11 报 `10.0`，**只有 build 号可用**。

**实测本机支持**：`build 10.0.22631, acrylic supported: yes`（Windows 11 23H2），
亚克力真实生效。自检输出包含这一行，便于日后排查。

alpha 预算（这也是 Windows 10 上**唯一**的外观来源，所以必须自身可读）：

```
0xE6 (90%) 外层底色
0xD9 (85%) 标题栏/状态栏
0xCC (80%) 列表区
```

行悬停/选中也保持半透明（`#1FFFFFFF` / `#334C8DF6`），否则悬停会在玻璃上"打出一个不透明的洞"。
`ContextMenu` 默认是不透明的，已一并替换 —— 玻璃面板旁边弹出不透明菜单看起来像渲染 bug。

**测试**：`StorageTests` 共 22 条，覆盖默认值、字段往返、损坏文件隔离、原子写入无残留、
以及上面全部位置/尺寸边界。测试通过 `AppPaths.StateDirectoryOverride` 写入临时目录 ——
**测试套件若覆盖用户真实配置，比没有测试更糟**。

#### 玻璃背景排查：DropShadowEffect 遮住 DWM 材质（已修）

**现象**：浅色玻璃主题下，文字下方是一块黑色，降低透明度只透出更多黑色，而不是桌面。

**排查过程值得记录，因为"设置全对但画面不对"是最难查的一类问题。**

第一步的诊断全部报正常：

```
renderTier=2, hwAccel=True, dwmComposition=True, win11_22H2+=True,
windowBg=#00FFFFFF, backdropAttr=True, cornerAttr=True
backdrop mica: set=True,  backdrop acrylic: set=True
```

窗口全透明、硬件加速开启、DWM 合成开启、版本门槛满足、材质属性设置成功。
**每一项都对，现象却相反。** 这说明所有设置类断言都无法区分两种原因：

1. 材质根本没生效
2. 材质生效了，但被别的东西盖住

**转向读取渲染结果**才把两者分开。做法：`PrintWindow(PW_RENDERFULLCONTENT)`
把窗口渲染到内存位图，采样圆角外缘、标题栏、内容区、状态栏四点的颜色。

```
角落 D3D3D3 / 标题栏 F2F2F2 / 内容区 CCCCCC / 状态栏 8C8C8C
角落 == 内容区: False
```

**角落是浅灰而不是 `000000`** —— 若窗口后面有不透明黑层，这里必然是全黑。
同时同一窗口在屏幕外（背景较暗）采样得到 `content=707070`，正常位置得到
`CCCCCC`：**颜色随背景变化，证明材质在透出背后的东西**。

**根因**：根 `Border` 上的 `Border.Effect`(`DropShadowEffect`)。
`Effect` 让 WPF 把该元素渲染到中间图面，在无边框、依赖 DWM 材质的窗口里，
该中间图面以不透明黑合成，把材质整块遮住。

**为什么之前没发现**：深色主题时期面板本身就是近黑色，遮住了也看不出来；
换成浅色白玻璃才暴露 —— 而当时误判为"颜色没改对"。

**修复**：从 XAML 移除 `Border.Effect`，改为按配置 `ShowDropShadow`（默认
false）在代码中施加，便于按机器验证。

**方法论**：设置类断言全部通过却与现象矛盾时，不要再加设置类断言 ——
去读渲染结果。这是本次唯一有效的转折点。

#### 用户报告的 bug：重启后所有条目显示"不可达"（已修）

**根因是差量刷新与快照占位值的交互。** 快照恢复的行被标记为 `Unreachable`（这是刻意的，
见上文），而 `ApplyItems` 为保留图标缓存而**复用行对象**时，**只加进了列表、从未更新
`Validity`** —— 真实扫描结果回来了，行上的占位值却永远留着。

修复：`RecentItemViewModel.UpdateValidity()` 原地更新有效值，并**显式重发
`Validity` / `IsMissing` / `ValidityLabel` 三个属性通知**（后两者是由 `Validity` 计算出的，
不重发则文字和删除线不会跟着变）。`ApplyItems` 在复用行时调用它。

顺带把 `Unreachable` 的文案从"不可达"改为"上次结果" —— 这个枚举被复用于两种情形
（真的离线网络目标、以及快照占位行），原措辞在快照语境下不准确。

**这个 bug 值得记的是一个模式：只要性能优化引入了"复用状态"，就必须同时定义"如何更新状态"。**
差量刷新本身是对的（避免闪烁、保留滚动位置），缺的是复用路径上的更新职责。

#### 加插桩时发现的自身问题

为定位上述 bug 引入了两层诊断，各自暴露了一个错误，都不是产品代码的问题：

**1. 叠加检查的模型写错了。** 第一版把"快照 ∪ 真实结果"当作叠加结果，于是为
**只存在于快照、实际会被删除的行**报了假失败（`2 row(s) still Unreachable`）。
真实语义是**替换**：只保留真实结果，快照独有的行直接丢弃。
**诊断代码同样需要被质疑** —— 它报的"失败"可能只是它自己建模错了。

**2. `AttachParentConsole` 会让输出捕获静默失效。** WinExe 无自带控制台，该函数通过
`Console.OpenStandardOutput()` 重绑定 stdout —— 但那打开的是**控制台设备**而非父进程的
重定向管道，于是调用方 `$x = & exe ...` 拿到 0 行。试过用 `Console.IsOutputRedirected`
做守卫，结果**破坏了原本可用的 `Select-Object` 管道**，遂回退。

最终方案：自检**同时镜像写一份日志文件**，并且**用带 BOM 的 UTF-8** 写入 —— 文件名是中文，
没有 BOM 时 `Get-Content` 会按 ANSI 代码页解析，输出乱码。日志路径由
`Path.GetTempPath()` 决定，便于沙箱等自定义 TEMP 的环境读取。

#### 用户实测反馈修复的两个 bug（重要）

**Bug 1：右键"从列表移除"抛异常 —— `LinkResolver requires a COM-initialized STA thread`**

根因是一条很容易漏的线程链：`await ScanAsync()` 的后续代码在**线程池线程**上恢复
（`ScanAsync` 内部用了 `ConfigureAwait(false)`），而 `OnRemoveClick` 直接调用
`RecentEntryCleaner`，该线程没有 COM apartment，于是 `LinkResolver` 的守卫抛异常。

**但真正的设计缺陷在 `LinkResolver` 一侧**：它把"调用方必须自己安排 STA"当作契约，
而这个契约容易被违反、且**以运行时异常的方式失败**。已改为**按需自行初始化 COM**：

```csharp
// StaComScope.EnsureInitialized()：任何线程可用（UI 线程 / 专用 STA 扫描线程 / 线程池）
if (!StaComScope.EnsureInitialized())
{
    return null;
}
```

教训：**当一个条件被调用方自身就能满足时，不要把它变成调用方的义务。** 尤其是失败表现为
运行时异常而非编译错误时 —— 这类契约在代码评审里也看不出来。

回归测试 `RecentDock.exe --removecomtest` 复现了出错条件（`Task.Run` 保证线程池线程）
并验证不再抛异常，且**非破坏性**（传入不存在的路径，解析全部 `.lnk` 但一条都不删）。

**Bug 2：左键单击不高亮，只有双击才移动选中**

根因是我在行模板上把 `e.Handled = true`，**在 ListViewItem 看到事件之前就把它截断了**，
于是选中逻辑永远不执行。

修复：处理器移到 `ListView.PreviewMouseLeftButtonDown`，
**只在双击打开时才设 `Handled`**，单击放行让 ListView 正常处理；
同时用 `ItemsControl.ContainerFromElement(ItemList, e.OriginalSource)` 显式定位行
（点击子元素如文件名的 `TextBlock` 时，事件不一定到达容器）。

顺带发现并修正了一处**文案与实际行为不符**：`RemoveByTargetPath` 会删除**该目标的全部
记录**（测试实测：探针 + 原记录 = 删 2 条），而确认框原文写的是"移除这条记录"。
措辞已改为"会删除该文件在最近列表中的记录（若存在多条则一并删除）"。

---

## 三、修订后的仓库结构

```
/src/RecentDock              # WPF 主程序（M5/M6/M7）
/src/RecentDock.Core         # 可复用核心（M0–M4）
  ├── EnvironmentProbe.cs
  ├── RecentScanner.cs
  ├── LinkResolver.cs        # IShellLinkW 封装
  ├── RegistryScanner.cs     # RecentDocs + MRUListEx 解析
  └── FilterEngine.cs
/tests/RecentDock.Core.Tests # 新增，见下
/docs
```

**`/tests/RecentDock.Core.Tests` 是本轮最值得新增的一项。** 可测试边界全是纯逻辑、
无 UI 依赖，测试成本极低而收益极高：

- `MRUListEx` 解析（含 `0xFFFFFFFF` 终止、乱序索引、空键、截断缓冲区）→ **已实现**
- `RecentDocs` 单值解析（UTF-16LE 文件名、CJK 名、PIDL 偏移、控制字符拒绝）→ **已实现**
- 三态判定矩阵（跟踪开关 × 两源计数，全 9 种组合）→ **已实现**
- 过滤规则（黑名单、白名单开关不影响文件夹）→ 待实现
- 去重（大小写、路径归一化）→ 待实现
- 排序 tiebreaker 稳定性 → 待实现
- 有效性判定（UNC / 不存在的盘符 → `Unreachable`）→ 待实现
- 配置反序列化容错（截断 JSON、缺字段、类型错误）→ 待实现

**测试夹具取自真机 hex dump**，而非按假设构造。`RecentDocsRecordParserTests` 里的三个
字节数组是实测记录的逐字节转写（152 / 162 / 130 字节），并有一个 `HasExpectedLength`
测试专门守护夹具本身 —— 抄错一个字节就会让后面的断言失去意义。

### 验证状态：已编译并跑通（2026-09，SDK 8.0.425）

核心库与测试套件**编译通过（0 警告 0 错误，`TreatWarningsAsErrors` 开启）**，
**36 个测试全部通过**：

```
已通过! - 失败: 0，通过: 36，已跳过: 0，总计: 36，持续时间: 20 ms
```

（从 33 增至 36，新增的三条覆盖设置页面 URI 的修正与诊断方法。）

#### 端到端验证：真实数据 + 真实文件事件

除单元测试外，另有三个可复现的运行时检查，均针对**活体系统**：

| 命令 | 验证内容 | 结果 |
|---|---|---|
| `RecentDock.exe --selftest` | 双源扫描全链路：COM 解析、注册表解析、join、排序 | 13 条记录，`unresolved = 0` |
| `RecentDock.exe --watchtest` | `FileSystemWatcher` 触发与 500ms 防抖合并 | 18 raw events → 1 refresh |
| `dotnet test` | 纯逻辑：解析器、三态矩阵、URI | 36/36 |

`--selftest` 的真实输出确认了几个此前未验证的分支：

```
1. [rank 1] [DIR ] 推免文件                    D:\Users\Public\Desktop\推免文件
2. [rank 2] [FILE] 附件4：学术诚信承诺书.doc    D:\Users\Public\Desktop\推免文件\...
```

**文件夹记录混排、中文名（含全角冒号）、跨目录 join 全部正确**。这是 M1/M4 分支
第一次在真实数据上被验证，此前只跑过文件记录。

#### 变异测试：证明测试不是空转

"全绿"本身不构成证据 —— 若断言没真正绑到被测代码，同样会全绿。因此做了一次变异
验证：把 `RecentDocsRecordParser` 的 `pidlOffset` 故意改为 `nameFieldLength - 4`，
**结果 5 个测试失败**；回滚后恢复通过。这证明断言确实作用于解析逻辑。

#### 首次编译暴露并修复的三处缺陷

**1. `GetPath` 缺少 `[PreserveSig]`（CS0029）**

```csharp
// 错：CLR 把返回的 HRESULT 当错误通道吞掉，方法被声明为 void，
//     于是"S_FALSE 要显式检查"根本无法实现，编译报 CS0029
void GetPath(StringBuilder pszFile, int cch, IntPtr pfd, uint fFlags);

// 对
[PreserveSig]
int GetPath(StringBuilder pszFile, int cch, IntPtr pfd, uint fFlags);
```

典型陷阱：**注释写对了，代码却拿不到返回值**。凡需检查 HRESULT 的 COM 方法都必须显式标
`[PreserveSig]`。

**2. `Microsoft.Win32.Registry` 不该是 NuGet 包**

原 `PackageReference Version="5.0.0"` 导致 `NU1100`。该程序集由 **Windows Desktop 共享
框架**提供，故目标框架改为 **`net8.0-windows`**，依赖归零，也如实反映"本库天生仅限
Windows"（`IShellLink`、`RecentDocs`、Recent 目录）。测试项目同步改为 `net8.0-windows`。

**3. NuGet 包源为空导致 `NU1100`（68 毫秒即失败）**

本机 `%APPDATA%\NuGet\NuGet.Config` 只有 `<configuration />`，一个源都没有，于是还原
"瞬间失败"而非超时。已在仓库根新增 `nuget.config` 显式声明 `nuget.org`，**随仓库走**，
不改用户级配置，构建可复现。

### 环境约束（会反复踩，务必记录）

- **DSH 沙箱会拦截 schannel 的加密凭证访问**，导致 .NET 的一切 HTTPS 失败
  （`AuthenticationException: Authentication failed`，任何 TLS 版本、HTTP/1.1 均无效），
  因此 `dotnet restore` 报 `NU1301`。只看到 `curl.exe`/`Invoke-WebRequest` 失败时容易
  误判为网络问题；实测**越权（full access）后 13 秒还原成功**，根因确认为沙箱。
- `dotnet` CLI 启动会写 `C:\Users\<user>\.dotnet`，被沙箱拒绝
  （`UnauthorizedAccessException` 写 `*.toolpath.sentinel`）。构建需重定向：

```powershell
$env:DOTNET_CLI_HOME = "$root\.dotnet-home"
$env:NUGET_PACKAGES  = "$root\.nuget\packages"
$env:TMP = "$root\.tmp"; $env:TEMP = "$root\.tmp"
```

- 本机 schannel 在**沙箱外**同样不可用（`SEC_E_NO_CREDENTIALS`），HTTPS 下载需走
  `tools/parallel-download.js`（Node/OpenSSL）。该脚本与项目无关，验证完可删。

### 构建与测试

```powershell
dotnet build src\RecentDock.Core\RecentDock.Core.csproj
dotnet test  tests\RecentDock.Core.Tests\RecentDock.Core.Tests.csproj
```

首次需要 SDK：`winget install Microsoft.DotNet.SDK.8`（或从官方页下载 `.exe`/`.zip`）。

---

## 四、v1 实施顺序（建议）

1. `EnvironmentProbe` + 空状态引导页 ← **先做这个**，它验证了产品最核心的三态逻辑。
2. `RecentScanner` + `LinkResolver`（含 COM 规范与超时策略）。
3. `FilterEngine` + `SortResolver`（含 tiebreaker）。
4. WPF 面板（`SetParent` 替代方案 + 图标缓存 + 差量刷新）。
5. Watcher（含 `Error` 降级与轮询兜底）。
6. 托盘 + 配置 + 单实例 + 自启。
7. `RegistryScanner` 接入，作为排序权威与遗漏补充。

**验证状态：已完成**（见第〇节两次实测）。`.lnk` 命名规则、`MRUListEx` 实际内容、
文件夹记录所在子键、`RecentDocs` 单条字节布局与 PIDL 偏移取法**均已由真机数据确认**，
`RegistryScanner` 可以按 M3 的规格直接开工。

**取证工具**（保留在仓库中，供后续回归验证）：

| 工具 | 用途 |
|---|---|
| `tools/verify-dualsource.ps1` | 开关切换（`enable`/`disable`）+ 两源整体取证（`analyze`）。含状态三态判定 |
| `tools/analyze-recentdocs.ps1` | `RecentDocs` 深度解析：`MRUListEx` 链、记录布局、PIDL 偏移定位与路径解析、全桶遍历 |

两个脚本都依赖同一组已修正的解析例程（`[int]` 强转、`Encoding.Unicode`、
`StringBuilder(260, 260)`）。修改其一时须同步另一处。

---

## 五、边界与免责（README 必写）

- 不做桌面图标整理、不接管桌面图标。
- 不做快捷键呼出的启动器。
- **不修改系统"最近打开项目"设置**（仅在用户主动点击时打开设置页面，不代为修改注册表）。
- 不读取、不上传任何用户数据（纯本地，无网络代码）。
- 不依赖任何第三方桌面整理工具。
- **不保证与资源管理器"最近使用"列表完全一致**：`.lnk` 仅在应用调用
  `SHAddToRecentDocs`（或使用公共文件对话框）时生成，绿色软件、部分 Electron/自绘 UI
  程序、命令行工具打开的文件**永远不会**出现在其中。列表天然不完整，这是数据源属性，不是缺陷。
- **系统关闭记录功能时，本程序无法补全历史**（见第〇节实测结论）。
- 会写入 `HKCU` 的 `Run` 键（仅在用户开启"开机自启"时）。
- 许可证 MIT。
