# SpacePeek — 规格 v0.1

> 状态：**已定稿 v1.1（2026-11-12）**。触发键 = 裸空格；↑/↓ 只镜像；多选不翻页（§3.5）。
> 本文是本次决策的落点，所有"已定"项来自逐轮质询的结论；"待验证"项在阶段 0 用最小实验证伪后才动工。
> **定稿 ≠ 开工**：建仓库与 `dotnet new` 需要一句明确的开工资讯。

---

## 0. 一句话

在文件资源管理器里选中一个文件或文件夹，按一下键，它的内容浮现在它旁边；再按一下，干净消失。

---

### 1.3 产品原则（用户 2026-11-12 明确）

**只回答"这是什么、里面有什么"，不提供操作。** 窗口里不放按钮、不放工具栏、不为"操作"预留任何区域；能滚动的就滚动，能一眼看完的就一眼看完。谁想操作，回 Explorer 去。

**文档只看第 1 页。** Word / PowerPoint / Excel 不加载整个文件——那会让预览变慢，而"快"是这个 App 唯一的卖点。

**各类型的呈现规则（用户 2026-11-12 定）**

| 类型 | 窗口比例 | 内容 | 滚动 |
|---|---|---|---|
| 图片 | **跟随图片自身比例**（单边上限 660×720） | 只按显示尺寸解码（≤1200px，低质量插值），小图最多放大 2× | 无（图整体可见） |
| PDF | **跟随页面比例**（多为竖版） | 系统 PDF 引擎只光栅化第 1 页 | 无 |
| Word (.docx) | 竖版 A4 页面 | 直接解析 OOXML 取正文 | 无，超出页面裁掉 |
| PowerPoint / Excel | 4:3 横版 | 第 1 张幻灯片文字 / 首屏单元格 | 无，超出裁掉 |
| Markdown | 竖版 A4 页面 | 前 5000 行纯文本 | 无，超出裁掉 |

**右侧不要滚轮。** 文档类是"看一眼第一页"，不是阅读器；要滚动就去用真正的阅读器。

**不需要管理员权限。** 非提升进程的 `WH_KEYBOARD_LL` 已实测能吞掉 Explorer 前台的空格（§12 实验 2）。提权只会：让钩子看见所有进程的按键（杀软画像更差）、每次启动弹 UAC、与中完整性的 Explorer 跨 UIPI 交互更容易出问题。唯一失效场景是"前台是管理员窗口"——那不是 Explorer，本来也不该预览。

---

## 1. 目标与非目标

### 1.1 目标

1. **手感第一**：热键到 HUD 可见 p95 ≤ 150ms；关闭后不留残影、不夺焦点、不在 Alt-Tab 里出现。
2. **文件夹结构预览**：这是 QuickLook（QL-Win）、PowerToys Peek、macOS Quick Look 都没有做好的部分，也是本项目唯一的存在理由。
3. **零安装摩擦**：单个 exe，双击即用，卸载 = 删文件夹。
4. **可审计**：零遥测、除"检查更新"外不联网、全局钩子代码短且可读。

### 1.2 非目标（v1 明确不做）

| 不做 | 理由 |
|---|---|
| 第三方文件管理器（Files / Directory Opus / Total Commander） | 每个都要单独适配，成本 ×4 |
| Win10 兼容 | 只用 Win11 API（DWM 圆角、WebView2 预装、单文件体积不敏感） |
| 安装器 / 自动更新 | 便携 exe + 手动下载，是最省心的分发方式 |
| 压缩包内部浏览、HEIC/RAW、SVG、3D | 阶段 6 之后再议 |
| 自己渲染 docx/xlsx/pptx | 用系统 Preview Handler，装不了就降级 |
| 云盘/网络位置优化 | 走普通文件路径，慢就是慢 |
| 多选翻页（↑/↓ 在选中集合内切换） | 用户 2026-11-12 砍掉：**只做单对象预览**；多选时只显示一张"N 项已选"汇总卡 |
| 预览时的编辑、搜索、缩放之外的交互 | 只读 |

