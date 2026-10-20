"""Install the live path route into the existing Mailcow samix HTTPS vhost."""
from pathlib import Path
import shutil
import subprocess
import time

config = Path("/home/evanb/mailcow-dockerized/data/conf/nginx/00-samix.conf")
fragment = Path(__file__).with_name("mailcow-path.conf").read_text()
original = config.read_text()
marker = "location = /galacticmouse/ws"
anchor = "    location / {\n        proxy_pass http://172.22.1.1:9467;"
if marker in original:
    print("GalacticMouse path route already installed")
    raise SystemExit(0)
if original.count(anchor) != 1:
    raise SystemExit("Could not uniquely locate HTTPS proxy block; no changes made")
backup = config.with_name(f"00-samix.conf.galacticmouse-bak-{int(time.time())}")
shutil.copy2(config, backup)
try:
    config.write_text(original.replace(anchor, fragment + "\n" + anchor))
    subprocess.run(["docker", "exec", "mailcowdockerized-nginx-mailcow-1", "nginx", "-t"], check=True)
    subprocess.run(["docker", "exec", "mailcowdockerized-nginx-mailcow-1", "nginx", "-s", "reload"], check=True)
except Exception:
    shutil.copy2(backup, config)
    subprocess.run(["docker", "exec", "mailcowdockerized-nginx-mailcow-1", "nginx", "-s", "reload"])
    raise
print(f"Installed GalacticMouse path route; backup: {backup}")
