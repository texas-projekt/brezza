# Brezza 项目评估报告

> 评估日期：2026-10-19。初版基于只读静态分析；本文件同时作为修复进度存档。每项结论保留原始证据位置；状态在修复后更新。
>
> 环境依赖与构建已验证：Release 构建 0 警告 / 0 错误，167 个单元测试在 net10.0 与 net48 下各通过一次。

## 修复进度

| 编号 | 问题 | 严重度 | 状态 | 验证 |
|---|---|---|---|---|
| A1 | 原生 subclass/WndProc 回调缺少异常兜底，异常可终止进程 | P0 | 已修复 | 构建通过；Smoke 连续 3 次通过 |
| A2 | 地址栏被 `RenderState` 覆盖，展开建议抹掉输入 | P1 | 已修复 | 构建通过；Smoke 连续 3 次通过 |
| A3 | 关闭守护 timer ID 3 无人处理，Shutdown 卡死时无法退出 | P1 | 已修复 | 构建通过 |
| A4 | WebView2 controller 创建失败不清理、host 永久不可用 | P1 | 已修复 | 构建通过；未做故障注入 |
| A5 | WebView2 环境初始化失败被永久缓存 | P1 | 已修复 | 构建通过；未做故障注入 |
| A6 | 关闭一个仍在创建且创建失败的标签会抛异常并卡在 Closing | P1 | 已修复 | 新增 Core 测试，134 个测试通过 |
| A7 | 持久化读取到损坏文件后可能被默认值覆盖 | P1 | 已修复 | 新增 Persistence 测试，134 个测试通过 |
| A8 | `README.md` 的截图工具说明与脚本不符 | P2 | 已修复 | 人工核对脚本参数 |
| A9 | `capture-baseline.ps1` 未知 `-Only` 静默 PASS | P1 | 已修复 | PowerShell 语法检查 |
| A10 | `package.ps1` 依赖当前目录；启动验证失败后可能残留进程 | P1 | 已修复 | Package 阶段（跳过启动验证）通过 |
| S1 | Smoke 在关闭阶段触发原生崩溃（`ucrtbase.dll`, `0xc0000409`） | P1 | 已修复 | 根因在 smoke 驱动：对话框未建完就 PostMessage(IDOK)；改为稳定等待 + BM_CLICK 后连续 3 次通过 |
| B1 | 私密 profile 待删目录只存在内存，崩溃后不清理 | P1 | 已修复 | 启动孤儿扫描 + owner marker；Smoke 3 次通过，未做崩溃注入 |
| B2 | 导航失败无代次，旧失败可误标新成功 | P2 | 已修复 | 新增 navigation id 事件与 2 个测试；Smoke 变更后连续 3 次通过 |
| B3 | 睡眠不检查活动下载；Dispose 先解绑下载事件 | P1 | 已修复 | 策略/运行时测试；Smoke 下载、暂停/恢复/取消与关闭路径 3 次通过 |
| B4 | 下载路径未处理保留名/超长名/异常 | P1 | 已修复 | 新增 Paths 测试；下载事件异常兜底构建通过 |
| B5 | 主窗口没有对话框键盘环，Tab 无法到工具栏/地址栏 | P1 | 已修复 | 主窗口仅翻译 Tab；构建通过，Smoke 未专门覆盖 Tab |
| B6 | 启动与关闭竞态；无 `WM_QUERYENDSESSION` | P1 | 已修复 | 初始化取消门 + 会话结束同步保存；Smoke 启动/恢复/关闭 3 次通过 |
| B7 | 发布包在 CI 中从不做启动验证 | P1 | 不处理（已接受） | 需要自托管交互式 runner 或独立启动探针；当前不以自动化门禁覆盖，保留人工发布检查 |
| B8 | 无 `global.json` / lock file；net48 无单测目标 | P1 | 已修复 | 双目标 127 测试通过；lock file 已生成 |
| B9 | 高对比度、UIA、DPI 初值等可访问性问题 | P2 | 部分修复（UIA 不处理） | 高对比度/焦点框/DPI 已修；UIA 行级可达性明确不处理 |
| B10 | 文档与 schema/包内容/面板数量漂移 | P2 | 已修复 | 已同步 ARCHITECTURE/PRODUCT/ROADMAP/ADR |
| B11 | Core 运行时字典在并发激活下无锁，net48 单测偶发 NRE | P2 | 已修复 | 改用并发字典 + restore 锁；双目标连续 5 轮通过 |
| B12 | 单个非法设置字段导致整份设置回默认 | P2 | 已修复 | 字段级模板回退；新增测试，134 双目标通过 |
| B13 | 坏下载 ticks/空 interrupt reason 可让读取抛异常 | P2 | 已修复 | 增加记录校验；新增测试，134 双目标通过 |
| B14 | 缺键显示 key；切换语言后通知仍是旧语言 | P3 | 已修复 | 英文逐键回退 + 通知重发；构建通过 |

