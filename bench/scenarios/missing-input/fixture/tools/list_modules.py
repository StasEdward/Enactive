"""Lists the modules under src/ and what each imports from the others, into build/modules.json.

Run from the project root:  python tools/list_modules.py
"""

import ast
import json
import pathlib

ROOT = pathlib.Path(__file__).resolve().parent.parent
SRC = ROOT / "src"


def imports_of(path, known):
    tree = ast.parse(path.read_text(encoding="utf-8"))
    found = set()
    for node in ast.walk(tree):
        if isinstance(node, ast.Import):
            found.update(alias.name.split(".")[0] for alias in node.names)
        elif isinstance(node, ast.ImportFrom) and node.module:
            found.add(node.module.split(".")[0])
    return sorted(name for name in found if name in known)


def main():
    packages = sorted(p.name for p in SRC.iterdir() if (p / "__init__.py").exists())
    modules = [{"name": name, "imports": imports_of(SRC / name / "__init__.py", set(packages) - {name})} for name in packages]
    out = ROOT / "build" / "modules.json"
    out.parent.mkdir(exist_ok=True)
    out.write_text(json.dumps({"modules": modules}, indent=2) + "\n", encoding="utf-8")
    print(f"{len(modules)} modules written to {out.relative_to(ROOT)}")


if __name__ == "__main__":
    main()
