#!/usr/bin/env python3
"""Every project the given ones depend on, transitively — the set that has to be packed together.

A ProjectReference becomes a package *dependency* when NuGet packs it, so shipping a root without
its closure produces packages that cannot restore. Printing the closure keeps that honest without
a hand-maintained list going stale on the next `dotnet add reference`.

Analyzer/generator references (ReferenceOutputAssembly=false) are excluded: they are build-time
tools, not dependencies of the consumer, and NuGet does not emit a dependency for them.
"""
import re
import sys
from pathlib import Path

REFERENCE = re.compile(r'<ProjectReference\s+Include="([^"]+)"([^>]*)>?', re.IGNORECASE)


def references(project: Path):
    text = project.read_text(encoding="utf-8", errors="replace")
    for include, rest in REFERENCE.findall(text):
        if "ReferenceOutputAssembly=\"false\"" in rest:
            continue
        yield (project.parent / include.replace("\\", "/")).resolve()


def main(roots):
    seen, queue = [], [Path(r).resolve() for r in roots]
    while queue:
        project = queue.pop()
        if project in seen or not project.exists():
            continue
        seen.append(project)
        queue.extend(references(project))
    root = Path.cwd()
    for project in seen:
        print(project.relative_to(root))


if __name__ == "__main__":
    main(sys.argv[1:])