---

## 2. 与既有方案的差异

| | macOS Quick Look | PowerToys Peek | QL-Win/QuickLook | **SpacePeek** |
|---|---|---|---|---|
| 触发 | 空格 | 空格（0.95+） | 空格 | Ctrl+Space 默认，空格可选 |
| 文件夹预览 | 图标网格 + 计数 | 元数据摘要 | 有限 | **两层树 + 大小汇总** |
| 焦点 | 不抢 | 不抢 | 不抢 | 不抢 |
| 分发 | 系统内置 | 随 PowerToys 安装 | Store/MSI，GPL-3.0 | 单文件 exe，MIT |
| 平台 | macOS | Win10+ | Win10+ | Win11 only |

> 竞品事实：`QL-Win/QuickLook` 25.7k star、237 个未关 issue、GPL-3.0、2026-11 仍在提交。**代码一行都不能抄**（许可证不兼容），只能借鉴设计。

---

## 3. 交互规格

### 3.1 触发键

| 模式 | 默认 | 实现 | 备注 |
|---|---|---|---|
| **裸空格（默认）** | 开启 | `WH_KEYBOARD_LL` + 资格门 | 用户 2026-11-12 拍板：要的就是 Mac 手感。代价是 Explorer 里"空格切换勾选"被覆盖 |
| 热键备用 | `Ctrl+Shift+Space` | `RegisterHotKey` | 一直注册，失败时托盘提示 |
| 热键次级 | `Ctrl+Space` | `RegisterHotKey` | 中文输入法下会被间歇吞掉，只作补充 |
| `Alt+Space` | — | — | 注册直接失败（系统占用），不提供 |

**裸空格的资格门（钩子里只读缓存，绝不碰 COM/磁盘）**

1. 前台窗口（向上取根窗口后）属于 `explorer.exe`，且类名不在排除表内（桌面、任务栏、溢出窗、CoreWindow）；
2. `GetGUIThreadInfo` 显示无 caret，且焦点控件类名不含 `Edit`；
3. 合成按键（`LLKHF_INJECTED`）默认忽略；设 `SPACEPEEK_ALLOW_INJECTED=1` 供自动化测试；
4. 只处理 `WM_KEYDOWN` / `WM_SYSKEYDOWN`——**处理 keyup 会把刚打开的 HUD 立刻关掉**（已踩过）；
5. 资格由 150ms 轮询维护在 `volatile bool`；**不把"有选中项"放进资格门**，否则切窗口后要等一轮轮询才响应，手感发飘。选中项在 UI 线程再确认，没有就静默。

> 回调超过 `LowLevelHooksTimeout`（默认 300ms）系统会静默摘掉钩子——这是 Peek 那类"突然失灵"bug 的常见来源。

**裸空格开启后的资格门（必须全部为真才吞键）**

1. 前台窗口类名 ∈ `{CabinetWClass, ExplorerWClass, WorkerW, Progman}`，且属于 `explorer.exe`；
2. 存在 ≥1 个选中项；
3. `GetGUIThreadInfo` 显示无 caret（`hwndCaret == 0` 或未置 `GUI_CARETBLINKING`）——防"重命名时被预览打断"；
4. 焦点控件类名不含 `Edit` / `RichEdit` / `TextBox`；
5. 前台窗口变化后 40ms 内不响应（防抖，避开切换窗口的瞬间）；
6. 前台窗口不是 HUD 自己。

> 以上 6 条在**后台线程**预先算好，钩子回调里只读一个 `volatile bool`，然后 `return 1` 或 `CallNextHookEx`。回调内绝不做 COM 调用、文件 IO、锁等待 —— 超过 `LowLevelHooksTimeout`（默认 300ms）系统会**静默摘掉钩子**，这是 Peek 那类"突然失灵"bug 的常见来源。

### 3.2 HUD 打开时的按键

