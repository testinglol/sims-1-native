#!/usr/bin/env python3
"""Transpile SPIR-V shaders to HLSL Shader Model 6.0 for Direct3D 12, verify VS->PS stage linkage, and compile DXIL when dxc is available."""

from __future__ import annotations

import pathlib
import re
import shutil
import subprocess
import os
import sys

PIPELINE_PAIRS = [
    ("sprite.vert", "sprite.frag"),
    ("sprite.vert", "ghost.frag"),
    ("sprite.vert", "spriteDepth.frag"),
    ("sprite.vert", "censor.frag"),
    ("terrain.vert", "terrain.frag"),
    ("terrain.vert", "placement.frag"),
    ("avatar.vert", "avatar.frag"),
    ("avatar.vert", "surface.frag"),
    ("avatar.vert", "avatarMask.frag"),
]

STRUCT_RE = re.compile(r"struct\s+(SPIRV_Cross_(?:Input|Output))\s*\{(.*?)\};", re.DOTALL)
FIELD_RE = re.compile(r"^\s*(nointerpolation\s+)?(\w+)\s+\w+\s*:\s*([A-Za-z0-9_]+)\s*;\s*$")


def parse_stage_struct(hlsl: str, struct_name: str) -> list[tuple[str, str, str]]:
    for m in STRUCT_RE.finditer(hlsl):
        if m.group(1) != struct_name:
            continue
        fields: list[tuple[str, str, str]] = []
        for line in m.group(2).splitlines():
            if not line.strip():
                continue
            fm = FIELD_RE.match(line)
            if not fm:
                raise RuntimeError(f"Unrecognized HLSL stage struct field in {struct_name}: {line!r}")
            interp = (fm.group(1) or "").strip()
            ty = fm.group(2).strip()
            sem = fm.group(3).strip()
            fields.append((interp, ty, sem))
        return fields
    return []


def find_dxc() -> str | None:
    env_dxc = os.environ.get("DXC")
    if env_dxc and pathlib.Path(env_dxc).is_file():
        return env_dxc
    which_dxc = shutil.which("dxc")
    if which_dxc:
        return which_dxc
    if sys.platform == "win32":
        kits = pathlib.Path(os.environ.get("ProgramFiles(x86)", r"C:\Program Files (x86)")) / "Windows Kits" / "10" / "bin"
        if kits.is_dir():
            candidates = sorted(kits.glob("10.0.*/x64/dxc.exe"), reverse=True)
            if candidates:
                return str(candidates[0])
    return None


def main() -> int:
    shaders_dir = (
        pathlib.Path(sys.argv[1])
        if len(sys.argv) > 1
        else pathlib.Path(__file__).resolve().parents[1] / "native" / "SimsSDL" / "Shaders"
    )
    hlsl_dir = shaders_dir / "hlsl"
    hlsl_dir.mkdir(parents=True, exist_ok=True)

    generated: dict[str, str] = {}
    for spv in sorted(shaders_dir.glob("*.spv")):
        stem = spv.name[: -len(".spv")]
        hlsl = subprocess.check_output(
            ["spirv-cross", str(spv), "--hlsl", "--shader-model", "60"],
            text=True,
        )
        hlsl = "\n".join(line.rstrip() for line in hlsl.splitlines()).rstrip() + "\n"
        out_path = hlsl_dir / f"{stem}.hlsl"
        out_path.write_text(hlsl, encoding="utf-8")
        generated[stem] = hlsl
        print(out_path.name)

    for vert_name, frag_name in PIPELINE_PAIRS:
        vert_out = parse_stage_struct(generated[vert_name], "SPIRV_Cross_Output")
        frag_in = parse_stage_struct(generated[frag_name], "SPIRV_Cross_Input")
        if len(frag_in) > len(vert_out):
            raise RuntimeError(
                f"D3D12 stage linkage mismatch ({vert_name} -> {frag_name}): "
                f"PS has {len(frag_in)} inputs {frag_in}, VS only has {len(vert_out)} outputs {vert_out}"
            )
        for idx, ps_field in enumerate(frag_in):
            vs_field = vert_out[idx]
            ps_interp, ps_ty, ps_sem = ps_field
            vs_interp, vs_ty, vs_sem = vs_field
            # SV_Position in PS does not carry nointerpolation even if VS omits it.
            interp_ok = ps_interp == vs_interp or ps_sem == "SV_Position"
            if not interp_ok or ps_ty != vs_ty or ps_sem != vs_sem:
                raise RuntimeError(
                    f"D3D12 stage linkage mismatch ({vert_name} -> {frag_name}) at slot {idx}: "
                    f"VS={vs_field} vs PS={ps_field}"
                )

    dxc = find_dxc()
    if dxc:
        for hlsl_file in sorted(hlsl_dir.glob("*.hlsl")):
            stem = hlsl_file.name[: -len(".hlsl")]
            profile = "vs_6_0" if stem.endswith(".vert") else "ps_6_0"
            dxil_out = shaders_dir / f"{stem}.dxil"
            subprocess.check_call([dxc, "-T", profile, "-E", "main", "-O3", "-Fo", str(dxil_out), str(hlsl_file)])
            print(dxil_out.name)

    return 0


if __name__ == "__main__":
    raise SystemExit(main())
