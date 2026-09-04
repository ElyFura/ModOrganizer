# FFXIV Mod Organizer — Plan

## Context

Der User (Moonflow-Media) hat eine umfangreiche FFXIV-Mod-Bibliothek unter
`E:\FFXIV\FFXIV\FF14 Mods\Mods\Dawntrail` (14 Kategorien, >290 Mod-Ordner).
Die Verwaltung erfolgt aktuell rein im Explorer und ist unübersichtlich:

- Mod-Ordner und darin liegende `.pmp` / `.ttmp2`-Dateien haben oft inkonsistente Namen.
- Vorschaubilder fehlen in vielen Ordnern, manche haben mehrere, einige kryptische
  Namen (`mod_119906_c0536743-….jpg` → Downloads vom XIV Mod Archive).
- Es gibt keine zentrale Übersicht mit Thumbnail-Galerie.
- Duplikate (z. B. alte vs. neue Versionen) sind schwer zu erkennen.
- Tags/Notizen/Download-Links liegen nirgends strukturiert.
- Kategorien (= Unterordner direkt unter `Dawntrail\`, z. B. Gear, Hair, Accessory,
  Face, Body-Scales-Skin, VFX, Housing, Shoes, Ears-Horns-Tail, Miscellaneous,
  Animation-SFX, Bastet, Chi Gear, „für Textool") sind organisatorisch zentral,
  aber ohne Tool-Unterstützung nur über den Explorer verwaltbar.

**Ziel:** Eine **native Windows-Desktop-Anwendung** (C# / .NET 8 / WPF, single-file
.exe, keine externe Runtime), die
1. alle Mods als Galerie (Grid + Ordner-Ansicht) mit Thumbnails anzeigt,
2. **Kategorien als erstklassige Entität** verwaltet:
   Hinzufügen (= neuer Unterordner), Umbenennen, Zusammenführen, Löschen,
3. Verwaltungs-Aktionen für Mods: **Umbenennen, Verschieben (Kategorie/Root), Löschen**,
4. **Tagging** mit Multi-Tag-Filter anbietet,
5. pro Mod **Metadata-Links** (XIV Mod Archive, Nexus, Patreon, Twitter, Ko-Fi, etc.) speichert,
6. pro Mod **Kommentare / Anleitungen** in Markdown ablegt,
7. einen Health-Check für Screenshots bietet,
8. Duplikate findet, und
9. beliebig viele Mod-Roots scannen kann (Dawntrail, Endwalker, Penumbra-Active etc.).

Working Directory ist `F:\ModOrganizer` (aktuell leer — fresh project).

---

## Architektur

### Tech-Stack

- **UI:** .NET 8 + **WPF** (reif, offline, MVVM-freundlich)
- **MVVM:** CommunityToolkit.Mvvm (`[ObservableProperty]`, `[RelayCommand]`)
- **DI / Hosting:** Microsoft.Extensions.Hosting (Generic Host in WPF)
- **DB:** SQLite via `Microsoft.Data.Sqlite` + Dapper
- **Markdown-Render:** `Markdig.Wpf` für die Anleitungs-Ansicht
- **Logging:** Serilog → File + Debug-Sink
- **Bild-Handling:** `BitmapImage` mit `DecodePixelWidth`;
  PNG-Thumbnail-Cache unter `%LOCALAPPDATA%\FFXIVModOrganizer\thumbs\`
- **Hashing (Duplikat-Erkennung):** `System.IO.Hashing.XxHash64`
- **Papierkorb-Delete:** `Microsoft.VisualBasic.FileIO.FileSystem.DeleteDirectory(…, SendToRecycleBin, …)`
- **Packaging:** `dotnet publish -c Release -r win-x64 --self-contained` → single-file .exe

### Projektstruktur

```
F:\ModOrganizer\
├── ModOrganizer.sln
├── src\
│   ├── ModOrganizer.App\          (WPF, UI, ViewModels, Views)
│   │   ├── App.xaml / App.xaml.cs
│   │   ├── Views\
│   │   │   ├── MainWindow.xaml           (Shell: Sidebar + Content)
│   │   │   ├── GalleryView.xaml          (Grid mit Thumb+Name, virtualisiert)
│   │   │   ├── FolderView.xaml           (Ordner-Ansicht, Mod-Cover wie Ordner-Icon)
│   │   │   ├── ModDetailView.xaml        (Großbild, Tags, Links, Kommentar, Dateien)
│   │   │   ├── CategoryManagerView.xaml  (Kategorien anlegen/umbenennen/mergen/löschen)
│   │   │   ├── TagManagerView.xaml       (Tags erstellen/umbenennen/löschen, Farben)
│   │   │   ├── HealthView.xaml           (Screenshot-Probleme)
│   │   │   ├── DuplicatesView.xaml
│   │   │   └── SettingsView.xaml         (Roots verwalten)
│   │   ├── Dialogs\
│   │   │   ├── RenameDialog.xaml
│   │   │   ├── MoveDialog.xaml           (Kategorie/Root wählen)
│   │   │   ├── DeleteConfirmDialog.xaml
│   │   │   ├── CategoryMergeDialog.xaml  (Quelle → Ziel, Preview)
│   │   │   └── LinkEditorDialog.xaml
│   │   ├── ViewModels\
│   │   └── Converters\
│   ├── ModOrganizer.Core\         (keine WPF-Refs)
│   │   ├── Models\ (Mod, ModFile, Category, Root, Tag, ModLink, HealthIssue)
│   │   ├── Scanning\ (ModScanner, FileClassifier)
│   │   ├── Categories\ (CategoryService — CRUD + Merge + Rename)
│   │   ├── Management\ (RenameService, MoveService, DeleteService, UndoLog)
│   │   ├── Tagging\ (TagService, TagSuggester)
│   │   ├── Links\ (LinkService, UrlNormalizer, DomainIconResolver)
│   │   ├── Comments\ (CommentService — Markdown-Roundtrip)
│   │   ├── Health\ (HealthChecker)
│   │   ├── Duplicates\ (DuplicateFinder)
│   │   └── Storage\ (SqliteStore, Migrations, ConfigStore)
│   └── ModOrganizer.Core.Tests\   (xUnit + FluentAssertions + temp-folder fixtures)
└── README.md
```

### Datenmodell (SQLite)

```sql
roots(id, path, display_name, enabled, added_at)
categories(id, root_id, name, sort_order, icon_name, color_hex, description)
                                           -- sort_order = manuelle Reihenfolge in Sidebar
mods(id, category_id, folder_name, display_name,
     comment_md,                           -- Anleitung/Notizen in Markdown
     created_at, updated_at)
mod_files(id, mod_id, relative_path, kind, size_bytes, xxhash64, mtime)
                                           -- kind: pmp|ttmp2|image|doc|other
thumbnails(mod_file_id, cache_path, width, height, generated_at)

tags(id, name, color_hex, description)     -- z. B. "Bibo+", "NSFW", "Favorit"
mod_tags(mod_id, tag_id, added_at)         -- m:n, PRIMARY KEY(mod_id, tag_id)

mod_links(id, mod_id, url, title, domain, kind, added_at)
                                           -- kind: source|patreon|kofi|twitter|discord|other
                                           -- domain wird beim Insert extrahiert

health_issues(id, mod_id, kind, severity, detail, resolved_at)
action_log(id, ts, action, mod_id, from_path, to_path, tx_id, payload_json)
                                           -- action: rename|move|delete|tag|untag|link_add|…
```

DB unter `%LOCALAPPDATA%\FFXIVModOrganizer\store.db`. Dateien bleiben
Source-of-Truth für Struktur/Inhalte. Tags, Links, Kommentare leben **nur** in der DB
(portabel via DB-Backup/Export).

---

## Core-Features

### 1. Scan-Engine (`ModScanner`)

Pro Root:
- Kategorie = Unterordner Level 1, Mod = Unterordner Level 2.
- Klassifizierung bis Level 4: `.pmp`/`.ttmp2`→Mod-Daten, Bilder, Docs, Rest.
- Inkrementell (mtime+size → kein Rehash), Background-Task mit Progress.
- Bei Scan: verschwundene Mods bleiben als "missing" markiert, nicht gelöscht —
  Tags/Kommentare wären sonst weg.

Referenz aus Live-Scan: 14 Kategorien, 293 pmp, 55 ttmp2, 177 jpg, 90 png, 24 webp.

### 2. Galerie — zwei Modi

**Gallery-Grid (`GalleryView`):**
- `VirtualizingStackPanel` + Wrap-Layout (`VirtualizingPanel.IsVirtualizing=True`).
- Kachel: 256px Thumb, Mod-Name, kleine Tag-Chips (farbig), Badges
  (Anzahl `.pmp`, Health-Icon, Link-Icon wenn Links vorhanden).
- Sidebar: Root→Kategorie-Tree + Suchfeld + Tag-Filter (Multi-Select, UND/ODER-Toggle)
  + Status-Filter (`kein Bild`, `mehrere Bilder`, `Duplikat`, `ohne Tags`).

**Folder-View (`FolderView`):** — wie Windows-Explorer, Mod-Ordner als Ordner-Icons
- Große Ordner-Kachel-Grafik mit dem **Mod-Cover als Einlage** (das primäre Bild
  wird in eine stilisierte Ordner-Silhouette gerendert, sodass man auf den ersten
  Blick Ordnerstruktur + Cover sieht).
- Doppelklick navigiert rein (Kategorie → Mod → Dateien), Breadcrumb oben.
- Auf Mod-Ebene sieht man die enthaltenen Dateien (pmp/ttmp2/Bilder) mit Icons.
- Über Toggle-Button in der Toolbar wechselbar: **Grid | Folder**.

Thumbs werden lazy generiert (Decode @ 256px, seit `DecodePixelWidth`) und als
PNG gecached (Key = xxhash64 der Originaldatei).

### 3. Detail-View (`ModDetailView`)

Tab-Layout pro Mod:

- **Übersicht:** Großes Preview + Bild-Karussell, Meta (Kategorie, Root, mtime).
- **Anleitung / Kommentar:** Markdown-Editor (links) + Live-Preview (rechts, `Markdig.Wpf`).
  Gespeichert in `mods.comment_md`. Strg+S speichert; Auto-Save nach 3 s Idle.
- **Tags:** Chip-Editor — Typeahead über existierende Tags, Enter legt neue an.
  Farben werden im `TagManagerView` verwaltet.
- **Links:** Tabelle (Icon|Title|URL|Kind) + Buttons Hinzufügen/Bearbeiten/Öffnen.
  Beim Einfügen eines URL wird Domain+Kind automatisch erkannt
  (`xivmodarchive.com` → `source`, `patreon.com` → `patreon`, `ko-fi.com` → `kofi`,
  `twitter.com`/`x.com` → `twitter`). Titel wird (optional, einmalig) via
  `HttpClient` + HTML-`<title>`-Regex geholt — nur auf explizites Klicken, nicht
  automatisch (keine Hintergrund-Netzrequests).
- **Dateien:** Liste mit Hash, Größe, Typ — Rechtsklick → "Im Explorer anzeigen".
- **Aktionen** (Toolbar): Umbenennen · Verschieben · Löschen · Im Explorer öffnen.

### 4. Verwaltung — Umbenennen, Verschieben, Löschen

Alle drei gehen über einen gemeinsamen `ActionPlan` → Preview-Dialog → Execute
→ `action_log`-Eintrag mit `tx_id`. Undo wirkt auf jede Transaktion rückwärts.

**Umbenennen (`RenameService`):**
1. Plan erzeugen (dry-run):
   - Ordner: `<cat>\<oldFolder>` → `<cat>\<newFolder>`.
   - Pro Mod-Datei im Ordner: wenn Stem ≈ `oldFolder` (Case-insensitive, Levenshtein ≤ 2),
     Rename zu `<newFolder><suffix><ext>`. Multi-Mod-Ordner wie „AVALON REDUX" (7 PMPs)
     → nur die, deren Stem wirklich dem Ordnernamen entspricht, werden angefasst.
   - Bilder mit Pattern `mod_\d+_[a-f0-9\-]+\.(jpg|png)` → Rename zu `<newFolder>.<ext>`.
2. Preview-Dialog mit Checkboxen pro Zeile, Konflikt-Highlight (Ziel existiert).
3. Execute innerhalb Transaktion: Dateien zuerst, dann Ordner; bei Fehler Rollback.
4. Undo liest Transaktion rückwärts aus `action_log`.

**Verschieben (`MoveService`):**
- Dialog: Ziel-Kategorie (aus Root-Tree wählen) oder Ziel-Root + Kategorie.
- Prüft Kollision (Mod mit selbem Ordner-Namen existiert im Ziel).
- `Directory.Move` — atomar auf demselben Volume; bei Cross-Volume
  (z. B. E: → D:) Copy+Delete, mit Progress und Abbruch-Möglichkeit.
- DB-Update: `mods.category_id` auf neue Kategorie. Tags/Links/Kommentare bleiben
  an der Mod-ID hängen — nach Move also unverändert.
- Batch-Fähig: Multi-Select in Galerie → "Verschieben" → ein Ziel für alle.

**Löschen (`DeleteService`):**
- Nur in den **Windows-Papierkorb**, nicht permanent
  (`FileSystem.DeleteDirectory(path, UIOption.OnlyErrorDialogs, RecycleOption.SendToRecycleBin)`).
- Dialog zeigt: Mod-Name, Ordnerpfad, Dateianzahl, Gesamtgröße + Bestätigung.
- DB: Mod wird als `deleted_at` markiert (nicht hart gelöscht) — so bleiben
  Tags/Kommentare/Links für potenzielles Wiederherstellen aus dem Papierkorb
  erhalten. Beim nächsten Scan: Ordner wieder vorhanden → Mod reaktivieren;
  dauerhaft weg nach z. B. 30 Tagen → hart löschen (konfigurierbar).
- Batch-Fähig (Multi-Select).

### 5. Kategorie-Verwaltung (`CategoryService`)

Kategorien = Unterordner direkt unter einem Root (`<root>\<category>\<mod>\…`).
Die DB spiegelt sie, aber die Dateisystem-Ordner bleiben Source-of-Truth —
jede Kategorie-Aktion fasst Ordner auf Platte + DB-Zeile synchron an.

**UI (`CategoryManagerView` + Sidebar-Kontextmenü):**
- Liste aller Kategorien pro Root: Name · Mod-Anzahl · Icon · Farbe · Reihenfolge.
- Drag-Reorder für die Sidebar (`sort_order`).
- Inline-Umbenennen, Rechtsklick → Zusammenführen / Löschen / Icon+Farbe ändern.

**Aktionen:**

- **Hinzufügen:** Dialog mit Name + optional Icon/Farbe → erzeugt `<root>\<name>\`
  auf Platte + Zeile in `categories`. Namens-Sanitizing (keine
  Windows-ungültigen Zeichen; kein Konflikt mit existierender Kategorie im selben Root).
- **Umbenennen:** Ordner umbenennen (`Directory.Move`) + DB-Update. Läuft über
  denselben Transaktions-Mechanismus wie Mod-Rename (`action_log`, Undo).
- **Zusammenführen (Merge):** Dialog wählt Quelle + Ziel. Alle Mods der Quelle
  werden via `MoveService` (derselbe Volume = atomare `Directory.Move` pro Mod)
  in den Ziel-Ordner verschoben, anschließend Quell-Kategorie-Ordner gelöscht
  (nur wenn leer). Kollisionen (Mod mit gleichem Ordner-Namen in Quelle + Ziel)
  werden vorher im Preview als Konflikt gelistet — pro Kollision wählt der User
  "Überspringen" oder "Ziel-Mod mit Suffix umbenennen".
- **Löschen:**
  - Leere Kategorie → direktes Entfernen (Ordner + DB-Zeile).
  - Nicht-leere Kategorie → Dialog bietet zwei Wege:
    (a) erst alle Mods verschieben (öffnet Move-Dialog mit Batch-Ziel),
    (b) gesamten Kategorie-Ordner in den Windows-Papierkorb (über `DeleteService`).
- **Icon/Farbe:** frei wählbar (Segoe-Fluent-Icons + Hex-Farbe); rein kosmetisch
  in der Sidebar/Galerie, nichts am Dateisystem.

**Initial-Seed beim ersten Scan:**
Beim ersten Scan eines Roots werden alle Level-1-Unterordner automatisch als
Kategorien eingetragen (Reihenfolge = alphabetisch, Icon = generisches Ordner-Icon).
Der User kann danach kuratieren (Reihenfolge, Icons, Merges).

**Robustheit:** Wenn beim nächsten Scan eine Kategorie-Zeile in der DB existiert,
der Ordner aber nicht mehr → Kategorie wird als "missing" markiert (nicht gelöscht,
damit manuelle Explorer-Änderungen nicht stumm Metadaten fressen). UI zeigt einen
gelben Hinweis mit Optionen "Kategorie-Ordner wiederherstellen" oder
"Kategorie endgültig löschen".

### 6. Tagging (`TagService`)

- Tags sind global (nicht pro Root). Farbe + optionale Beschreibung pro Tag.
- `TagManagerView`: Liste aller Tags mit Benutzungs-Anzahl. Umbenennen/Löschen/Farbe
  ändern (Löschen entfernt nur die Verknüpfung, Mods bleiben).
- **Bulk-Tagging**: Multi-Select in Galerie → Kontextmenü → "Tag hinzufügen" /
  "Tag entfernen".
- **Smart-Tag-Vorschläge** (deterministisch, kein ML):
  - Wenn Mod-Name "Bibo+", "TBSE", "Rue", "TreYab", "Lavabod" enthält → Body-Tag vorschlagen.
  - Wenn Kategorie `Hair` → Tag "Hair" vorschlagen (nur Vorschlag, nicht Auto).
  - User akzeptiert per Klick.
- **Filter-Logik:** Chip-Leiste oben, Toggle "UND / ODER", negierte Tags per
  `-tagname`. Filter werden in der URL/Session persistiert.

### 7. Metadata-Links (`LinkService`)

- Pro Mod beliebig viele Links (URL + Titel + Kind).
- `UrlNormalizer` trimmt Tracking-Parameter (`utm_*`, `fbclid`, …).
- `DomainIconResolver` mappt bekannte Domains auf Inline-SVG-Icons
  (Mod Archive, Nexus, Patreon, Ko-Fi, Twitter/X, Discord, GitHub, Gumroad).
- Klick auf Link → `Process.Start({ FileName: url, UseShellExecute: true })`.
- Import aus `mod_XXXXX_UUID.jpg`-Dateiname: Beim Scan wird daraus optional
  `https://www.xivmodarchive.com/modid/XXXXX` als Source-Link-Vorschlag erzeugt
  (nur Vorschlag im Detail-Tab; nichts wird automatisch gespeichert).

### 8. Kommentare / Anleitungen (`CommentService`)

- `mods.comment_md` speichert Markdown pro Mod.
- Editor-Tab im Detail-View mit Live-Preview (`Markdig.Wpf`).
- Unterstützt Bilder per Drag-Drop: Bild wird in `%LOCALAPPDATA%\…\comment_assets\<mod_id>\`
  kopiert und im Markdown als relativer Pfad referenziert.
- Gut für "So installiert man" / "Kompatibel mit" / "TODO: Variante XYZ testen".
- Suche in Galerie findet Volltext-Matches im Kommentar (SQLite FTS5-Index,
  optional — erst bei >200 Mods sinnvoll; haben wir).

### 9. Screenshot-Health-Check (`HealthChecker`)

- **no_image**: kein Bild auf Mod-Top-Level.
- **multi_image**: >1 Bild (nur info).
- **broken_image**: Bild kann nicht decodiert werden (error).
- **orphan_image**: Bild ohne zugehörige `.pmp`/`.ttmp2`.
- `HealthView` listet alle Issues, Filter nach Severity/Kategorie, Jump-to-Mod.
- Kein Auto-Fix — User entscheidet pro Mod.

### 10. Duplikat-Erkennung (`DuplicateFinder`)

- **Name-Ähnlichkeit:** Normalisierung (lower, Version-Tags/Body-Suffixe entfernen),
  Gruppen mit ≥2 Mods.
- **Hash-Gleichheit:** Gruppen gleicher xxhash64 über `.pmp`/`.ttmp2`.
- `DuplicatesView` zeigt beide Listen; Gruppe aufklappen → Cards nebeneinander,
  Buttons "Verschieben in Archiv-Kategorie" / "Zu Papierkorb".

### 11. Multi-Root-Verwaltung (`SettingsView`)

- Liste von Roots (Pfad, Name, Enabled).
- Hinzufügen via `FolderBrowserDialog`, Scan pro Root oder alle.
- Initial vorbefüllt: `E:\FFXIV\FFXIV\FF14 Mods\Mods\Dawntrail` als „Dawntrail".

---

## Kritische Dateien

Alles neu — noch keine Files im Repo.

| Datei | Zweck |
|---|---|
| `F:\ModOrganizer\ModOrganizer.sln` | Solution |
| `src\ModOrganizer.App\App.xaml(.cs)` | Host-Bootstrap, DI |
| `src\ModOrganizer.App\Views\MainWindow.xaml` | Shell-Layout + View-Switcher |
| `src\ModOrganizer.App\Views\GalleryView.xaml` | Virtualisiertes Thumb-Grid |
| `src\ModOrganizer.App\Views\FolderView.xaml` | Ordner-Ansicht mit Cover-als-Icon |
| `src\ModOrganizer.App\Views\ModDetailView.xaml` | Tabs: Übersicht/Anleitung/Tags/Links/Dateien |
| `src\ModOrganizer.App\Views\CategoryManagerView.xaml` | Kategorien CRUD + Reorder + Merge |
| `src\ModOrganizer.App\Views\TagManagerView.xaml` | Tags verwalten |
| `src\ModOrganizer.App\Dialogs\RenameDialog.xaml` | Rename-Preview |
| `src\ModOrganizer.App\Dialogs\MoveDialog.xaml` | Kategorie/Root wählen |
| `src\ModOrganizer.App\Dialogs\CategoryMergeDialog.xaml` | Merge-Preview mit Konflikten |
| `src\ModOrganizer.Core\Scanning\ModScanner.cs` | Filesystem-Scan |
| `src\ModOrganizer.Core\Categories\CategoryService.cs` | Add/Rename/Merge/Delete Kategorien |
| `src\ModOrganizer.Core\Management\RenameService.cs` | Rename-Plan + Transaktion |
| `src\ModOrganizer.Core\Management\MoveService.cs` | Move-Plan (same/cross-volume) |
| `src\ModOrganizer.Core\Management\DeleteService.cs` | Recycle-Bin-Delete + Soft-Delete in DB |
| `src\ModOrganizer.Core\Tagging\TagService.cs` | Tag-CRUD + Bulk-Operationen |
| `src\ModOrganizer.Core\Links\LinkService.cs` | URL-Normalize, Domain-Mapping |
| `src\ModOrganizer.Core\Comments\CommentService.cs` | Markdown-CRUD + Asset-Handling |
| `src\ModOrganizer.Core\Health\HealthChecker.cs` | Screenshot-Regeln |
| `src\ModOrganizer.Core\Duplicates\DuplicateFinder.cs` | Name+Hash-Duplikate |
| `src\ModOrganizer.Core\Storage\SqliteStore.cs` | DB-Migrations + Queries |

---

## Entwicklungs-Reihenfolge (MVP → Polish)

1. Skelett: `dotnet new wpf` + `dotnet new classlib` + Solution, CommunityToolkit.Mvvm, SQLite, Markdig.Wpf.
2. Core-Datenmodell + SQLite-Migration v1 (alle Tabellen inkl. tags, links, comment).
3. `ModScanner` + CLI-Test-Harness, Scan gegen echten Pfad.
4. `MainWindow` + `GalleryView` mit statischen Thumbs — visueller Durchstich.
5. Thumbnail-Cache.
6. `ModDetailView` mit Tabs: Übersicht, Anleitung (Markdown), Tags, Links, Dateien.
7. `RenameService` (Mods) + Dialog + Undo. **Unit-Tests gegen temp-Ordner**.
8. `CategoryService` (Add/Rename/Delete) + `CategoryManagerView` + Sidebar-Binding.
9. `TagService` + `TagManagerView` + Filter-Integration in Galerie.
10. `LinkService` + Link-Editor-Dialog.
11. `CommentService` inkl. Drag-Drop-Assets + FTS5-Suche.
12. `MoveService` (Mods) + Dialog + Batch-Select.
13. `CategoryService.Merge` + `CategoryMergeDialog` (setzt auf MoveService auf).
14. `DeleteService` (Recycle-Bin) + Soft-Delete + Reaktivierung.
15. `FolderView` (Ordner-Ansicht mit Cover-Icon) + Toggle Grid/Folder.
16. `HealthChecker` + `HealthView`.
17. `DuplicateFinder` + `DuplicatesView`.
18. `SettingsView` + Multi-Root-Support.
19. Publish self-contained single-file .exe (`dotnet publish -c Release -r win-x64 --self-contained`).

---

## Verifikation

### Unit-Tests (xUnit + temp folders)

- `ModScanner_FindsAllCategoriesAndMods`.
- `RenameService_RenamesFolderAndMatchingFiles` (Multi-PMP-Ordner).
- `RenameService_RollsBackOnError`, `RenameService_UndoReverts`.
- `CategoryService_Add_CreatesFolderAndDbRow`.
- `CategoryService_Rename_MovesFolderAndUpdatesDb`.
- `CategoryService_Merge_MovesAllModsAndRemovesSource`.
- `CategoryService_Merge_ReportsCollisionsBeforeExecute`.
- `CategoryService_Delete_EmptyCategory_HardDeletes`.
- `CategoryService_Delete_NonEmpty_SendsToRecycleBin`.
- `ModScanner_SeedsCategoriesOnFirstScan`.
- `ModScanner_MissingCategoryFolder_MarksAsMissingNotDeleted`.
- `MoveService_MovesWithinSameVolume_IsAtomic`.
- `MoveService_MovesCrossVolume_CopiesAndDeletes`.
- `MoveService_PreservesTagsAndLinks`.
- `DeleteService_SendsFolderToRecycleBin_AndSoftDeletesInDb`.
- `TagService_BulkAddRemove`, `TagService_RenamingTag_KeepsAssociations`.
- `LinkService_NormalizesUrl_StripsUtmParams`.
- `LinkService_DetectsDomainKind` (xivmodarchive, patreon, kofi, twitter, …).
- `CommentService_RoundtripMarkdown`, `CommentService_StoresDragDropAsset`.
- `HealthChecker_DetectsMissingMultipleBrokenOrphan`.
- `DuplicateFinder_GroupsByHashAndName`.

### Manuelle Verifikation gegen `E:\FFXIV\…\Dawntrail`

1. **Scan:** App starten, 14 Kategorien, ~290 Mods erkannt, Health-Liste füllt sich.
   Alle 14 Kategorien (Gear, Hair, Accessory, Face, Body-Scales-Skin, VFX,
   Housing, Shoes, Ears-Horns-Tail, Miscellaneous, Animation-SFX, Bastet,
   Chi Gear, „für Textool") landen als Zeilen in `categories`.
2. **Kategorie hinzufügen:** Neue Kategorie „_Archiv" anlegen → Ordner
   `E:\FFXIV\…\Dawntrail\_Archiv` existiert, erscheint in der Sidebar.
3. **Kategorie umbenennen:** „Chi Gear" → „Chinese Gear". Ordner umbenannt,
   Mods bleiben funktional erreichbar, Undo funktioniert.
4. **Kategorie zusammenführen:** „Ears-Horns-Tail" → „Accessory" mergen.
   Preview listet potenzielle Namens-Kollisionen; nach Execute sind alle
   Mods unter Accessory, Quell-Ordner ist weg.
5. **Grid ↔ Folder-View:** Toggle switcht flüssig; in Folder-View trägt jede
   Ordner-Kachel das Mod-Cover (Beispiel: „Gear/Akari Catsuit" zeigt `Akari Catsuit.png`).
6. **Rename:** „Akari Catsuit" → „Akari Catsuit DT". Ordner, `.pmp` und `.png`
   werden konsistent umbenannt; Undo stellt Original her.
7. **Tags:** „AVALON REDUX" + 5 weitere Mods taggen als „Bibo+", „NSFW", „Favorit".
   Filter „Bibo+ UND Favorit" zeigt nur die gemeinsamen Treffer.
8. **Links:** Bei „Miku" (Hair) aus `mod_112738_….jpg` XIV-Mod-Archive-Link
   `https://www.xivmodarchive.com/modid/112738` hinzufügen → Klick öffnet Browser.
9. **Kommentar:** Bei „Akari Catsuit" Markdown-Anleitung mit Drag-Drop-Screenshot
   schreiben, speichern, neu starten — Inhalt + Bild-Referenz bleiben.
10. **Move:** „Empress - Makeup" (existiert in Body-Scales-Skin **und** Face, also
    Dupe) in die neu erstellte Kategorie „_Archiv" verschieben; Tags/Kommentar bleiben dran.