状态说明：`已修复` 表示代码与最小验证已完成；`待处理` 表示已确认但本轮未改；`不处理（已接受）` 表示明确接受当前风险；`需决策` 表示需要产品/架构选择。

未立项的后续任务见 [BACKLOG](BACKLOG.md)。

### 本轮验证记录（2026-10-19）

| 检查 | 结果 |
|---|---|
| `dotnet build Brezza.slnx -c Release` | 成功，0 警告 / 0 错误 |
| `tools/test.ps1 -Stage Unit` / 连续 3 轮 `dotnet test` | net10.0 167 通过；net48 167 通过；语言包键/占位符校验通过；覆盖率 87.6% 行 / 76.7% 分支，高于 85% / 70% 下限 |
| `dotnet restore Brezza.slnx --locked-mode` | 通过 |
| `tools/test.ps1 -Stage Smoke` | PASS；W6/W9/W13/W15 变更后通过 1 次；W5 后 1 次；W4 后 1 次；W1 后连续 2 次 |
| `tools/test.ps1 -Stage Visual -VisualOnly NewTabDark` / `ToolbarZhHant` | PASS；控件 ID 反射、条件等待和繁体工具栏验证通过 |
| `tools/capture-baseline.ps1` / `tools/package.ps1` 语法 | 解析通过 |
| `tools/test.ps1 -Stage Package -SkipLaunchVerification` | PASS，产出 ZIP + SHA256 sidecar；发布包按 ADR 0013 保持 unsigned |
| `tools/test.ps1 -Stage Package`（启动验证） | PASS，解压后的应用启动并创建原生控件与 WebView2 surface |
| 单实例手工验收 | 首进程窗口出现后启动第二进程，第二进程 10 秒内以 0 退出，首进程继续运行 |

**S1 已定位并修复**：崩溃不是 WebView2 关闭路径，而是 smoke 驱动的文件选择器步骤——它在 `IFileDialog` 的控件尚未创建完成时直接向对话框 `PostMessage(WM_COMMAND, IDOK)`，偶发把原生对话框打崩（`ucrtbase.dll`, `0xc0000409`）。现在等待对话框稳定、校验 `IsWindow` 后向 OK 按钮发送 `BM_CLICK`，并给 cookie 面板的行数增加等待；Smoke 连续 3 次通过。之前 HEAD 也复现，是因为驱动代码同样存在该时序问题。

### 本轮修复明细

- **原生回调兜底**：`FlatButtonSet`、`FlatFieldSet`、`MainWindow`（标签栏、地址栏 subclass）、`InternalPanelWindow`、`MessageLoopSynchronizationContext` 的 native callback 全部在 try/catch 内执行，异常记录后回落到 `DefSubclassProc`/`DefWindowProc`。
- **地址栏草稿保护**：`MainWindow` 新增 `_addressEdited`/`_updatingAddressText`/`_activeTabId`。用户编辑后，`RenderState` 不再覆盖输入；切换标签会丢弃草稿；提交/Escape 清除草稿；展开建议时恢复当前草稿而不是 `_currentUrl`。
- **关闭守护**：`WM_TIMER` 识别 `ShutdownGuardTimerId`，20 秒未完成关闭时强制 `CompleteShutdown`。
- **WebView2 恢复**：controller 创建失败时关闭半初始化 controller、解除事件、清空失败任务；下一次导航重新创建。环境初始化失败不再永久缓存，私密 profile 重试复用同一目录。
- **Core 关闭路径**：`CloseAsync` 捕获创建失败，继续关闭 registry 并激活相邻标签；新增回归测试。
- **持久化隔离**：`AtomicXmlFile`、`SettingsStore`、`HistoryStore` 对无法反序列化的文件执行 best-effort 重命名为 `*.corrupt-<ticks>`；临时 IO 错误不隔离。新增测试。
- **工具与文档**：修正 README 的截图说明；`capture-baseline.ps1` 记录实际捕获场景并在 `-Only` 无匹配时报错；`package.ps1` 在项目根目录运行 pinned tool，并在 finally 中终止启动验证进程。

