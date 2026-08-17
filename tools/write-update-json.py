#!/usr/bin/env python3
"""
Schreibt update.json fort - die eine Datei, die das Programm beim Start abfragt.

Das Windows DaSi Tool besteht aus einer einzigen EXE. Entsprechend kennt
update.json genau einen Abschnitt: "program". Mehr braucht es nicht.

Abgefragt wird die Datei ueber raw.githubusercontent.com und NICHT ueber die
GitHub-Schnittstelle (api.github.com): Die Schnittstelle laesst ohne Anmeldung
nur 60 Abrufe je Stunde und IP-Adresse zu. In einer Firma sitzen alle Rechner
hinter derselben Adresse - nach 60 Programmstarts waere Schluss.
raw.githubusercontent liefert ueber ein Auslieferungsnetz aus und kennt diese
Grenze nicht.

Aufruf (in der Regel aus dem Workflow "Veroeffentlichen"):

  python tools/write-update-json.py \\
      --repo Tinnitus97/Windows.DaSi.Tool \\
      --tag v1.1.0 \\
      --version 1.1.0 \\
      --sha256 3f8a... \\
      [--datei WindowsDaSiTool.exe] \\
      [--datum 2026-08-17]

Ohne --datum wird das heutige Datum eingetragen. --datei ist nur noetig, wenn
die EXE an der Veroeffentlichung einmal anders heissen sollte.
"""

from __future__ import annotations

import argparse
import json
import re
from datetime import date
from pathlib import Path

ZIEL = Path("update.json")

NUMMER = re.compile(r"^\d+(\.\d+){1,3}$")
PRUEFSUMME = re.compile(r"^[0-9a-f]{64}$")


def main() -> int:
    p = argparse.ArgumentParser(description="update.json fuer das Windows DaSi Tool schreiben")
    p.add_argument("--repo", required=True, help="z.B. Tinnitus97/Windows.DaSi.Tool")
    p.add_argument("--tag", required=True, help="Etikett der Veroeffentlichung, z.B. v1.1.0")
    p.add_argument("--version", required=True, help="Nummer ohne v, z.B. 1.1.0")
    p.add_argument("--sha256", required=True, help="SHA256 der EXE, 64 Zeichen")
    p.add_argument("--datei", default="WindowsDaSiTool.exe", help="Name der EXE an der Veroeffentlichung")
    p.add_argument("--datum", default="", help="Freigabedatum JJJJ-MM-TT, sonst heute")

    args = p.parse_args()

    if not NUMMER.match(args.version):
        p.error(f"--version sieht nicht nach einer Nummer aus: {args.version!r}")

    pruefsumme = args.sha256.strip().lower()
    if not PRUEFSUMME.match(pruefsumme):
        p.error("--sha256 muss aus genau 64 Zeichen 0-9/a-f bestehen")

    if args.datum and not re.match(r"^\d{4}-\d{2}-\d{2}$", args.datum):
        p.error("--datum muss die Form JJJJ-MM-TT haben")

    basis = f"https://github.com/{args.repo}/releases"

    inhalt = {
        "schema": 1,
        "program": {
            "version": args.version,
            "released": args.datum or date.today().isoformat(),
            "url": f"{basis}/download/{args.tag}/{args.datei}",
            "sha256": pruefsumme,
            "notes": f"{basis}/tag/{args.tag}",
        },
    }

    ZIEL.write_text(json.dumps(inhalt, indent=2, ensure_ascii=False) + "\n", encoding="utf-8")
    print(ZIEL.read_text(encoding="utf-8"))
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