11. **Delete (Mod):** Test-Mod in den Papierkorb schicken → im Windows-Papierkorb
    prüfbar, Mod in App als gelöscht markiert.
12. **Delete (Kategorie):** Leere Test-Kategorie „_Tmp" löschen → Ordner weg,
    DB-Zeile weg. Volle Kategorie „_Archiv" löschen → Dialog bietet "Mods vorher
    verschieben" oder "Alles in Papierkorb".
13. **Health:** Hair-Ordner ohne Bild (diverse Unterordner) sind als `no_image` gelistet.
14. **Dupes:** „Empress - Makeup" erscheint in der Name-Dupe-Liste.
15. **Multi-Root:** Zweiten Root hinzufügen, Scan läuft, Galerie-Filter pro Root,
    Kategorien sind pro Root getrennt verwaltbar.
16. **Missing-Kategorie:** Manuell eine Kategorie im Explorer umbenennen während
    die App läuft → nach Re-Scan zeigt App die alte Kategorie als "missing" mit
    gelbem Hinweis, ohne Mods/Tags aus der DB zu verlieren.

### Performance-Budget

- Initial-Scan < 30 s auf HDD (Hashing nur für `.pmp`/`.ttmp2`, nicht für Bilder).
- Galerie-Scroll 60 fps dank Virtualisierung + gecachter Thumbs.
- Markdown-Preview-Render < 50 ms für typische Anleitung (wenige KB).

---

## Nicht enthalten (bewusst verworfen)

- **Preview-Extraktion aus `.pmp`**: vom User abgelehnt.
- **Auto-Metadata-Scraping** aus XIV Mod Archive per HTTP: nur auf Knopfdruck
  im Link-Editor (Titel holen), keine Hintergrund-Requests.
- **ML-Tag-Erkennung**: stattdessen deterministische Namens-Heuristik.
- **Penumbra-Integration (Enable/Disable in Penumbra)**: out-of-scope.
- **Cloud-Sync der Tags/Kommentare**: DB-Datei kann manuell kopiert werden.