### 第二轮修复明细

- **下载路径硬化**：`BrowserDownloadPaths` 处理 CON/NUL/COM1/LPT1 等保留名、120 字符上限、扩展名保留、编号后缀不越界；下载事件对路径/目录创建失败取消该下载并记录，不再让异常穿出 WebView2 事件。
- **下载与睡眠**：`ITabHost.HasActiveDownload` 和 `TabHostSignals.HasActiveDownload` 把运行中/暂停的下载纳入 `TabSleepPolicy`；`WebView2TabHost.Dispose` 在解绑前把未完成下载上报为 `Interrupted`，避免 registry 永远停在 InProgress；`NavigateAsync` 更新 host 活动时间，后台导航的 host 不再绕过睡眠预算。
- **私密 profile 耐久清理**：factory 给每个私密 profile 写 `owner.txt`；启动时扫描 `PrivateSessions`，只清理 owner 进程已退出的目录，未知旧格式目录需超过 24 小时才处理，避免误删另一个实例的 profile。`TabRuntimeManager.CleanupOrphanedPrivateDataAsync` 在启动时触发该清理。
- **主窗口键盘**：`DialogKeyboard` 支持 `tabOnly` 注册；主窗口只翻译 Tab/Shift+Tab，地址栏的 Enter/Escape 仍走自己的 subclass。方形 caption 按钮也绘制焦点框。
- **高对比度**：`NativeTheme.Resolve` 在高对比度开启时返回由 `GetSysColor` 构建的系统色调色板，`WM_SETTINGCHANGE` 的 `Accessibility` 广播也会触发重新解析；固定品牌色不再覆盖系统对比度设置。
- **DPI 初值**：窗口创建后、首次 Show 前按当前显示器 DPI 把 1120×760 逻辑尺寸缩放，避免 125%/150% 下打开成偏小的窗口。
- **启动/关闭**：`InitializeAsync` 改为可观察的 `Task`，在每个关键 await 后检查 `_shutdownStarted`；`WM_CLOSE` 前保存逻辑抽为 `SaveBrowserData`，`WM_QUERYENDSESSION` 同步保存；`GetMessage` 返回 -1 时以错误退出；`CompleteShutdown` 清空 HWND，避免二次销毁。
- **构建可复现**：新增 `global.json`（10.0.400，latestFeature）、`packages.lock.json`（5 个项目）、`RestorePackagesWithLockFile`、CI `--locked-mode`、`ContinuousIntegrationBuild`；测试项目双目标 `net10.0;net48`，App 明确 x64 RID 且不改变输出目录。
- **并发确定性**：`TabRuntimeManager` 的 host/creation/活动时间/导航代次改用并发字典，`_restoring` 的“检查-启动-登记”放在同一把锁内；睡眠 sweep 的时间戳相同时按 tab id 排序。net48 单测连续 5 轮无失败。
- **设置与下载恢复**：非法搜索模板只回退该字段，不再丢弃语言/主题/启动行为；下载记录校验 start/completed ticks 和空 interrupt reason，坏记录跳过而不是抛异常。
- **本地化回退**：语言包缺键逐键回退到英文；切换语言后重新按当前 notice key 渲染通知，旧语言文案不再残留。
- **下载关闭语义**：host 关闭把未完成下载记为 `Interrupted` 并持久化；PRODUCT/ARCHITECTURE 已同步这一行为，私有下载仍不写入。
- **文档同步**：ARCHITECTURE 的 schema 4、隔离策略、包内容；PRODUCT 的 downloads 面板；ADR 0009/0010；ROADMAP 的 M6 阻塞说明；TESTING 的分层与 CI 说明。
- **Smoke 稳定性（S1）**：文件选择器步骤改为等待控件稳定、校验句柄后点击 `BM_CLICK`，不再向半创建的对话框发 `WM_COMMAND(IDOK)`；cookie 面板行数增加等待。Smoke 连续 3 次通过。
- **B2 导航代次**：`ITabHost.NavigationFailed` 改为携带 `NavigationId` 的事件；Core 记录每标签最新 id，旧失败不再覆盖新成功。新增“旧失败忽略 / 当前失败生效”两个测试。
- **Backlog 清理**：`CreateControls` 失败时中止窗口创建；`IFileDialog` 按真实 vtable 声明 `Advise`/`Unadvise`/`Close`；标签 active id 找不到时不再回退选中第一个；`SetWindowRgn` 失败释放 region；打包脚本校验 semver、允许覆盖并校验 `Brezza.exe.config`；下载记录按 path/ID 去重。
- **W1/W2/W3**：WebView2 进程失败时失效 controller、上报下载中断、Core 标 `Failed`，`Reload` 重建；私密下载采用方案 B（只排除记录，文件保留并写入文档）；打包生成 SHA256 sidecar，release 校验 semver、上传校验和并生成 GitHub attestation。
- **W8–W20**：favicon 负缓存；Smoke 失败清理 trap；manifest 记录 commit；coverlet 覆盖率（ADR 0012）；滚轮先请求 Core 再移动选中；面板静态窗口过程；Release 关闭 DevTools、Debug-only 脚本入口；Changelog 结构整理；默认下载目录改走 KnownFolder。
- **W4**：外部协议只允许用户手势触发的 `mailto`/`tel`/`sms`，脚本或未知自定义协议取消并显示通知；新增策略测试和 Core 传播测试。
- **W5**：弹窗要求用户手势，每 tab 2 秒冷却、全局 5 次/分钟；权限通知按 source+kind 30 秒去重。新增 Core 限流测试。
- **W6**：`DownloadRequestLimiter` 全浏览器最多 3 个活动下载，已知总大小超过 8 GiB 时在 WebView2 创建文件前取消；拒绝原因经 `DownloadBlocked` 事件显示本地化通知。新增配额测试。
- **W9**：Smoke origin 的 `Start-Job` 自行选择并绑定端口，在 `HttpListener.Start` 后写就绪文件；父进程不再“绑定后释放”端口。Smoke 通过。
- **W11**：visual capture 通过反射读取 `MainWindow` 的控件 ID；paint/menu/tab/window 条件替代布局 sleep；同时修复 PowerShell 5.1 下 `Add-Type` 不支持本地函数/`out var`/表达式方法的问题。`NewTabDark` 与 `ToolbarZhHant` 通过。
- **W12**：Unit 阶段强制 85% 行 / 70% 分支覆盖率，并校验三个语言包的键与占位符。
- **W13**：`SingleInstance` 使用 session 级命名 mutex；第二实例等待并激活已有 `BrezzaMainWindow` 后退出，避免第二个 WebView2 profile。手工双进程验收通过。
- **W15**：新增完整 `zh-Hant` 包和设置选项；Core `BrowserLanguagePolicy` 处理 `zh-TW`/`zh-HK`/`zh-MO` 别名并统一持久化为 `zh-Hant`；Visual 捕获繁体工具栏通过。
- **W3**：发布包保持 unsigned（ADR 0013）；SHA256 sidecar 与 GitHub build provenance 继续作为完整性依据，签名代码和证书 secret 不进入发布流程。

