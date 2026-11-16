#!/usr/bin/env python3
from pathlib import Path
import json
import re
import subprocess
import sys

sys.path.insert(0, str(Path(__file__).resolve().parent / "checks"))
from check_sdl_metal_bindings import check

directory = Path(sys.argv[1]) if len(sys.argv) > 1 else Path(__file__).resolve().parents[1] / "native/SimsSDL/Shaders"
for source in sorted(directory.glob("*.spv")):
    reflection = json.loads(subprocess.check_output(["spirv-cross", str(source), "--reflect"], text=True))
    command = ["spirv-cross", str(source), "--msl", "--msl-version", "20100"]
    if not reflection.get("ssbos"):
        command.append("--msl-decoration-binding")
    output = subprocess.check_output(command, text=True)
    output = "\n".join(line.rstrip() for line in output.splitlines()).rstrip() + "\n"
    problems = check(source, output)
    if problems:
        raise RuntimeError("\n".join(problems))
    if source.name.endswith(".vert.spv") and any(block["name"] == "Quad" for block in reflection.get("ubos", [])):
        assert re.search(r"constant Quad& quad \[\[buffer\(0\)\]\]", output), source
        if reflection.get("ssbos"):
            assert re.search(r"const device Vertices& \w+ \[\[buffer\(1\)\]\]", output), source
    source.with_suffix(".msl").write_text(output)
    print(source.with_suffix(".msl").name)
