#!/usr/bin/env python3
import hashlib
import json
from pathlib import Path
import sys

pack = Path(sys.argv[1])
files = pack / "files"
resources = {path.relative_to(files).as_posix().lower(): hashlib.sha256(path.read_bytes()).hexdigest()
             for path in sorted(files.rglob("*")) if path.is_file()}
(pack / "plugin/resources.json").write_text(json.dumps(resources, indent=2, sort_keys=True) + "\n")