| 键 | 行为 |
|---|---|
| 触发键再按 | 关闭 |
| `Esc` | 关闭 |
| `Enter` | 用默认程序打开选中项，并关闭 |
| `Ctrl+C` | 复制文件路径 |
| 滚轮 | 滚动预览内容（滚轮事件按光标位置投递，HUD 无焦点也能收到） |
| `↑/↓` | 交给 Explorer 镜像：选中项变了 HUD 跟着换；多选时不翻页 |

### 3.3 打开/关闭语义

- 打开：HUD 显示，**焦点仍留在 Explorer**（`WS_EX_NOACTIVATE`）。
- 关闭：`Hide()`，不 `Close()`，复用 HWND；焦点无需归还（从未离开）。
- 再次打开时复用同一 HWND 与同一预览器实例，避免"首次激活慢"（Peek #40598 的已知问题）。

### 3.4 状态机

```
Idle ──触发──▶ Opening ──就绪──▶ Ready ──触发/Esc──▶ Closing ──▶ Idle
                  │                 │
                  └──超时 1.5s──▶ Timeout(显示"仍在加载"+打开按钮)
```

### 3.5 ↑/↓ 与多选（用户 2026-11-12 改为：只做单对象预览）

HUD 不抢焦点，Explorer 始终持有键盘。

- **单选**：↑/↓ 交给 Explorer，选中项移动，HUD 跟着换内容（镜像，零劫持）。这是唯一被支持的路径。
- **多选（≥2）**：不翻页、不劫持，只显示一张"N 项已选"汇总卡（列出前三个名字）。HUD 打开期间多选集合变化不重新判定。
- 原先设计的"多选时钩住 ↑/↓ 在预览内翻页"整块砍掉，理由：用户只需要单对象预览，多一套按键路由=多一类 bug。

---

## 4. 架构

### 4.1 进程模型

单进程常驻（托盘），**单实例**（`Mutex` + 激活已有实例）。

- 主线程：WPF UI + 消息循环。
- 钩子线程：仅裸空格模式，独立线程 + 自己的消息循环，避免拖慢 UI。
- 后台线程池：文件夹扫描、大小统计、缩略图解码。

不做 hook helper 子进程：单进程便于调试与分发，钩子只在裸空格模式下启用，风险可控。

### 4.2 模块

| 模块 | 职责 | 关键依赖 |
|---|---|---|
| `SpacePeek.App` | WPF 宿主、托盘、设置窗、HUD 生命周期 | — |
| `SpacePeek.Interop` | Win32 封装：窗口样式、DPI、钩子、热键、DWM | CsWin32 |
| `SpacePeek.Shell` | 选中项获取（SHDocVw / IShellBrowser / IFolderView） | COM interop |
| `SpacePeek.Preview` | 预览器工厂与管线 | — |
| `SpacePeek.FolderTree` | 文件夹扫描、树模型、统计 | — |

### 4.3 目录结构

```
SpacePeek/
  src/
    SpacePeek.App/          # WPF, net8.0-windows
    SpacePeek.Interop/
    SpacePeek.Shell/
    SpacePeek.Preview/
    SpacePeek.FolderTree/
    SpacePeek.Tests/
  docs/
    SPEC.md
  .github/workflows/build.yml
  LICENSE  (MIT)
  README.md
```

---

## 5. 子系统

### 5.1 选中项获取

```
ShellWindows (SHDocVw) 枚举
  └─ 找到 HWND == 前台窗口 的那一个
       └─ IServiceProvider.QueryService(SID_STopLevelBrowser)
            └─ IShellBrowser.QueryActiveShellView()
                 └─ IFolderView.Items(SVGIO_SELECTION, IID_IShellItemArray)
                      └─ IShellItem.GetDisplayName(SIGDN_FILESYSPATH)
```

- 桌面：`IShellWindows.FindWindowSW(SWC_DESKTOP, SWFO_NEEDDISPATCH)`。
- Win11 多标签：需识别**活动标签页**，否则会拿到后台标签的视图（⚠️ 待验证：Peek 用 `GetActiveTab()` 比对 `shellBrowser.GetWindow()`）。
- 拿不到（搜索页、首页、库、回收站、压缩包内）→ **静默**，写一条本地日志，不弹窗。

