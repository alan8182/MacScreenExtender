#!/bin/bash
# ==============================================================================
# MacScreenSender 官方商业化发布脚本：构建、代码签名、苹果公证与装订
# ==============================================================================
set -euo pipefail
cd "$(dirname "$0")"

# 优先从首个参数或环境变量读取，否则自动探测本地钥匙串中的 Developer ID 证书
CERT="${1:-${DEVELOPER_ID_APP_CERT:-}}"
if [ -z "$CERT" ]; then
    CERT=$(security find-identity -v -p codesigning 2>/dev/null | grep "Developer ID Application:" | head -n 1 | sed -E 's/.*"(.*)"/\1/' || true)
fi
if [ -z "$CERT" ]; then
    CERT="Developer ID Application: Jiezi Xingchen (Hangzhou) Intelligent Technology Co., Ltd. (BFX35MFS32)"
fi

PROFILE="${NOTARY_PROFILE:-notary-profile}"
APP="build/MacScreenSender.app"
DMG="build/MacScreenSender.dmg"
DMG_ROOT="build/dmg_root"

echo "=================================================="
echo "🚀 步骤 1/5: 正在编译发布版二进制 (Swift Release)..."
echo "=================================================="
swift build -c release

echo "=================================================="
echo "📦 步骤 2/5: 组装 MacScreenSender.app 包结构..."
echo "=================================================="
rm -rf "$APP"
mkdir -p "$APP/Contents/MacOS"
mkdir -p "$APP/Contents/Resources"

cp .build/release/MacScreenSender "$APP/Contents/MacOS/MacScreenSender"
cp packaging/Info.plist "$APP/Contents/Info.plist"

if [ -f packaging/AppIcon.icns ]; then
    cp packaging/AppIcon.icns "$APP/Contents/Resources/AppIcon.icns"
fi

echo "=================================================="
echo "🔑 步骤 3/5: 使用 Developer ID 证书签署 App (开启 Hardened Runtime)..."
echo "=================================================="
codesign --force --deep --options runtime --timestamp -s "$CERT" "$APP"

echo "🔍 验证 App 本地签名状态:"
codesign -dv --verbose=2 "$APP"

echo "=================================================="
echo "💿 步骤 4/5: 生成用户安装镜像 DMG 并签署..."
echo "=================================================="
rm -rf "$DMG_ROOT" "$DMG"
mkdir -p "$DMG_ROOT"
cp -R "$APP" "$DMG_ROOT/"
ln -s /Applications "$DMG_ROOT/Applications"

hdiutil create -volname "MacScreenSender" -srcfolder "$DMG_ROOT" -ov -format UDZO "$DMG"
codesign --force --timestamp -s "$CERT" "$DMG"
rm -rf "$DMG_ROOT"

echo "=================================================="
echo "☁️ 步骤 5/5: 提交苹果官方公证 (Notarization)..."
echo "=================================================="
echo "正在上传 DMG 到苹果安全公证服务器，这通常需要 1~3 分钟，请稍候..."
xcrun notarytool submit "$DMG" --keychain-profile "$PROFILE" --wait

echo "=================================================="
echo "📎 正在将苹果官方公证票据装订到 DMG 和 APP (Stapling)..."
echo "=================================================="
xcrun stapler staple "$DMG"
xcrun stapler staple "$APP"

echo "=================================================="
echo "🎉 商业化发布包制作完成！"
echo "=================================================="
echo "✅ 已生成官方公证 DMG 安装包: $DMG"
echo "✅ 已生成官方公证 App: $APP"
echo "💡 此安装包已附带 Apple 官方背书，全球所有 Mac 用户下载双击即可直接运行，无任何安全阻拦！"
