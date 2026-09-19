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

# 默认用 Apple Development 证书：签名身份稳定，重新打包不丢 TCC 权限。
# 无参数时自动选择；传 "-" 强制 ad-hoc；传证书名覆盖默认
SIGN_ID="${1:-Apple Development: Yunfei Yang (55832U3GFU)}"
if [ "$SIGN_ID" = "-" ]; then
    codesign --force -s - "$APP"
    echo "⚠️  ad-hoc 签名：重新打包后需重新授权"
else
    codesign --force -s "$SIGN_ID" "$APP"
    echo "🔑 已用证书 [$SIGN_ID] 签名"
fi

# 自动安装到 /Applications（用户目录下的 App 容易被系统重置 TCC 授权）
rm -rf "/Applications/MacScreenSender.app"
cp -R "$APP" /Applications/
echo "✅ 已安装到 /Applications/MacScreenSender.app"
echo "   启动：启动台/Launchpad 或 open -a MacScreenSender"
