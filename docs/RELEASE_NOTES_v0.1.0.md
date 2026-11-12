# v0.1.0 — 首个可用版本

Windows 11 上的空格键快速预览：在资源管理器里选中文件或文件夹，按一下**空格**，内容浮现在旁边；再按一下关闭。

## 支持

- **图片**：jpg / png / bmp / gif / tif / ico / webp，按 EXIF 方向自动摆正
- **视频**：mp4 / m4v / mov / mkv / avi / wmv / webm / mpg / mpeg / ts / flv —— 取第一帧
- **PDF**：第 1 页，按页面比例竖版显示，2.5× 分辨率
- **Word**：doc / docx，整页显示（不启动 Word 进程）
- **Excel**：xlsx / xlsm 自研渲染（字体、填充、合并、日期格式照原文件）；xls 走 Excel 导出
- **PowerPoint**：ppt / pptx 第 1 张幻灯片
- **Markdown**：渲染后显示（标题、列表、引用、代码块、表格）
- **HTML**：WebView2 真渲染，失败回退源码
- **EPUB**：解析容器 → OPF → spine，按书页展示
- **压缩包**：zip / jar / apk / nupkg / vsix / epub 条目列表，GBK 文件名自动纠正
- **文本 / 代码**：140+ 扩展名，含 README / LICENSE / Dockerfile 等无扩展名文件
- **文件夹**：两层结构树 + 文件数与大小统计

## 使用

下载 `SpacePeek.exe`，双击运行（无需安装、无需管理员权限）。之后在资源管理器里选中文件按 **空格**。

托盘图标右键：设置、开机自动启动、清空缓存、关于、退出。

## 说明

- **未签名**：首次运行如遇 SmartScreen，点「更多信息 → 仍要运行」
- **零遥测**：不联网、不写注册表（开机自启除外）
- **仅在系统资源管理器生效**：第三方文件管理器不响应
- 加密 PDF 无法预览（会提示需要密码）
- `.xls` / `.ppt` 需要本机安装 Office；Office 文档首次预览会拉起对应的预览组件，结果有缓存

## 系统要求

- Windows 11（x64）
- 自包含 .NET 8，无需预装运行时
- HTML 预览依赖系统自带的 WebView2（Win11 默认已安装）
