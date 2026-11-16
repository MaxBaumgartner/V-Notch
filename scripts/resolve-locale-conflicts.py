"""Resolves git conflicts in Locales/*.json by keeping both sides' keys.

Upstream and the fork both append keys at the end of the flat locale files, so a cherry-pick conflicts in every
language at once. For each conflict hunk this keeps our lines and then upstream's lines (joined with a comma) and
drops upstream lines whose key already exists in ours, then checks that the file parses and stages it.

    python scripts/resolve-locale-conflicts.py
"""
import json
import pathlib
import re
import subprocess
import sys

KEY = re.compile(r'^\s*"((?:[^"\\]|\\.)*)"\s*:')


def resolve(text: str) -> str:
    out, i = [], 0
    lines = text.splitlines(keepends=True)
    while i < len(lines):
        if not lines[i].startswith('<<<<<<< '):
            out.append(lines[i]); i += 1; continue
        ours, theirs, j = [], [], i + 1
        while not lines[j].startswith('======='):
            ours.append(lines[j]); j += 1
        j += 1
        while not lines[j].startswith('>>>>>>> '):
            theirs.append(lines[j]); j += 1
        i = j + 1
        keys = {m.group(1) for line in ours if (m := KEY.match(line))}
        theirs = [line for line in theirs if not ((m := KEY.match(line)) and m.group(1) in keys)]
        merged = [line for line in ours if line.strip()]
        extra = [line for line in theirs if line.strip()]
        if merged and extra:
            last = merged[-1].rstrip('\r\n')
            ending = merged[-1][len(last):] or '\n'
            if not last.rstrip().endswith((',', '{')):
                merged[-1] = last + ',' + ending
        out.extend(merged + extra)
    return ''.join(out)


def main() -> int:
    root = pathlib.Path(__file__).resolve().parent.parent
    names = subprocess.run(['git', 'diff', '--name-only', '--diff-filter=U'], cwd=root,
                           capture_output=True, text=True, check=True).stdout.split()
    bad = 0
    for name in [n for n in names if n.startswith('Locales/') and n.endswith('.json')]:
        path = root / name
        text = resolve(path.read_bytes().decode('utf-8'))
        try:
            json.loads(text)
        except json.JSONDecodeError as error:
            print(f'{name}: still invalid after merge: {error}')
            bad += 1
            continue
        path.write_bytes(text.encode('utf-8'))
        subprocess.run(['git', 'add', name], cwd=root, check=True)
        print(f'{name}: merged')
    return 1 if bad else 0


if __name__ == '__main__':
    sys.exit(main())