### 5.2 HUD 窗口

- `WindowStyle=None`、`AllowsTransparency=false`（透明窗口会关掉硬件加速）、`ShowActivated=false`、`ShowInTaskbar=false`。
- `SourceInitialized` 时追加 `WS_EX_NOACTIVATE | WS_EX_TOOLWINDOW`；`Topmost=true`。
- Win11 圆角：`DwmSetWindowAttribute(DWMWA_WINDOW_CORNER_PREFERENCE)`；阴影交给 DWM。
- 尺寸：内容自适应，上限为工作区的 75%，最小 480×320。

**定位算法（A 方案：贴着选中项）**

1. 取选中项的屏幕矩形 `rect`（⚠️ 待验证：`IFolderView2::GetItemPosition` + 视图原点换算，或 UIA `BoundingRectangle`，见 §12）。
2. 首选：HUD 左边 = `rect.right + 8`，顶边对齐 `rect.top`（并夹在工作区内）。
3. 右侧放不下：翻到 `rect.left - 宽度 - 8`。
4. 仍放不下：居中于 Explorer 窗口。
5. 全部计算用显示器 DPI（`GetDpiForWindow`），HUD 按 `PerMonitorV2` 感知。
6. **不记忆位置**。

### 5.3 预览管线（阶段 2 起）

工厂按扩展名分派，顺序与 Peek 一致但精简：

```
Image → Video → Audio → PDF/HTML(SVG) → Text/Code → Markdown → ShellPreviewHandler → Folder → InfoCard
```

- **PDF / SVG / HTML**：WebView2 直接 `Navigate(file:///…)`，用 Edge 内置查看器。不引入 PDFium。
- **文本/代码**：WebView2 + Monaco（vendored，MIT，需自带走 LICENSE）；不做语法高亮的话可退化为纯文本。
- **Markdown**：Markdig → 临时 HTML → WebView2（临时文件放 `%LOCALAPPDATA%\SpacePeek\temp`，退出清理）。
- **Office/字体/RTF**：`IPreviewHandler` 宿主。
  - CLSID 查 `HKCR\.ext\shellex\{8895b1c6-…}`，回退到 ProgID。
  - `CoGetClassObject(CLSCTX_LOCAL_SERVER)` → 缓存 `IClassFactory` + `LockServer(true)`，冷启动快 200–400ms。
  - `IInitializeWithStream` → `IInitializeWithItem` → `IInitializeWithFile` 依次尝试。
  - 子 HWND `WS_CHILD`，`SetWindow(hwnd, rect)` + `DoPreview()`，`SetRect` 跟随 DPI/尺寸变化。
  - 卸载：`Unload()` + `FinalReleaseComObject` 各自 try/catch。
- **`.xlsm / .xlsb`**：**不调 Office handler**，只给 InfoCard。理由：Peek #29580，预览 Excel 会释放用户的 Personal Macro Workbook。

### 5.4 文件夹树（阶段 1 的核心）

- 根节点 = 选中文件夹；默认展开 1 层（直接子项）。
- 子文件夹**按需**展开，不递归预扫。
- 顶部汇总行：`12 个文件夹 · 348 个文件 · 2.4 GB`，异步累计，未完成显示"统计中…"。
- 扫描：`FileSystemEnumerable`，跳过 reparse point，`IgnoreInaccessible = true`，单目录上限 500 项，软超时 1.5s 后转"部分结果"状态。
- 渲染：**扁平化 + 虚拟化列表**（自己维护可见行 + 缩进），不用 `TreeView`（WPF TreeView 的虚拟化是半残的）。
- 排序：先文件夹后文件，各自按名称（自然序）。
- 图标：`SHGetFileInfo` 小图标，异步取，不带缩略图（阶段 2 再说）。

---

## 6. 性能预算

