#!/bin/bash
# 组装 InputMonitor.app 并签名
# 优先使用自签名证书 "InputMonitor Dev Cert"（签名身份跨构建稳定，TCC 权限一次授权长期有效）；
# 证书不存在时回退 ad-hoc 签名（每次构建 cdhash 变化，需重新授权）。
# 用法: ./Scripts/bundle.sh [--run] [--debug]
set -euo pipefail

cd "$(dirname "$0")/.."

CONFIG="release"
RUN_APP=0
for arg in "$@"; do
    case "$arg" in
        --debug) CONFIG="debug" ;;
        --run)   RUN_APP=1 ;;
    esac
done

APP_NAME="InputMonitor"
APP_DIR="dist/${APP_NAME}.app"
BINARY=".build/${CONFIG}/${APP_NAME}"

echo "==> swift build -c ${CONFIG}"
swift build -c "${CONFIG}"

echo "==> 组装 ${APP_DIR}"
rm -rf "${APP_DIR}"
mkdir -p "${APP_DIR}/Contents/MacOS" "${APP_DIR}/Contents/Resources"
cp "${BINARY}" "${APP_DIR}/Contents/MacOS/${APP_NAME}"
cp "Resources/Info.plist" "${APP_DIR}/Contents/Info.plist"
cp "Resources/AppIcon.icns" "${APP_DIR}/Contents/Resources/AppIcon.icns"

SIGN_IDENTITY="InputMonitor Dev Cert"
if security find-certificate -c "${SIGN_IDENTITY}" ~/Library/Keychains/login.keychain-db >/dev/null 2>&1; then
    echo "==> 自签名证书签名 (${SIGN_IDENTITY})"
    codesign --force --sign "${SIGN_IDENTITY}" --identifier com.local.inputmonitor "${APP_DIR}"
else
    echo "==> 自签名证书不存在，回退 ad-hoc 签名（注意：重新构建后需重新授权 TCC 权限）"
    codesign --force --sign - --identifier com.local.inputmonitor "${APP_DIR}"
fi

if [ "${RUN_APP}" -eq 1 ]; then
    echo "==> 运行"
    pkill -x "${APP_NAME}" 2>/dev/null || true
    open "${APP_DIR}"
fi

echo "==> 完成: ${APP_DIR}"
