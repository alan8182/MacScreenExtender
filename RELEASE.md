# 发版流程（RELEASE）

> 从代码改动到用户可下载的完整发版手册。当前发布渠道：GitHub Releases（https://github.com/alan8182/MacScreenExtender/releases）。

---

## 版本号规范

- 使用语义化版本：`v主版本.次版本.修订号`（如 `v1.0.1`）——协议/破坏性变更升主版本，新功能升次版本，修复升修订号；
- Git 标签名 = 版本号，如 `v1.2.0`，三端版本号同步更新到同一版本：
  - **Mac 发送端**：`packaging/Info.plist` → `CFBundleShortVersionString`；
  - **iPad 接收端**：`ipad-receiver/MacScreenReceiver-iPad.xcodeproj/project.pbxproj` → `MARKETING_VERSION`（Debug/Release 各一处）；
  - **Android 接收端**：`android-receiver/app/build.gradle.kts` → `versionName`（对外显示）与 `versionCode`（每次发版 +1）。

---

## 一次性环境准备（新机器才需要）

| 依赖 | 用途 | 检查方法 |
|---|---|---|
| Apple Developer ID Application 证书 | Mac 端签名 | `security find-identity -v -p codesigning` 能看到证书 |
| notarytool 钥匙串档案 `notary-profile` | 苹果公证免密提交 | `xcrun notarytool history --keychain-profile notary-profile` |

公证档案不存在时重新存储一次（用 Apple ID + App 专用密码）：

```bash
xcrun notarytool store-credentials notary-profile \
  --apple-id "你的AppleID" --team-id BFX35MFS32 --password "App专用密码"
```

- GitHub 推送走 **SSH**（本机 443 直连 GitHub 不通）：`git remote -v` 应为 `git@github.com:alan8182/MacScreenExtender.git`；
- Mac 端构建在本机完成（依赖你的证书），Windows 端由 GitHub Actions 云端构建，无需本地 .NET 环境。

---

## 标准发版步骤

### 1️⃣ 发版前检查

- 确认 [ISSUES.md](ISSUES.md) 无 🔴 未修复的关键问题，或已在版本说明中声明；
- 三端版本号已按上表同步更新；
- `git status` 干净，所有改动已提交。

### 2️⃣ 打标签并推送（触发 Windows 端自动构建）

```bash
git tag -a v1.x.x -m "v1.x.x 版本一句话说明"
git push origin main v1.x.x
```

推送 `v*` 标签会自动触发 **Build Windows Receiver** 工作流：编译 win-x64 → 下载 FFmpeg 8.1 运行时 → 打包 zip → 自动创建该版本的 Release 并附加 `MacScreenReceiver-win64.zip`。构建进度见仓库 **Actions** 页（约 3~5 分钟），也可在 Actions 页手动触发（workflow_dispatch）先验证。

### 3️⃣ 本地构建 Mac 端公证 DMG

```bash
./release-mac.sh
```

脚本自动完成：Swift Release 编译 → 组装 .app → Developer ID 签名（Hardened Runtime）→ 打 DMG → 提交苹果公证 → 装订票据。成功标志：`status: Accepted` 与 `The staple and validate action worked!`，产物在 `build/MacScreenSender.dmg`。

> 需要覆盖证书时可显式指定：`./release-mac.sh "Developer ID Application: 公司名 (TEAMID)"`，或设环境变量 `DEVELOPER_ID_APP_CERT`。

### 4️⃣ 上传 DMG 到 Release

打开 `https://github.com/alan8182/MacScreenExtender/releases/edit/v1.x.x`（步骤 2 的 CI 已自动建好该 Release），把 `build/MacScreenSender.dmg` 拖入附件区保存。Release 说明可用自动生成的（changelog），建议再补充一段面向用户的中文更新说明。

### 5️⃣ 发布后验证（重要，别省）

- 下载 Release 里的 DMG 到一台**未装过本应用**的 Mac，双击安装应无 Gatekeeper 拦截（公证生效）；
- 下载 zip 到一台 Windows 机器，解压运行，确认 SmartScreen 提示可正常「仍要运行」、能发现并连接 Mac；
- 若本机重新安装了新版：因 Bundle ID 变更（`com.macscreextender.*`）或版本升级，需先删除旧 app 再装，并**重新授权屏幕录制与辅助功能权限**。

---

## 各端产物一览

| 端 | 产物 | 构建方式 | 用户获取方式 |
|---|---|---|---|
| Mac 发送端 | `MacScreenSender.dmg`（签名+公证） | 本地 `./release-mac.sh` | Releases 下载双击安装 |
| Windows 接收端 | `MacScreenReceiver-win64.zip` | CI 自动（推 tag 触发） | Releases 下载解压即用（需 .NET 8 Desktop Runtime） |
| iPad 接收端 | 无预编译产物 | 用户自行 Xcode 构建 | 源码 `ipad-receiver/` |
| Android 接收端 | 无预编译产物（待真机联调） | 用户自行 Android Studio 构建 | 源码 `android-receiver/` |

---

## 常见问题

**公证卡在 `In Progress` 很久 / 提交失败**
苹果公证服务偶发拥堵，重跑 `./release-mac.sh` 即可（全流程幂等，约 2~4 分钟）。失败后可查详细日志：`xcrun notarytool log <SubmissionID> --keychain-profile notary-profile`。

**CI 构建失败：下载 FFmpeg 那一步 404**
工作流使用 BtbN 的 `ffmpeg-n8.1-latest-win64-lgpl-shared-8.1.zip`（匹配 `FFmpeg.AutoGen 8.1.0` 按名加载的 `avcodec-62.dll` 等）。BtbN 会随大版本下线旧线资产：若 FFmpeg 升级到 9.x，需同步升级 `FFmpeg.AutoGen` 包版本并改工作流里的下载 URL 为对应版本线。

**CI 构建成功但 Release 没建出来**
检查 Actions 运行日志中「附加到 GitHub Release」步骤的 `GITHUB_TOKEN` 权限（工作流已声明 `permissions: contents: write`）；fork 仓库默认禁止 workflow 写 Release。

**`git push` 连不上 GitHub**
本机 443 到 GitHub 不通属网络环境问题，确认远端为 SSH（`git remote set-url origin git@github.com:alan8182/MacScreenExtender.git`）后重试；SSH 密钥已配置在 GitHub 账号 `alan8182` 下。

**误把构建产物提交进仓库**
`.gitignore` 已排除 `*.dmg`、`*.zip`、`.build/`、`windows-receiver/publish/` 等；发版产物只存在于 `build/` 与 GitHub Release，不入库。
