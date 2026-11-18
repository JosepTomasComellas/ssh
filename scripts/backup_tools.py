#!/usr/bin/env python3
"""Ajudant de scripts/backup-run.sh: retenció i comprovació d'edat de les còpies xifrades (*.tar.age).

Els noms són YYYYMMDDTHHMMSSZ-<hex>.tar.age, així que l'ordre alfabètic és l'ordre cronològic. Mai no toca
els .partial (còpies fallides, per diagnosticar) ni cap altre fitxer.
"""
from __future__ import annotations

import argparse
import re
import sys
import time
from calendar import timegm
from pathlib import Path

NAME = re.compile(r"^(\d{8}T\d{6}Z)-[0-9a-f]{8}\.tar\.age$")


def backups(directory: Path) -> list[Path]:
    """Còpies vàlides, de la més antiga a la més nova."""
    return sorted(p for p in directory.iterdir() if p.is_file() and NAME.match(p.name))


def prune(directory: Path, keep: int) -> list[Path]:
    """Esborra les còpies més antigues i en conserva les `keep` més noves. Retorna les esborrades."""
    if keep < 1:
        raise ValueError("keep ha de ser >= 1 (mai s'esborren totes les còpies)")
    found = backups(directory)
    doomed = found[:-keep] if len(found) > keep else []
    for path in doomed:
        path.unlink()
    return doomed


def age_hours(path: Path, now: float | None = None) -> float:
    """Edat segons el nom del fitxer (UTC), no el mtime, que canvia en copiar o restaurar."""
    m = NAME.match(path.name)
    assert m, path.name
    taken = timegm(time.strptime(m.group(1), "%Y%m%dT%H%M%SZ"))
    return ((time.time() if now is None else now) - taken) / 3600


def check(directory: Path, max_hours: float, now: float | None = None) -> str:
    """Retorna un missatge d'estat; llança RuntimeError si no hi ha còpia o la més nova és massa antiga."""
    found = backups(directory)
    if not found:
        raise RuntimeError(f"No hi ha cap còpia a {directory}")
    hours = age_hours(found[-1], now)
    if hours > max_hours:
        raise RuntimeError(f"L'última còpia ({found[-1].name}) té {hours:.1f} h; el màxim és {max_hours:g} h")
    return f"OK: l'última còpia ({found[-1].name}) té {hours:.1f} h; {len(found)} còpies"


def main() -> int:
    p = argparse.ArgumentParser(description=__doc__)
    sub = p.add_subparsers(dest="cmd", required=True)
    a = sub.add_parser("prune"); a.add_argument("--dir", required=True); a.add_argument("--keep", type=int, required=True)
    c = sub.add_parser("check"); c.add_argument("--dir", required=True); c.add_argument("--max-hours", type=float, default=36)
    args = p.parse_args()
    try:
        if args.cmd == "prune":
            for gone in prune(Path(args.dir), args.keep):
                print("Esborrada: " + gone.name)
        else:
            print(check(Path(args.dir), args.max_hours))
    except (RuntimeError, ValueError, OSError) as exc:
        print(f"ERROR: {exc}", file=sys.stderr)
        return 1
    return 0


if __name__ == "__main__":
    sys.exit(main())
