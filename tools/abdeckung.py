#!/usr/bin/env python3
"""Wertet die Abdeckungsberichte der Pruefungen aus (Cobertura, von coverlet).

Aufruf:  python tools/abdeckung.py bericht1.xml [bericht2.xml ...] [--pflicht]

Zaehlt jede Funktion des Programms - Methoden, Konstruktoren und
Eigenschaften mit eigenem Code - und meldet die, die keine Pruefung je
ausgefuehrt hat. Lambdas, lokale Funktionen und async-Zustandsmaschinen
gehoeren zu der Methode, in der sie stehen.

Beide Pruefprojekte enthalten dieselben Dienste (tests/DaSiTests bindet die
Quelldateien ein, tests/DaSiGuiTests nutzt das Hauptprojekt). Die Berichte
werden deshalb nach Datei und Funktion zusammengefuehrt: Was eine der beiden
Pruefungen ausfuehrt, gilt als geprueft.

--pflicht: Endet mit Fehler, wenn eine Funktion ohne Pruefung bleibt, die
nicht in tests/abdeckung-ausnahmen.txt steht (jeweils mit Begruendung) -
oder wenn dort eine Ausnahme steht, die gar nicht mehr gebraucht wird.
"""
from __future__ import annotations

import os
import re
import sys
import xml.etree.ElementTree as ET
from collections import defaultdict

WURZEL = os.path.normpath(os.path.join(os.path.dirname(os.path.abspath(__file__)), ".."))
AUSNAHMEN = os.path.join(WURZEL, "tests", "abdeckung-ausnahmen.txt")

# Nur der Programmcode - nicht die Pruefungen selbst, nicht vom Compiler
# erzeugte Dateien (obj/, *.g.cs).
ORDNER = ("Services/", "ViewModels/", "Views/")
EINZELN = ("App.axaml.cs", "Program.cs")


def relativ(pfad: str, quellen: list[str]) -> str | None:
    pfad = pfad.replace("\\", "/")
    for q in quellen:
        q = q.replace("\\", "/").rstrip("/") + "/"
        if pfad.startswith(q):
            pfad = pfad[len(q):]
            break
    # Absolute Pfade ohne <source>: ab dem Repository-Ordner schneiden.
    for teil in ORDNER:
        i = pfad.find("/" + teil)
        if i >= 0:
            pfad = pfad[i + 1:]
    if "/obj/" in "/" + pfad or pfad.endswith(".g.cs") or pfad.startswith("tests/"):
        return None
    if pfad.startswith(ORDNER) or pfad in EINZELN:
        return pfad
    return None


GENERIERT = re.compile(r"^<([^>]+)>")


def funktion(klasse: str, methode: str) -> tuple[str, str] | None:
    """(Klasse, Funktion) im Quelltext - oder None fuer reinen Compilercode."""
    teile = klasse.split("/")
    aussen = [t for t in teile if not t.startswith("<")]
    innen = [t for t in teile if t.startswith("<")]
    k = "/".join(aussen)
    # async-Methode: Klasse/<Name>d__7 :: MoveNext
    for t in innen:
        m = GENERIERT.match(t)
        if m and m.group(1) and not m.group(1).startswith(">"):
            name = m.group(1)
            if name.startswith("<"):           # <<Name>g__Lokal|..>d
                name = GENERIERT.match(name + ">").group(1) if GENERIERT.match(name + ">") else name
            return (k, name.lstrip("<"))
    # Lambda/lokale Funktion: <Name>b__3_0, <Name>g__Lokal|3_0
    m = GENERIERT.match(methode)
    if m:
        return (k, m.group(1)) if m.group(1) else None
    if methode in (".cctor",):
        return (k, "(statische Felder)")
    return (k, methode)


def lesen(dateien: list[str]):
    # (Datei, Klasse, Funktion) -> {Zeile: Treffer}
    stand: dict[tuple[str, str, str], dict[int, int]] = defaultdict(dict)
    for datei in dateien:
        if not os.path.exists(datei):
            print(f"FEHLT: {datei} - diese Pruefung hat keinen Bericht geschrieben.")
            continue
        baum = ET.parse(datei)
        quellen = [s.text or "" for s in baum.iter("source")]
        for klasse in baum.iter("class"):
            rel = relativ(klasse.get("filename", ""), quellen)
            if rel is None:
                continue
            for m in klasse.iter("method"):
                f = funktion(klasse.get("name", ""), m.get("name", ""))
                if f is None:
                    continue
                zeilen = stand[(rel, f[0], f[1])]
                for z in m.iter("line"):
                    nr = int(z.get("number"))
                    zeilen[nr] = max(zeilen.get(nr, 0), int(z.get("hits", "0")))
    return stand