| 动作 | p50 | p95 | 超时行为 |
|---|---|---|---|
| 热键 → HUD 可见 | 80ms | 150ms | — |
| 文件夹根目录首屏 | 120ms | 300ms | 1.5s 转"部分结果" |
| 展开子文件夹 | 60ms | 200ms | 同上 |
| 切换文件（阶段 2） | 100ms | 250ms | — |
| 视频首帧 | 250ms | 400ms | 1.5s 显示"仍在加载" |
| PDF 首帧 | 300ms | 600ms | 同上 |

**测量方式**：内置 `Stopwatch` 计时点，`--bench` 参数跑脚本化序列并把结果写到 `%LOCALAPPDATA%\SpacePeek\bench.log`。不联网上报。

---

## 7. 降级与失败矩阵

| 情况 | 行为 |
|---|---|
| 位置不支持（搜索/首页/库/回收站/压缩包内） | 静默无反应 + 本地日志 |
| 拿不到选中项矩形 | 居中于 Explorer 窗口 |
| 没有可用预览组件 | InfoCard（图标/类型/大小/时间）+「用默认程序打开」 |
| 文件夹项数 > 500 | 显示前 500 + "仅显示前 500 项" |
| 文件夹扫描超 1.5s | 显示已有结果 + "统计中" 角标，后台继续 |
| 网络路径 / 无权限 | InfoCard + "无法访问" |
| 文件被占用 / 已删除 | InfoCard + "文件不可读" |
| WebView2 运行时缺失 | 一眼可读的类型退回 WPF 原生渲染；其余 InfoCard + 提示安装 |
| 钩子安装失败 | 记录日志，回退热键模式 |
| 热键注册失败 | 自动换 `Ctrl+Shift+Space`，托盘提示 |
| `explorer.exe` 重启 | `EVENT_SYSTEM_FOREGROUND` 失效后重建钩子与 COM 连接（Pez 类 bug 高发区，需专门测） |

---

## 8. 隐私与数据足迹