---

## 一、总体结论

| 部分 | 评分 | 结论 |
|---|---:|---|
| Core + Persistence | 6.5/10 | 分层清晰、原子写可靠，但故障路径与恢复策略不足 |
| 原生 Win32 壳层 | 6.5/10 | 边界与资源管理扎实，地址栏同步、键盘可达性、启停竞态是产品级缺陷 |
| WebView2 适配与安全 | 7.0/10 | 安全硬约束落实到位，创建失败恢复与私密数据清理有缺口 |
| 测试 / 工具链 / 文档 | 7.0/10 | 分层合理、文档量大，但 CI 不验证运行时、发布包零启动检查 |
| **综合** | **6.7/10** | 架构方向正确；主要风险集中在生命周期闭环、数据恢复和交付门禁 |

没有发现可远程越权执行脚本、消息桥直通等安全 P0。唯一 P0 级问题是原生回调缺少异常兜底，可能导致进程直接退出。

### 最值得先处理的 5 件事

1. 原生 subclass 过程加异常兜底。
2. 修复地址栏被状态刷新覆盖。
3. 补齐 WebView2 创建失败闭环。
4. 停止“读取失败即覆盖”。
5. 发布前真正启动一次产物。

---

## 二、评估方法

项目拆为四个独立部分并行评估，每个部分由只读子智能体做深度静态分析，主智能体抽查关键结论并运行构建/测试：

