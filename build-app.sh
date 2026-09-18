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

if [ $# -ge 1 ]; then
    codesign --force -s "$1" "$APP"
    echo "🔑 已用证书 [$1] 签名"
else
    codesign --force -s - "$APP"
    echo "⚠️  ad-hoc 签名：重新打包后需重新授予屏幕录制/辅助功能权限"
    echo "    （创建自签证书后执行 ./build-app.sh <证书名> 可一劳永逸）"
fi

echo "✅ 已生成 $APP"
echo "   双击运行（菜单栏出现 🖥 图标）；建议拖入 /Applications 后再开开机自启"
