#!/usr/bin/env python3
import re
import sys
from pathlib import Path

log_file = Path(sys.argv[1]) if len(sys.argv) > 1 else Path("build-libs.log")
out_file = Path(sys.argv[2]) if len(sys.argv) > 2 else Path("build-libs-diagnostics.log")

start_re = re.compile(
    r"""(
        warning: |
        error: |
        fatal\ error: |
        undefined\ reference |
        collect2:\ error |
        No\ such\ file |
        cannot\ find |
        failed |
        \bld: |
        make(\[[0-9]+\])?:\ \*\*\*
    )""",
    re.IGNORECASE | re.VERBOSE,
)

follow_re = re.compile(
    r"""(
        ^\s*[0-9]+\s+\| |                              # formatted source line: "  51 | ..."
        ^\s*\| |                                       # continuation line with only pipe
        ^\s*[\^~]+ |                                  # caret / tilde marker line
        ^In\ file\ included\ from |                   # include chain start
        ^\s*from\s+ |                                 # include chain continuation
        ^.*:\d+:\d+:\s+note: |                        # gcc/clang note with file:line:col
        ^.*:\d+:\d+:\s+warning: |                     # nested warning line
        ^.*:\d+:\d+:\s+error: |                       # nested error line
        ^\s*note:\s+ |                                # generic note line
        ^\s*required\ from |                          # template instantiation trail
        ^\s*instantiated\ from |                      # older compiler wording
        ^\s*declared\ here |                          # occasional continuation wording
        ^\s*expanded\ from\ macro |                   # clang macro expansion wording
        ^\s*in\ expansion\ of\ macro |                # gcc macro expansion wording
        ^\s*\.\.\. |                                  # truncated continuation
        ^\s*$                                         # blank line inside a diagnostic block
    )""",
    re.IGNORECASE | re.VERBOSE,
)

if not log_file.exists():
    print(f"Input log not found: {log_file}", file=sys.stderr)
    sys.exit(1)

lines = log_file.read_text(encoding="utf-8", errors="replace").splitlines()

blocks = []
current = []
capturing = False

def flush():
    global current
    if current:
        # trim trailing blank lines inside the block
        while current and not current[-1].strip():
            current.pop()
        if current:
            blocks.append("\n".join(current))
        current = []

for line in lines:
    if start_re.search(line):
        flush()
        capturing = True
        current.append(line)
        continue

    if capturing and follow_re.search(line):
        current.append(line)
        continue

    if capturing:
        flush()
        capturing = False

if current:
    flush()

out_file.write_text("\n\n".join(blocks) + ("\n" if blocks else ""), encoding="utf-8")
print(f"Wrote: {out_file}")
print(f"Blocks: {len(blocks)}")