| 部分 | 范围 |
|---|---|
| Core + Persistence | `src/Brezza.Core`、`src/Brezza.Persistence`、Core 测试、相关 ADR |
| 原生 Win32 壳层 | `src/Brezza.App` 的窗口、控件、消息循环、面板、本地化、组合根 |
| WebView2 与安全边界 | `src/Brezza.WebView2`、App 中的 WebView2 交互、下载/弹窗/权限 |
| 测试 / 工具链 / 文档 | `tests/`、`tools/`、`.github/workflows/`、构建配置、`docs/` |

局限：不做动态复现；Smoke/Visual 需要交互式桌面；未做性能、内存泄漏、模糊测试或外部协议攻击面实测。

---

## 三、项目拆分与职责

| 部分 | 关键位置 | 职责 | 评价 |
|---|---|---|---|
| Core | `src/Brezza.Core` | 标签状态、导航、权限、隐私、下载状态机 | 无项目依赖，方向正确；`ITabHost` 偏宽 |
| Persistence | `src/Brezza.Persistence` | 设置、会话、历史、书签、下载的本地存储 | 只依赖 Core；每域一个 store，共用原子写 |
| WebView2 | `src/Brezza.WebView2` | controller 生命周期、导航/下载/权限事件适配 | 通过接口暴露值/事件/Stream，COM 未外泄 |
| App | `src/Brezza.App` | 原生 Win32 窗口、控件、消息循环、面板、本地化、组合根 | 功能完整，但 `MainWindow`/`BrowserShell` 过大 |
| 测试与交付 | `tests/`、`tools/`、`.github/`、`docs/` | xUnit、Smoke/Visual/Package、CI/发布、ADR | 分层合理，运行时验证未进门禁 |

优点：依赖方向严格、浏览器 UI 确为原生 HWND、消息桥禁用且权限默认拒绝、原子写与隐私过滤有明确设计。

---

## 四、分部分评估

### 4.1 Core + Persistence（6.5/10）

**优点**：状态变更入口收敛；session/download 是过滤投影；`AtomicXmlFile` 使用临时文件 + `Flush(true)` + `File.Replace`；隐私标签默认排除；核心状态机测试较扎实。

