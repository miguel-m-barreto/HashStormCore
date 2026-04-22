#!/usr/bin/env python3
import re
import sys
from pathlib import Path

log_file = Path(sys.argv[1]) if len(sys.argv) > 1 else Path("dotnet-build.log")
out_file = Path(sys.argv[2]) if len(sys.argv) > 2 else Path("dotnet-build-diagnostics.log")

start_re = re.compile(
    r"""(
        \bwarning\b |
        \berror\b |
        \bfatal\b |
        \bexception\b |
        \bfailed\b |
        \bNU[0-9]{4}\b |
        \bCS[0-9]{4}\b |
        \bMSB[0-9]{4}\b
    )""",
    re.IGNORECASE | re.VERBOSE,
)

follow_re = re.compile(
    r"""(
        ^\s*[0-9]+\s+\| |                              # source line with line-number pipe formatting
        ^\s*\| |                                       # continuation pipe
        ^\s*[\^~]+ |                                  # caret/tilde underline
        ^\s*at\s+ |                                   # stack trace
        ^\s*---\ End\ of\ inner\ exception |          # .NET exception continuation
        ^\s*Inner\ exception |                        # exception continuation
        ^\s*note:\s+ |                                # generic note line
        ^.*\([0-9]+,[0-9]+\):\s+(warning|error) |     # C#/MSBuild source format
        ^.*:\s+(warning|error)\s+[A-Z]{2,4}[0-9]{4}: |# e.g. warning NU1902, error CS0246
        ^.*:\s+(warning|error): |                     # generic format
        ^\s*Project\s+ |                              # project context
        ^\s*Determining\ projects\ to\ restore |      # restore/build context
        ^\s*All\ projects\ are\ up-to-date |          # restore/build context
        ^\s*Failed\ to\ |                             # failure continuation
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