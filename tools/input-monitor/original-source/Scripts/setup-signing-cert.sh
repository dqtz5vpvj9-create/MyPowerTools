#!/bin/bash
# 创建自签名代码签名证书 "InputMonitor Dev Cert" 并导入登录钥匙串。
# 用途：稳定签名身份 → macOS TCC（辅助功能/输入监控）权限跨构建保持有效，
#       避免 ad-hoc 签名 cdhash 每次构建变化导致的重复授权。
# 用法: ./Scripts/setup-signing-cert.sh   （重复执行安全，已存在则跳过）
set -euo pipefail

CERT_NAME="InputMonitor Dev Cert"
KEYCHAIN="$HOME/Library/Keychains/login.keychain-db"

if security find-certificate -c "${CERT_NAME}" "${KEYCHAIN}" >/dev/null 2>&1; then
    echo "证书已存在: ${CERT_NAME}，无需重复创建"
    exit 0
fi

WORKDIR="$(mktemp -d)"
trap 'rm -rf "${WORKDIR}"' EXIT

cat > "${WORKDIR}/cert.conf" << EOF
[ req ]
distinguished_name = dn
x509_extensions = ext
prompt = no
[ dn ]
CN = ${CERT_NAME}
[ ext ]
keyUsage = critical, digitalSignature
extendedKeyUsage = critical, codeSigning
basicConstraints = critical, CA:false
EOF

openssl req -x509 -newkey rsa:2048 \
    -keyout "${WORKDIR}/key.pem" -out "${WORKDIR}/cert.pem" \
    -days 3650 -nodes -config "${WORKDIR}/cert.conf" 2>/dev/null

# -name 设置 friendlyName，保证 macOS 钥匙串正确关联证书与私钥
openssl pkcs12 -export \
    -out "${WORKDIR}/dev.p12" \
    -inkey "${WORKDIR}/key.pem" -in "${WORKDIR}/cert.pem" \
    -name "${CERT_NAME}" -passout pass:imdev

# -A 允许任何应用（codesign）使用私钥，避免签名时弹窗
security import "${WORKDIR}/dev.p12" -k "${KEYCHAIN}" -P imdev -A

echo "完成: 证书 '${CERT_NAME}' 已导入登录钥匙串"
echo "验证: codesign --sign '${CERT_NAME}' 可直接使用（find-identity 可能不显示，属正常）"