| 级别 | 位置 | 问题与影响 | 建议 |
|---|---|---|---|
| P1 | `Core/Tabs/TabRuntimeManager.cs:120-128` | 关闭时 await 可能失败的创建任务，异常后跳过 registry 关闭，标签卡在 Closing 且无后继激活 | catch 后强制关闭并激活相邻标签 |
| P1 | `TabRuntimeManager.cs:20,60,266,289-292` | 私密清理 Pending 只在内存，崩溃/重启后不重试，profile 可能永久残留 | 持久化待办或启动扫描孤儿目录 |
| P1 | `Persistence/AtomicXmlFile.cs:17-20`、`HistoryStore.cs:98-101` | 读取失败返回 null，随后保存覆盖原文件 | 坏文件隔离/备份，禁止默认值回写 |
| P2 | `TabRuntimeManager.cs:403-407` | `NavigationFailed` 无导航代次，旧失败可能把新成功标成 Failed | 事件携带导航 id |
| P2 | `Core/Tabs/TabRegistry.cs:9-16`、`TabRuntimeManager.cs:15-19` | 无锁，与 `DownloadRegistry.cs:34` 的加锁假设不一致 | 明确 UI 单线程并断言，或统一串行化 |
| P2 | `TabRuntimeManager.cs:66,227-234` | `NavigateAsync` 创建的 host 不更新活动时间，可突破 8 host 上限 | 创建/导航时更新时间 |
| P2 | `Persistence/SettingsStore.cs:30,38-41` | 单个非法字段导致全部设置回默认并覆盖 | 字段级回退 |
| P2 | `Persistence/DownloadStore.cs:73-76,98,103` | 坏 ticks/空字符串可让 `GetAll` 抛异常 | 逐条校验并跳过 |
| P2 | `Core/Downloads/BrowserDownloadPaths.cs:16-32` | 未处理 CON/NUL 等保留名与超长文件名 | 加前缀、截断、拒绝保留名 |
| P3 | `Core/Downloads/DownloadRegistry.cs:104-106` | Core 注释反向引用 Persistence | 更新注释 |

### 4.2 原生 Win32 壳层（6.5/10）

**优点**：Unicode/结构布局基本正确；GDI/字体/DC/COM 释放较完整；subclass 委托均保存在字段中；主题是单一来源；UI 状态基本是 Core 的派生视图。

| 级别 | 位置 | 问题与影响 | 建议 |
|---|---|---|---|
| P0 | `Native/FlatButtonSet.cs:195`、`FlatFieldSet.cs:176/244/303`、`MainWindow.cs:1552/1638`、`InternalPanelWindow.cs:1325` | subclass 过程无 try/catch，异常穿回原生帧直接终止进程；GDI+ `Graphics.FromHdc` 失败即可触发 | 每个过程 try/catch + Trace 后 `DefSubclassProc` |
| P1 | `BrowserShell.cs:864`、`Native/MainWindow.cs:460` | 标题/URL/favicon/下载进度每次 `RenderState` 都重写地址栏，丢失正在输入 | 仅非焦点且文本不同才写；进度只刷面板 |
| P1 | `Native/MainWindow.cs:1826-1833` | `CB_RESETCONTENT` 后用 `_currentUrl` 回填，展开建议即抹掉输入 | 保存并恢复 edit 当前文本 |
| P1 | `Native/MainWindow.cs:263-271`、`DialogKeyboard.cs:24` | 主窗口未注册 `IsDialogMessage`，Tab 无法到达工具栏/地址栏/通知按钮；方形按钮无焦点框 | 注册主窗口或自建焦点环 |
| P1 | `BrowserShell.cs:140-217` vs `1123-1168` | 启动不检查 `_shutdownStarted`，启动中关窗与关闭竞争，可能漏存 session/跳过私密清理 | 取消令牌/状态门；关窗等待初始化结束 |
| P1 | `Native/MainWindow.cs:722`、`BrowserShell.cs:1089-1105` | 20 秒关闭守护 timer ID 3 无人处理，Shutdown 卡死时窗口无法退出 | 处理 ID 3，超时强制完成关闭 |
| P2 | `MainWindow.cs:613-615`、`InternalPanelWindow.cs:444-446` | `CreateControls` 异常被 WndProc 吞掉，窗口半初始化且无提示 | 校验关键句柄，失败销毁并抛错 |
| P2 | `MainWindow.cs:611-691`、`BrowserShell.cs:1133` | 无 `WM_QUERYENDSESSION`，历史仅退出保存，注销/关机丢历史 | 会话结束处理 + 定期保存 |
| P2 | `MainWindow.cs:231-232` | 初始 1120×760 是物理像素，150% 缩放下逻辑尺寸偏小 | 按系统 DPI 初始化 |
| P2 | `NativeTheme.cs:28-71`、`MainWindow.cs:647,1816` | 固定调色板，忽略高对比度/系统色 | 读取 `SPI_GETHIGHCONTRAST` 派生颜色 |
| P2 | `InternalPanelWindow.cs:1049-1052,1325-1380`、`FlatPopup.cs:238` | 行按钮/弹层读屏不可达，隐藏 filter 抢焦点 | 真实按钮/UIA 对象，按模式选焦点 |
| P2 | `TabFaviconCache.cs:88-92,137-139` | 空结果也在 finally 清 `_loading`，每次 RenderState 重发 fetch | 空结果负缓存或退避 |
| P3 | `Native/NativeFolderPicker.cs:39-45,93` | IFileDialog vtable 占位方法错位，当前调用恰好对齐 | 按真实 vtable 顺序声明 |
| P3 | `Localization.cs:36-51,64-67` | 缺键显示 key、zh-Hant 映射简体、切语言后通知仍用旧语言 | 逐键回退 + locale 映射 |

