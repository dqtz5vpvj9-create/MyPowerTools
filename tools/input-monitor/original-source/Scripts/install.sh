#!/bin/bash
# 构建（稳定证书签名）→ 安装到 /Applications → 启动
# 用法: ./Scripts/install.sh [--debug]
set -euo pipefail

cd "$(dirname "$0")/.."

APP_NAME="InputMonitor"
TARGET="/Applications/${APP_NAME}.app"

# 1. 构建并签名（复用 bundle.sh，透传 --debug）
./Scripts/bundle.sh "$@"

# 2. 安装到 /Applications
echo "==> 安装到 ${TARGET}"
pkill -x "${APP_NAME}" 2>/dev/null || true
rm -rf "${TARGET}"
cp -R "dist/${APP_NAME}.app" "${TARGET}"
xattr -dr com.apple.quarantine "${TARGET}" 2>/dev/null || true

# 3. 启动
echo "==> 启动"
open "${TARGET}"

echo "==> 完成: ${TARGET}"
echo "提示: 签名身份稳定（InputMonitor Dev Cert），覆盖安装无需重新授予 TCC 权限"
