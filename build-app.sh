#!/bin/bash
# 打包 MacScreenSender.app（菜单栏应用）
#
# 用法:
#   ./build-app.sh              # ad-hoc 签名
#   ./build-app.sh <证书名>     # 用固定自签证书签名（推荐，见下）
#
# ⚠️ TCC 授权与签名身份绑定（重要）:
#   屏幕录制/辅助功能权限记录的是 App 的签名身份。
#   - ad-hoc 签名（默认）: 每次重新打包都会丢权限，要重新去系统设置里授权；
#   - 固定证书签名: 在「钥匙串访问 → 证书助理 → 创建证书」创建一个
#     「代码签名」类型证书（名字自取，如 MacScreenSender），之后执行
#     ./build-app.sh MacScreenSender —— 重新打包不再丢权限。
set -euo pipefail
cd "$(dirname "$0")"

swift build -c release

APP="build/MacScreenSender.app"
rm -rf "$APP"
mkdir -p "$APP/Contents/MacOS"
cp .build/release/MacScreenSender "$APP/Contents/MacOS/MacScreenSender"
cp packaging/Info.plist "$APP/Contents/Info.plist"
if [ -f packaging/AppIcon.icns ]; then
    mkdir -p "$APP/Contents/Resources"
    cp packaging/AppIcon.icns "$APP/Contents/Resources/AppIcon.icns"
fi

# 优先使用传入参数，未传入时自动探测本地签名证书（Apple Development / Developer ID），若无则使用 ad-hoc
SIGN_ID="${1:-}"
if [ -z "$SIGN_ID" ]; then
    SIGN_ID=$(security find-identity -v -p codesigning 2>/dev/null | grep -E "Developer ID Application|Apple Development" | head -n 1 | sed -E 's/.*"(.*)"/\1/' || true)
fi

if [ -z "$SIGN_ID" ] || [ "$SIGN_ID" = "-" ]; then
    codesign --force -s - "$APP"
    echo "⚠️  ad-hoc 签名（本地未检测到开发者证书）：重新打包后需在系统设置中重新授权 TCC 权限"
else
    codesign --force -s "$SIGN_ID" "$APP"
    echo "🔑 已用证书 [$SIGN_ID] 签名"
fi

# 自动安装到 /Applications（用户目录下的 App 容易被系统重置 TCC 授权）
rm -rf "/Applications/MacScreenSender.app"
cp -R "$APP" /Applications/
echo "✅ 已安装到 /Applications/MacScreenSender.app"
echo "   启动：启动台/Launchpad 或 open -a MacScreenSender"
