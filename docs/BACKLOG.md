# Backlog

任务尚未进入 `docs/ROADMAP.md`，优先级是建议值，不代表已承诺的里程碑。已修复项见
[ASSESSMENT](ASSESSMENT.md)；明确不处理的项见文末“已接受”。

## P1 — 下一次发布前应决策

无。Authenticode 签名已明确不采用（ADR 0013），发布包保持 unsigned，完整性由 SHA256 sidecar 和 GitHub build provenance 提供。

## P2 — 质量与安全加固

无。

## P3 — 清理

无。

## 已完成

| ID | 结果 |
|---|---|
| W1 | WebView2 进程失败时失效 controller、重建导航、标记 `Failed`；新增 Core 测试 |
| W2 | 采用方案 B：文档明确私密下载只排除记录，文件仍留在 Downloads |
| W3 | 打包生成 SHA256 sidecar；release 校验 tag semver、上传校验和并生成 GitHub attestation；Authenticode 明确不采用（ADR 0013） |
| W4 | 外部协议策略：仅用户手势触发的 `mailto`/`tel`/`sms` 放行，其余取消并提示；新增策略与传播测试 |
| W5 | 弹窗要求用户手势，每 tab 2 秒冷却、全局 5 次/分钟；权限通知按 source+kind 30 秒去重；新增限流测试 |
| W6 | 全浏览器同时最多 3 个下载，已知总大小超过 8 GiB 时在开始前拒绝；不创建部分文件并显示本地化原因；新增 Core 配额测试 |
| W9 | setup 失败会停止并移除 origin job；origin job 自行绑定端口并在 `HttpListener.Start` 后写就绪文件；Smoke 通过 |
| W11 | capture manifest 记录 git commit 和 dirty 状态；capture 通过反射读取 `MainWindow` 控件 ID；等 paint/menu/window 条件替代布局 sleep；修复 PowerShell 5.1 C# 兼容性，Visual 通过 |
| W13 | 默认下载目录改走 `SHGetKnownFolderPath(FOLDERID_Downloads)`；session 级命名 mutex，第二实例恢复并激活已有窗口后退出 |
| W7 | `WM_CREATE` 控件创建失败时返回 -1，窗口/面板不会以半初始化状态存在 |
| W8 | favicon 对“无图标”按 tab + URI 负缓存；无 controller 时不误缓存 |
| W10 | `package.ps1` 校验 semver、允许覆盖旧 zip、校验 `Brezza.exe.config` |
| W15 | 新增完整 `zh-Hant`（繁體中文）语言包和设置项；`zh-Hant`/`zh-TW`/`zh-HK`/`zh-MO` 映射到 `zh-Hant`，持久化统一为 script tag；Unit 校验三语键与占位符，Visual 验证繁体工具栏 |
| W12 | Unit stage 收集 Cobertura 覆盖率（ADR 0012），并强制 85% 行 / 70% 分支下限；语言包键与占位符校验也在此阶段运行 |
| W14 | `IFileDialog` 按真实 vtable 顺序声明 `Advise`/`Unadvise`/`Close`；Smoke 通过 |
| W16 | `DownloadRegistry.Start/Restore` 按 target path/ID 去重；新增测试 |
| W17 | 滚轮先请求 Core 激活，等状态回写后再移动原生选中 |
| W18 | 面板窗口类改为静态类过程路由，重开/多实例不会再指向旧委托 |
| W19 | `ExecuteScriptAsync` 仅 Debug 编译；Release 关闭 DevTools |
| W20 | Changelog 统一到 `## Unreleased` + `### Added` / `### Fixed` |

## 已接受，不立项

| 项 | 原因 |
|---|---|
| B7 发布包自动启动验证 | 需要自托管交互式 runner；保留人工发布检查 |
| UIA 行级可达性 | 需要实现 UIA Provider 或重构自绘行；当前明确不处理 |