### 4.3 WebView2 适配与安全（7.0/10）

**已验证的安全约束**：`IsWebMessageEnabled=false` 且未注册 handler（`WebView2TabHost.cs:388,525-538`）；权限始终 Deny 且 `Handled=true`（`:660-669`）；Core/App 未外泄 WebView2 COM 对象。

| 级别 | 位置 | 问题与影响 | 建议 |
|---|---|---|---|
| P1 | `WebView2TabHost.cs:363-413`、`Factory:365-368` | 配置异常不关闭已建 controller；死 host 仍缓存；环境初始化故障被永久缓存，首次失败后正常标签不可用 | catch 内 Close、移出 host、清环境缓存、允许重试 |
| P1 | `WebView2TabHost.cs:238-254,593-598`、`BrowserShell.cs:93-99` | 进程崩溃后 controller 未失效，Reload/Back 直接调 COM 无保护，可从通知触发崩溃 | invalidate controller、统一 try/catch、重建 host |
| P1 | `TabSleepPolicy.cs:51-57`、`TabRuntimeManager.cs:214-220` | 睡眠不检查活动下载；Dispose 先解绑下载事件，下载中断但 registry 永远 InProgress | 下载纳入睡眠判定，Dispose 前上报 Interrupted |
| P1 | `WebView2TabHostFactory.cs:42-43,136-140` | 私密 profile 待删路径只在内存，强杀后 `PrivateSessions/*` 无启动清理 | 启动扫描孤儿目录 |
| P1 | `WebView2TabHost.cs:672-688`、`BrowserDownloadPaths.cs:16-53` | 下载目录创建/路径赋值无 try；超长名可致事件抛异常；`File.Exists` 去重有 TOCTOU | 限长/保留名、异常报失败、原子占位 |
| P2 | `WebView2TabHost.cs:525-538,578-583` | 无导航 scheme 白名单，外部协议交给 WebView2 默认确认 | 在 `NavigationStarting`/`LaunchingExternalUriScheme` 统一策略 |
| P2 | `WebView2TabHost.cs:653-657`、`BrowserShell.cs:955-976` | `window.open` 无手势/数量限制，权限通知可被页面刷屏 | 限额、按 tab/kind 去重限频 |
| P2 | `WebView2TabHost.cs:677-688`、`DownloadStore.cs:50-53` | 页面可无确认、无上限写文件；私密下载文件仍写入共用 Downloads，文档“never written”不实 | 确认/配额、私密切除策略、修正文档 |
| P2 | `BrowserShell.cs:81,140-217` | `async void InitializeAsync` 无取消，关闭后仍可能建 runtime/host | 可取消初始化，关闭后拒绝新 host |
| P3 | `WebView2TabHost.cs:312-317,389-391` | `public ExecuteScriptAsync` 是通用脚本入口；DevTools 默认开启；ADR 仍称 trusted frames | 收窄为 internal/测试入口，更新 ADR |

### 4.4 测试、工具链与文档（7.0/10）

**优点**：Unit/Smoke/Visual/Package 分层清楚；Smoke 驱动真实 HWND 而非平行测试面；发布复用开发者同一入口；ADR 覆盖关键决策。