- **零遥测**，无任何统计上报。
- 联网仅一处：托盘"检查更新"→ `GET api.github.com/repos/<owner>/SpacePeek/releases/latest`；可在设置里关。
- 日志：`%LOCALAPPDATA%\SpacePeek\logs\`，滚动 5MB × 3。
- 缓存：`%LOCALAPPDATA%\SpacePeek\cache\`（缩略图/临时 HTML），设置里一键清除，**不缓存文件内容**。
- 配置：`%LOCALAPPDATA%\SpacePeek\config.json`，纯文本可手改。
- 注册表：**只有**用户勾选"开机自启"时写 `HKCU\...\Run`，取消勾选即删。

---

## 9. 分发与发布

- `dotnet publish -c Release -r win-x64 --self-contained -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true`
- 产物：`SpacePeek.exe`（约 80–150MB），压缩后 Release 附件。
- v1 **不签名**：README 首屏放"未签名说明"、SHA256、VirusTotal 链接。
- 并行申请 SignPath Foundation（开源免费）或 Azure Trusted Signing；拿到后重发 v1.1。
- CI：`build.yml` 跑 `dotnet build` + `dotnet test` + 生成 portable artifact；tag 触发 Release。

---

## 10. 阶段划分与验收

### 阶段 0 — 可行性实验（1–2 天，可丢弃代码）

- [ ] `WH_KEYBOARD_LL` 在 .NET 8 进程里装得上，能吞掉前台 Explorer 的空格，且不影响重命名
- [ ] `WS_EX_NOACTIVATE` WPF 窗口显示后，Explorer 焦点与选中高亮不丢
- [ ] ShellWindows → 活动标签 → 选中项，在 3 种视图下都拿得到
- [ ] 选中项矩形拿得到（UIA 或 IFolderView2）

**验收**：四个实验全绿，或至少"矩形拿不到"被证实（则 §5.2 退化为居中）。

### 阶段 1 — MVP（本项目的立项证明）

- [ ] 托盘常驻，`Ctrl+Space` 弹出空 HUD，再按关闭，焦点不丢
- [ ] 选中文件夹时，HUD 显示两层树 + 汇总行 + 部分结果状态
- [ ] 选中文件时，HUD 显示 InfoCard（图标/名/类型/大小/时间）
- [ ] 500 项、10 万项目录都不卡 UI
- [ ] 关闭后无残影、无 Alt-Tab 条目、任务栏无图标

**验收**：上面 5 条 + §6 的三项时间预算达标。**这一阶段不过，项目就该停。**

### 阶段 2 — 媒体

图片（含 GIF/WebP）、视频、音频；方向键切换的防黑帧策略（保留旧帧 + 遮罩淡入）。

### 阶段 3 — 文档

PDF、纯文本/代码、Markdown；WebView2 宿主与生命周期。

### 阶段 4 — Office 与设置

IPreviewHandler 宿主；设置窗、双语、排除目录、开机自启。

### 阶段 5 — 发布

README（含 GIF 演示）、CI、Release、签名申请。

---

## 11. 风险登记

| 风险 | 影响 | 缓解 |
|---|---|---|
| 未签名 exe + 全局钩子被 AV 判为键盘记录器 | 高：第一条 issue 就是"被杀软删了" | 默认热键模式；钩子代码集中且可读；README 主动说明；VT 链接 |
| Win11 多标签页拿错选中项 | 高：核心功能错乱 | **已实测复现**（§12）：同 HWND 两个浏览器对象。规避 = 单浏览器快路径 + UIA 交叉匹配，匹配失败即静默 |
| 选中项矩形拿不到 | 中：退化为居中 | 阶段 0 验证；UIA 兜底 |
| 文件夹树性能不达标 | 高：项目失去意义 | 虚拟化扁平列表 + 按需展开 + 硬上限；阶段 1 就是它的考试 |
| 文件夹树行布局重叠（展开后） | 高：界面不可用 | **已解决**：根因是 `ListView` 显式 `Height` + `SizeToContent` + `CanContentScroll` 三者冲突。改为 `MaxHeight=420` + 不设显式高度 + 标准虚拟化后，离屏渲染验证正常（§12 实验 9） |
| WPF 无焦点窗口的边角问题（IME/无障碍/滚轮） | 中 | 阶段 0 实验；接受 IME 不可用 |
| 与 QuickLook（25k star）正面竞争 | 中 | 不打"又一个 Quick Look"，只打文件夹树这一刀 |
| 单文件 exe 体积 150MB | 低 | 文档说明；后续可出 framework-dependent 变体 |

---

## 12. 阶段 0 实验结果（2026-11-12，本机 Win11 build 26200）

目标：在写正式代码前，用可丢弃的 `_spike` 工程证伪四个核心假设。

| # | 假设 | 结果 | 数据 |
|---|---|---|---|
| 1 | `WS_EX_NOACTIVATE` 的 WPF 窗口显示后不抢焦点 | **PASS** | `ShowActivated=false` + `SourceInitialized` 里加 `NOACTIVATE\|TOOLWINDOW`；前台 HWND 前后一致，且不进任务栏 |
| 2 | `WH_KEYBOARD_LL` 在 .NET 8 里能吞键、能透传 | **PASS** | 吞键时目标窗口 `KeyDown=0`；关闭吞键后 `KeyDown=1`；50 次合成按键回调 102 次，无超时 |
| 3 | 能拿到选中项（含多选） | **PASS** | `Shell.Application.Document.SelectedItems()` 正确返回 2 项；**warm 2.1–2.6ms** |
| 4 | 能拿到选中项的屏幕矩形 | **PASS** | UIA：定位列表 69ms（一次性），之后 `Children` 扫描 **0.2–2.3ms**，选中项矩形 **2.4ms**；Details 视图下矩形为整行 `747×31` |
| 5 | Win11 多标签页能确定"活动标签" | **FAIL（未解决）** | 见下 |
| 6 | `Ctrl+Space` 能稳定当触发键 | **FAIL** | 注册成功 ≠ 能收到：同一进程内，Notepad 前台时 `Ctrl+Space` 触发，Explorer 前台时**不触发**；`Ctrl+Shift+Space` / `Ctrl+Alt+Space` 稳定触发；`Alt+Space` 注册失败 |
| 7 | 裸空格触发全链路 | **PASS** | 钩子吞掉空格 → 选中项 → HUD；再按一次关闭；暖路径 **4–27ms** |
| 8 | HUD 定位用物理像素可行 | **FAIL（已改）** | `SetWindowPos` 用物理像素、WPF 命中测试用 DIP，在 125% 缩放下点击坐标整体偏移（实测 pos 比预期大 ~2.9×），点击落在列表外。改为用 WPF `Left/Top`（DIP）定位后，`e.GetPosition` 与容器边界一致 |
| 9 | 能离屏验证 HUD 布局 | **PASS** | 新增 `SpacePeek.exe --render <路径> <out.png>`：窗口建在屏幕外（-6000,-6000）渲染成 PNG，**完全不打扰桌面**。用它验证了根视图与展开后的行布局均正常 |
| 10 | Office 用系统缩略图取第 1 页 | **FAIL** | `IShellItemImageFactory` 对本机 docx 返回 **395×512 全白**页面（无内容）。改走自研 OOXML 解析 |
| 11 | 自研 OOXML 第 1 页快照 | **PASS** | 直接解 zip+XML：docx **71ms**、xlsx **83ms**、pptx **69ms**，不启动 Office、不依赖预览组件。xlsx 需同时处理 `inlineStr` 与共享字符串表 |

**离屏渲染是阶段 1 之后的主要验收手段**：桌面正被占用时，自动点击抢焦点会互相干扰；`--render` 让"看一眼对不对"变成一条命令。

**关于时延的重要更正**：`Shell.Application` 首次调用约 200ms（COM/IDispatch 冷启动），**warm 之后是 2ms**。所以 150ms 预算里必须给"启动预热"留一步——托盘常驻时先做一次哑调用。
同理 UIA 首次 `FromHandle` + 定位列表要 70–160ms，也应预热并缓存列表元素。

**标签页的实测结论（新增风险，写进 §11）**

`Ctrl+T` 之后，`ShellWindows` 会为同一个 `CabinetWClass` HWND 返回**两个浏览器对象**：

```
url='file:///C:/.../spacepeek-spike'   selected=2   ← 原标签
url=''                                  selected=0   ← 新开的主文件夹标签
```

按 HWND 匹配会随机拿到错的那个。我尝试用 `IServiceProvider → IShellBrowser → QueryActiveShellView` 判定可见视图，但该调用在两个条目上都返回 `S_FALSE`（0x00000001），**接口层未能区分**。同时 UIA 的 `TabItem` 探测是假阳性（导航窗格里的"收藏夹/主文件夹"被当成 tab）。

**拟定规避（待验证）**：默认走"单浏览器快路径"（绝大多数用户一次只有一个标签）；当同 HWND 下出现 ≥2 个浏览器时，用 UIA 当前可见列表的选中项名称与各浏览器的 `SelectedItems` 名称做交叉匹配，匹配不上就当作"不支持的位置"静默。这条要在阶段 1 落实并压测。

---

## 附：本次质询中被推翻/修正的判断

| 原判断 | 修正为 | 触发事实 |
|---|---|---|
| 默认裸空格 | 默认 `Ctrl+Space`，裸空格为实验项 | 公开分发 + 未签名 + 钩子 ≈ 杀软画像；Peek 的裸空格 bug 是成建制的 |
| "文件夹预览"直接照 Quick Look | 做成两层树 | Peek 只给元数据（#31686 有人要但未做），这是唯一空档 |
| Excel 走 Office handler | `.xlsm/.xlsb` 只给 InfoCard | #29580：预览 Excel 会释放 Personal Macro Workbook |
| 自研 PDF 渲染 | WebView2 内置查看器 | Peek 也没有 PDFium |