def kurz(klasse: str) -> str:
    return klasse.split(".")[-1].replace("/", ".")


def main() -> int:
    args = [a for a in sys.argv[1:] if not a.startswith("--")]
    pflicht = "--pflicht" in sys.argv
    if not args:
        print(__doc__)
        return 2
    stand = lesen(args)
    fehlende = [a for a in args if not os.path.exists(a)]

    # Gleiche Funktion aus mehreren Klassenteilen (Lambda + Methode) zusammen.
    funktionen: dict[tuple[str, str], dict[int, int]] = defaultdict(dict)
    for (rel, klasse, name), zeilen in stand.items():
        ziel = funktionen[(rel, kurz(klasse) + "." + name)]
        for nr, h in zeilen.items():
            ziel[nr] = max(ziel.get(nr, 0), h)

    je_datei: dict[str, list[tuple[str, bool]]] = defaultdict(list)
    for (rel, name), zeilen in sorted(funktionen.items()):
        if not zeilen:
            continue
        je_datei[rel].append((name, any(h > 0 for h in zeilen.values())))

    zeilen_ges = zeilen_hit = 0
    zeilen_datei: dict[str, tuple[int, int]] = {}
    alle: dict[str, dict[int, int]] = defaultdict(dict)
    for (rel, _), zeilen in funktionen.items():
        for nr, h in zeilen.items():
            alle[rel][nr] = max(alle[rel].get(nr, 0), h)
    for rel, z in alle.items():
        g, h = len(z), sum(1 for v in z.values() if v > 0)
        zeilen_datei[rel] = (g, h)
        zeilen_ges += g
        zeilen_hit += h

    offen: list[str] = []
    print(f"{'Datei':48} {'Funktionen':>12} {'Zeilen':>8}")
    for rel in sorted(je_datei):
        fs = je_datei[rel]
        ok = sum(1 for _, d in fs if d)
        g, h = zeilen_datei[rel]
        print(f"{rel:48} {ok:>5} / {len(fs):<5} {100 * h / max(g, 1):>6.1f} %")
        offen += [f"{rel}: {n}" for n, d in fs if not d]
    anzahl = sum(len(v) for v in je_datei.values())
    print(f"{'Gesamt':48} {anzahl - len(offen):>5} / {anzahl:<5} {100 * zeilen_hit / max(zeilen_ges, 1):>6.1f} %")

    ausnahmen: dict[str, str] = {}
    if os.path.exists(AUSNAHMEN):
        for zeile in open(AUSNAHMEN, encoding="utf-8"):
            zeile = zeile.strip()
            if not zeile or zeile.startswith("#"):
                continue
            eintrag, _, grund = zeile.partition("|")
            ausnahmen[eintrag.strip()] = grund.strip()

    fehlt = [o for o in offen if o not in ausnahmen]
    ueberfluessig = [a for a in ausnahmen if a not in offen]
    begruendet = [o for o in offen if o in ausnahmen]

    if begruendet:
        print(f"\nOhne Pruefung, begruendet ({len(begruendet)}, siehe tests/abdeckung-ausnahmen.txt):")
        for o in begruendet:
            print(f"  {o}  -- {ausnahmen[o]}")
    if fehlt:
        print(f"\nFunktionen OHNE Pruefung ({len(fehlt)}):")
        for o in fehlt:
            print(f"  {o}")
    if ueberfluessig:
        print(f"\nAusnahmen, die nicht mehr gebraucht werden ({len(ueberfluessig)}):")
        for a in ueberfluessig:
            print(f"  {a}")
    if not fehlt and not ueberfluessig:
        print("\nJede Funktion wird von mindestens einer Pruefung ausgefuehrt.")

    if pflicht and (fehlt or ueberfluessig or fehlende):
        return 1
    return 0


if __name__ == "__main__":
    sys.exit(main())
