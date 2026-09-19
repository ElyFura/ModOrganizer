# FFXIV Mod Organizer

Verwaltet eine **gemeinsame** FFXIV-Mod-Bibliothek für mehrere Personen an verschiedenen
PCs. Die Mod-Ordner werden über Nextcloud (oder Syncthing o. Ä.) synchronisiert, die
Metadaten — Kategorien, Tags, Bewertungen, Kommentare — liegen in einer gemeinsamen
Postgres-Datenbank.

.NET 8 · WPF · Npgsql/Dapper · Supabase (Auth + Realtime)

## Was es kann

- **Galerie** mit Vorschaubildern, virtualisiert (eine Bibliothek mit tausenden Mods
  materialisiert ~20 Kacheln, nicht alle)
- **Mehrbenutzer**: eine Bibliothek ist für alle dieselbe, aber jeder ordnet ihr den Ordner
  auf seinem eigenen PC zu — unterschiedliche Laufwerksbuchstaben sind der Normalfall
- **Live-Abgleich** über Supabase Realtime: Änderungen der anderen erscheinen sofort,
  inklusive Anzeige, wer gerade welchen Mod ansieht
- **Penumbra-Abgleich**: zeigt, welche Mods bei wem in welcher Collection aktiv sind
- Tags mit UND/ODER/NICHT-Filter, smarte Sammlungen, Bewertungen, Markdown-Kommentare
- **Duplikatsuche** über Namen und Dateihashes
- **Posen-Bibliotheken**: erkennt `.pose`-Dateien und beliebig tief verschachtelte Ordner
- Papierkorb mit Wiederherstellung, Aktivitätsverlauf, HTML-Export, Statistik

## Voraussetzungen

- Windows 10/11
- Eine PostgreSQL-Datenbank, die alle Benutzer erreichen (z. B. ein Supabase-Projekt)
- Für Anmeldung, Live-Abgleich und Penumbra-Austausch: ein Supabase-Projekt mit aktivierter
  E-Mail-Anmeldung

Die App bringt die .NET-Laufzeit mit — es ist eine einzelne `.exe`, keine Installation.

## Einrichtung

Beim ersten Start fragt die App nach der Verbindung und legt sie hier ab:

```
%LOCALAPPDATA%\FFXIVModOrganizer\appsettings.json
```

```jsonc
{
  "Supabase": {
    "Url": "https://<projekt>.supabase.co",
    "AnonKey": "<anon key>"
  },
  "Postgres": {
    "ConnectionString": "Host=db.<projekt>.supabase.co;Port=5432;Database=postgres;Username=<benutzer>;Password=<passwort>;SSL Mode=Require"
  },
  "Update": {
    "Enabled": true,
    "Repository": "ElyFura/ModOrganizer"
  }
}
```

Diese Datei enthält Zugangsdaten und gehört **nicht** ins Repository; `.gitignore`
schließt sie aus. Das Datenbankschema legt die App beim Start selbst an.

> **Hinweis zu den Zugangsdaten:** Wer die Datei hat, hat den darin hinterlegten
> Datenbankzugang. Für mehrere Benutzer lohnt sich eine eigene Rolle mit Rechten nur auf die
> App-Tabellen statt des Administrator-Kontos.

### Bibliothek einrichten

1. **Einstellungen → Bibliothek hinzufügen** und den Mod-Ordner wählen.
2. Beim zweiten Benutzer erscheint dieselbe Bibliothek, aber ohne Ordner. Einmal
   **„Alle auf einmal zuordnen…"** klicken und den eigenen Sync-Ordner wählen — die
   Zuordnung wird über den längsten passenden Pfad-Abschnitt gefunden.
3. **Verschachtelt** ankreuzen, wenn die Mods tiefer liegen als `Bibliothek/Kategorie/Mod`
   (typisch für Posen-Sammlungen: `Solo/NSFW/Sitzend/<Pose>`).

## Bauen

```bash
dotnet build ModOrganizer.sln
dotnet test src/ModOrganizer.Core.Tests/ModOrganizer.Core.Tests.csproj
dotnet publish src/ModOrganizer.App/ModOrganizer.App.csproj -c Release -o publish
```

Ergebnis ist eine einzelne, eigenständige `publish\ModOrganizer.App.exe` (~69 MB).

## Kommandozeile

`ModOrganizer.Cli` ist das Werkzeug für Diagnose und Wartung ohne Oberfläche:

| Befehl | Zweck |
| --- | --- |
| `scan <ordner> <verbindung>` | Bibliothek einlesen |
| `verify <verbindung>` | Migrationen anwenden, jede Abfrage messen, Integrität prüfen |
| `selftest <verbindung>` | Anlegen/Umbenennen/Löschen auf einer Wegwerf-Kategorie |
| `roots <verbindung>` | Bibliotheken, Zuordnungen je Benutzer, Penumbra-Stand |
| `roots <verbindung> --as <benutzer>` | Zeigt, was dieser Benutzer sähe |
| `missing <verbindung> [--purge]` | Mods, deren Ordner verschwunden ist |
| `scanmode <verbindung> <id> <fixed\|auto>` | Flaches oder verschachteltes Ordnermodell |
| `profile <verbindung> --as <benutzer> [--name <name>]` | Anzeigename |
| `penumbra <verbindung>` | Lokalen Penumbra-Stand ausgeben |
| `tag` / `untag` | Tags setzen und entfernen |

## Releases

Ein Tag `v<version>` baut über GitHub Actions die exe und hängt sie an ein Release. Die App
prüft beim Start, ob dort eine neuere Version liegt, und aktualisiert sich auf Wunsch
selbst.

Vor dem Tag `<Version>` in `src/ModOrganizer.App/ModOrganizer.App.csproj` hochzählen — der
Workflow bricht ab, wenn Tag und Version nicht zusammenpassen.

## Aufbau

```
src/ModOrganizer.Core    Datenbank, Scanner, Abfragen, Penumbra — testbar, ohne UI
src/ModOrganizer.App     WPF-Oberfläche
src/ModOrganizer.Cli     Diagnose- und Wartungsbefehle
src/ModOrganizer.Core.Tests
```