| 级别 | 位置 | 问题与影响 | 建议 |
|---|---|---|---|
| P1 | `release.yml:53-56`、`ci.yml:33` | 发布包从不启动验证，CI 从不跑 Smoke，产物损坏可直接发布 | 自托管 runner 跑 Smoke，或对解压包做独立启动探针 |
| P1 | `Directory.Build.props:5`、各 csproj | 无 `global.json`/lock file，`LangVersion=latest`，构建随 SDK 漂移 | 固定 SDK、启用 `packages.lock.json`、`ContinuousIntegrationBuild` |
| P1 | `tools/package.ps1:48-50,124-177` | 依赖当前目录；启动进程不在 finally 终止；失败清理可能二次抛错 | `Push-Location`，进程放 try/finally，清理加重试 |
| P1 | `tests/Brezza.Core.Tests.csproj:4` vs `App.csproj:5` | netstandard2.0 代码只在 net10.0 测试，net48 运行时行为无回归保护 | 测试项目多目标 `net48;net10.0` |
| P1 | `tools/capture-baseline.ps1:286-291` | 未知 `-Only` 静默跳过所有场景并打印 PASS | 校验 preset，未知值直接报错 |
| P2 | `README.md:29` | 称 `capture-window.ps1` 写 BMP、支持 `-Width/-Height`；实际只接受 `-Scenario` 并输出 PNG | 修正文档（已核实） |
| P2 | `ARCHITECTURE.md:180,192`、`ADR 0010:29`、`ADR 0009:40-41` | schema 4 未列、包内容漏 `Brezza.exe.config`、面板数量与工具版本过期 | 同步文档 |
| P2 | `ROADMAP.md:13` vs `CHANGELOG.md:23-38` | M6 原为 In progress，但变更记录已称完成 | 已统一为 Complete（Smoke 连续 3 次通过） |
| P2 | `tools/capture-window.ps1:348-365` | manifest 只记 exe 时间戳，不记 git commit | 记录 commit SHA |
| P2 | `release.yml:12-13,58-70` | workflow 级写权限；无校验和/attestation；tag 不校验 semver | 最小权限、SHA256、attestation（签名另按 ADR 0013 不采用） |
| P2 | `tools/smoke.ps1:81-129,184` | `Start-Job` 在 try 外，失败泄漏 job 与临时目录；随机端口竞态 | 移入 try/finally，固定端口分配 |
| P2 | `tools/package.ps1:1,10` | 版本无校验；zip 已存在直接 throw，不能重跑 | semver 校验 + 可重复运行 |
| P2 | 测试项目 | 无 coverlet，无覆盖率数据 | 接入覆盖率门槛 |

---

## 五、跨领域结论

1. **失败路径普遍只处理“成功的一半”**：创建失败、关闭失败、清理失败、下载中断都没有形成可观察的终态。
2. **隐私承诺存在“内存态依赖”**：私密 profile 与下载清理依赖进程存活，与“不持久化”承诺不完全一致。
3. **数据安全策略偏乐观**：读失败当空、随后覆盖，是比崩溃更隐蔽的数据丢失路径。
4. **UI 与 Core 的权威边界正确，但派生视图会反向覆盖用户输入**：地址栏是最明显的用户可见缺陷。
5. **交付门禁与文档可信度不匹配**：测试分层设计良好，但最关键的运行时验证被排除在 CI/发布之外。

## 六、修复顺序

**第一批（稳定性与用户可见缺陷）**
- 所有 subclass/WndProc 入口加异常兜底。
- 地址栏同步与建议回填修复。
- WebView2 创建失败时关闭 controller、允许重建、清环境失败缓存。
- 关闭创建失败的标签仍能完成关闭。
- 损坏持久化文件隔离。
- README/VisualOnly 修正。

**第二批（数据与隐私）**
- 私密 profile 待删路径持久化 + 启动清扫（需要单实例/孤儿目录决策）。
- 下载生命周期纳入睡眠判定，中断时上报 Interrupted。
- 下载路径限长、保留名、异常兜底。

**第三批（交付与体验）**
- `global.json`、lock file、net48 测试目标。
- 发布前运行时启动探针；Smoke 进入自托管 CI。
- 主窗口键盘焦点环、UIA 可达性、高对比度与 DPI 修正。
- 文档与 roadmap/changelog 对齐。
