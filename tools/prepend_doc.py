import sys
from pathlib import Path

path = Path(sys.argv[1])
section = Path(sys.argv[2]).read_text(encoding="utf-8")
body = path.read_text(encoding="utf-8")
if body.startswith("\ufeff"):
    body = body[1:]
path.write_text(section + body, encoding="utf-8")
print(f"updated {path} ({len(section)} chars added)")
