# 发布到 GitHub（v0.1.0）

这份文档是给"把 SpacePeek 传到 GitHub 并发 v0.1.0"用的。本地仓库已经初始化好、文件已提交、tag 也打好了，你只需要做**建远程仓库 + 推送 + 发 Release** 三步。

---

## 0. 前置检查

```powershell
git --version          # 需要 2.20+
dotnet --list-sdks     # 需要 8.0.x（只在本机构建时才要）
```

GitHub 账号准备两件事：

1. 一个 **Personal Access Token**（Settings → Developer settings → Personal access tokens → Fine-grained tokens），勾选 `repo` 权限；
   或者配好 SSH key（`ssh -T git@github.com` 能回 `Hi <name>!`）。
2. 想好仓库名，建议 `SpacePeek`（中文名"快速预览"会出现在 README 和窗口标题里）。

---

## 1. 本地已完成的部分（不用你做）

```powershell
cd D:\AiCode\DSH\Preview
git log --oneline -1     # 已经有一次初始提交
git tag -l               # 会看到 v0.1.0
git ls-files | Measure-Object -Line   # 约 50 个文件
```

被 `.gitignore` 排除、**不会**上传的东西：`bin/ obj/ dist/ _spike/ _legacy-samples/`。
`dist/SpacePeek.exe` 通过 Release 附件分发，不进仓库（Gitee/GitHub 都不适合放 75MB 的二进制）。

---

## 2. 在 GitHub 上建仓库

1. 打开 <https://github.com/new>
2. **Repository name**：`SpacePeek`
3. **Description**：`Windows 空格键快速预览 · 按空格预览文件与文件夹结构`
4. 可见性选 **Public**
5. **不要**勾选 "Add a README file"、**.gitignore**、**license**（本地已经有，勾了会冲突）
6. 点 **Create repository**

建好后页面会显示推送命令，先别急着复制，继续下一步。

---

## 3. 关联远程并推送

把 `<你的用户名>` 换成你的 GitHub 用户名。

### HTTPS（简单，推荐新手）

```powershell
cd D:\AiCode\DSH\Preview
git remote add origin https://github.com/<你的用户名>/SpacePeek.git
git branch -M main
git push -u origin main
```

首次推送会弹浏览器要求登录 GitHub（Git Credential Manager 会记住），或者提示输入用户名 + **Token 当密码**。

### SSH（已配好 key 的话）

```powershell
git remote add origin git@github.com:<你的用户名>/SpacePeek.git
git branch -M main
git push -u origin main
```

---

## 4. 推送 tag（版本号）

```powershell
git push origin v0.1.0
```

到这里仓库里就有 `main` 分支和 `v0.1.0` tag 了。

---

## 5. 发 Release（把 exe 挂上去）

### 方式 A：网页操作

1. 仓库页右侧 **Releases** → **Create a new release**
2. **Choose a tag** 选 `v0.1.0`
3. **Release title**：`v0.1.0 — 首个可用版本`
4. 描述可以贴：

```markdown
首个版本，Windows 11 上的空格键快速预览。

## 支持
- 图片（含 EXIF 方向校正）、视频首帧、PDF 第 1 页
- Word / Excel / PowerPoint 整页预览
- Markdown 渲染、HTML（WebView2）、EPUB
- 压缩包条目（GBK 文件名不乱码）
- 100+ 文本与代码格式、文件夹两层结构树

## 使用
下载 `SpacePeek.exe`，双击运行，然后在资源管理器里选中文件按 **空格**。

## 说明
- 未签名，首次运行如遇 SmartScreen 请点「更多信息 → 仍要运行」
- 免安装、无管理员权限、无遥测
- SHA256：<把你文件的哈希贴这里>
```

5. 把 `dist\SpacePeek.exe` 拖到 **Attach binaries** 区域
6. 勾不勾 "Set as the latest release" 都行 → **Publish release**

生成 SHA256（放在 Release 描述里，用户可以校验）：

```powershell
Get-FileHash .\dist\SpacePeek.exe -Algorithm SHA256 | Format-List
```

### 方式 B：命令行（装了 GitHub CLI 更快）

```powershell
winget install GitHub.cli
gh auth login

cd D:\AiCode\DSH\Preview
gh release create v0.1.0 .\dist\SpacePeek.exe `
  --title "v0.1.0 — 首个可用版本" `
  --notes-file .\docs\RELEASE_NOTES_v0.1.0.md
```

（`--notes-file` 里的内容就是上面那段 Markdown，你可以先建这个文件。）

---

## 6. 验证 CI

仓库里已经带了 `.github/workflows/build.yml`：

- 每次 push 到 `main`：自动 `dotnet build` + 生成单文件 exe，作为 artifact 存 14 天；
- 推送 `v*` tag：自动构建并**直接挂到 Release 上**。

推送后到仓库 **Actions** 页看 `build` 是否变绿。第一次跑约 2–3 分钟。

---

## 7. 以后每次改版的流程

```powershell
cd D:\AiCode\DSH\Preview

# 改代码后
git add .
git commit -m "fix: xxx"
git push

# 发新版本
git tag v0.1.1
git push origin v0.1.1
# 然后按第 5 步发 Release（CI 会自动把 exe 挂上去）
```

---

## 8. 常见问题

**Q：推送时提示 `remote origin already exists`**
```powershell
git remote set-url origin https://github.com/<你的用户名>/SpacePeek.git
```

**Q：exe 太大，能不能放进仓库？**
不要。GitHub 单文件上限 100MB，且每次提交都会永久留在历史里。用 Release 附件，或开 Git LFS。

**Q：CI 报 `NETSDK1100 / 找不到 Windows SDK`**
workflow 里已经指定 `runs-on: windows-latest` 和 `TargetFramework net8.0-windows10.0.19041.0`，正常不会出现；若出现，检查 `SpacePeek.App.csproj` 里的 TFM 没被改回 `net8.0-windows`。

**Q：CI 构建失败提示找不到图标**
`ApplicationIcon` 指向 `..\..\assets\SpacePeek.ico`，这个文件**必须提交**；
如果换了 `assets/Appicon.png`，要重新生成 ico（`tools` 里没有脚本，见下），否则图标还是旧的。

**Q：想换成别的开源协议**
改 `LICENSE` 文件，并在 README 末尾的许可段落同步改。

**Q：怎么让仓库更容易被搜到**
仓库页右上 **About** → 加 topics：`windows` `quicklook` `file-preview` `explorer` `wpf` `dotnet`。

---

## 9. 版本号约定

| 位 | 什么时候加 |
|---|---|
| `0.1.x` | 修 bug、改样式 |
| `0.x.0` | 新增一类格式或功能 |
| `1.0.0` | 功能稳定、有签名、有自动更新 |

`v0.1.0` 是首个版本：能在 Win11 上按空格预览主要格式，还没有代码签名。
