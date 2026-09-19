#!/usr/bin/env bash
# Выкладка игрового сервера на VPS: сборка под linux-x64 (с рантаймом .NET — на сервере его ставить не нужно),
# клиент, shared/*.json → /opt/sro, перезапуск службы sro. Аккаунты (/opt/sro/data) не трогаются.
#
# На сервере один раз настроены: пользователь sro, systemd-служба sro (127.0.0.1:5100), сайт nginx «sro»
# с сертификатом Let's Encrypt для 158-255-0-216.sslip.io. Играть: https://alexandrp1902.github.io/sro2/?server=158-255-0-216.sslip.io
#
#   tools/deploy.sh                  # на root@158.255.0.216
#   SRO_HOST=root@1.2.3.4 tools/deploy.sh
set -euo pipefail

HOST="${SRO_HOST:-root@158.255.0.216}"
ROOT="$(cd "$(dirname "$0")/.." && pwd)"
OUT="$ROOT/server/bin/_deploy"

rm -rf "$OUT"
mkdir -p "$OUT"
dotnet publish "$ROOT/server/Sro.Server" -c Release -r linux-x64 --self-contained -o "$OUT/app" -v q
(cd "$ROOT/client" && npm run build)

# Относительные пути: tar в Git Bash принимает «C:\…» за адрес удалённой машины.
cd "$ROOT"
tar -czf server/bin/_deploy/app.tgz -C server/bin/_deploy/app .
tar -czf server/bin/_deploy/static.tgz shared client/dist

scp server/bin/_deploy/app.tgz server/bin/_deploy/static.tgz "$HOST:/tmp/"
ssh "$HOST" 'set -e
  rm -rf /opt/sro/app.new /opt/sro/static.new
  mkdir -p /opt/sro/app.new /opt/sro/static.new
  tar -xzf /tmp/app.tgz -C /opt/sro/app.new
  tar -xzf /tmp/static.tgz -C /opt/sro/static.new
  rm /tmp/app.tgz /tmp/static.tgz
  systemctl stop sro
  rm -rf /opt/sro/app /opt/sro/shared /opt/sro/client
  mv /opt/sro/app.new /opt/sro/app
  mv /opt/sro/static.new/shared /opt/sro/shared
  mv /opt/sro/static.new/client /opt/sro/client
  rmdir /opt/sro/static.new
  chmod +x /opt/sro/app/Sro.Server
  chown -R sro:sro /opt/sro
  systemctl start sro
  sleep 3
  systemctl is-active sro'
echo "https://158-255-0-216.sslip.io: HTTP $(curl -fsS -o /dev/null -w '%{http_code}' https://158-255-0-216.sslip.io/)"
