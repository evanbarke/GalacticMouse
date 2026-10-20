#!/usr/bin/env bash
# Run on 192.168.1.129 after the Squarespace A record resolves publicly.
set -euo pipefail

host=galactamouse.samix-technology.com
expected_ip=77.204.251.99
root=/home/evanb/galacticmouse
nginx_config=/home/evanb/mailcow-dockerized/data/conf/nginx/00-galacticmouse.conf
container=mailcowdockerized-nginx-mailcow-1

if ! dig +short A "$host" @8.8.8.8 | grep -Fxq "$expected_ip"; then
    echo "DNS for $host does not yet resolve to $expected_ip" >&2
    exit 1
fi

sudo certbot certonly --webroot \
    -w /home/evanb/mailcow-dockerized/data/web \
    -d "$host" --cert-name "$host" \
    --non-interactive --agree-tos --email evan@samix-technology.com

backup="${nginx_config}.backup-$(date +%Y%m%d%H%M%S)"
cp "$nginx_config" "$backup"
cp "$root/deploy/mailcow-nginx.conf" "$nginx_config"
if ! docker exec "$container" nginx -t; then
    cp "$backup" "$nginx_config"
    echo "nginx validation failed; restored $backup" >&2
    exit 1
fi
docker exec "$container" nginx -s reload
curl -fsS "https://$host/health"
echo
echo "Dedicated GalacticMouse host is active. Backup: $backup"
