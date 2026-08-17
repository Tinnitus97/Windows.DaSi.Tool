# Veröffentlichen

Ein Stand, ein Etikett, ein `update.json`.

| | |
| --- | --- |
| **Was** | `WindowsDaSiTool.exe` |
| **Nummer** | aus `<Version>` in der `.csproj` |
| **Etikett** | `v1.0.6` |
| **Ergebnis** | `WindowsDaSiTool.exe` + `SHA256SUMS.txt` an der Veröffentlichung |

---

## Ein neues Programm veröffentlichen

1. `<Version>` in der `.csproj` und `VersionString` in
   `ViewModels/MainWindowViewModel.cs` auf dieselbe Nummer setzen
   (und `app.manifest`, wenn du es sauber halten willst).
2. `CHANGELOG.md` ergänzen.
3. Committen und pushen (GitHub Desktop: *Commit* → *Push origin*).
4. Veröffentlichen — **einer** der drei Wege:

**a) Im Browser, ganz ohne Etikett** (am wenigsten Handgriffe)

> Reiter **Actions** → links *Veröffentlichen* → rechts **Run workflow** →
> Nummer: `1.0.6` → **Run workflow**

Das Etikett `v1.0.6` legt der Workflow selbst an.

**b) In GitHub Desktop**

> Reiter **History** → Rechtsklick auf den obersten Commit → **Create Tag…** →
> `v1.0.6` → dann **Push origin**

**c) Auf der Kommandozeile**

```bash
git tag v1.0.6
git push origin v1.0.6
```

Der Workflow prüft in jedem Fall, ob die Nummer zur `.csproj` passt (sonst
bricht er mit einer klaren Meldung ab), baut die EXE, bildet die Prüfsumme,
legt die Veröffentlichung an und schreibt `update.json` fort.

---

## Wie das Programm davon erfährt

Es holt sich beim Start **eine** Datei:

```
https://raw.githubusercontent.com/Tinnitus97/Windows.DaSi.Tool/HEAD/update.json
```

Zwei Besonderheiten:

- **`HEAD` statt eines festen Zweignamens.** `HEAD` folgt immer dem
  Standardzweig — heute `main`. Ein fest eingetragener Name wäre eine stille
  Falle: Würde der Standardzweig später umbenannt, liefe die Abfrage ins Leere,
  und zwar in **jeder bereits verteilten EXE**. Die lässt sich dann nicht mehr
  nachbessern — sie holt ihre Nachbesserung ja über genau diese Adresse. Aus
  demselben Grund benutzt der Workflow
  `${{ github.event.repository.default_branch }}` statt `main`.

- **Nicht über `api.github.com`.** Die Schnittstelle erlaubt ohne Anmeldung nur
  60 Abrufe je Stunde und IP-Adresse; in einer Firma sitzen alle Rechner hinter
  derselben Adresse. `raw.githubusercontent.com` wird über ein Auslieferungsnetz
  bereitgestellt und kennt diese Grenze nicht.

Eine abweichende Adresse lässt sich in `Update-Url.txt` **neben der EXE**
hinterlegen — eine Zeile, vollständige `https://…`-Adresse.

---

## Aufbau des Repositories

```
WindowsDaSiTool.csproj        das Programm
Services/  ViewModels/  Views/
Assets/app.ico

tools/write-update-json.py    schreibt update.json fort
tests/DaSiTests/              Prüfungen ohne Oberfläche
update.json                   was das Programm abfragt
.github/workflows/            Bauen, Prüfen, Veröffentlichen
```

Die `.csproj` liegt im Wurzelverzeichnis und würde von sich aus **jede**
`.cs`-Datei darunter einsammeln — auch die des Testprojekts. Deshalb stehen dort
die Ausschlüsse für `tests/`, `publish/` und `tools/`; ohne sie scheitert der
Build mit `CS0017: Program has more than one entry point defined`.
