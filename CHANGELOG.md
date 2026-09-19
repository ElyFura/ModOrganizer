# Änderungen

## v1.1.0

Erste Version mit Selbstaktualisierung. Ältere Builds müssen einmalig von Hand ersetzt
werden; danach meldet sich jede neue Version von selbst.

### Neu

- **Auto-Update** über GitHub Releases: Prüfung beim Start, Banner mit Größenangabe,
  Download mit Fortschritt und anschließendem Austausch samt Neustart. Die laufende Version
  steht unten in der Seitenleiste.
- **Verschachtelte Bibliotheken**: pro Bibliothek umschaltbar, ob Mods strikt unter
  `Kategorie/Mod` liegen oder tiefer (`Solo/NSFW/Sitzend/<Pose>`). Erkannt wird ein Mod
  daran, dass der Ordner Dateien enthält.
- **Kategorien als Baum** in der Seitenleiste. Ein übergeordneter Eintrag zeigt alles
  darunter.
- **`.pose` als eigene Dateiart** mit eigenem Zähler auf der Karte.
- **Anwesenheit sichtbar**: wer online ist und welchen Mod jemand gerade offen hat.
- **Anzeigename** in den Einstellungen — ohne ihn erscheint überall die E-Mail-Adresse.
- **Health-Check „Archiv defekt"** für `.pmp`/`.ttmp2`, die nicht lesbar oder leer sind.
- **Fehlende Mods**: Ordner, die es nicht mehr gibt, werden markiert statt stillschweigend
  weiter angezeigt, mit Banner und Verschieben in den Papierkorb.
- **Sammel-Zuordnung**: ein Basisordner ordnet alle Bibliotheken auf einmal zu.

### Behoben

- Scans hielten die Datenbank-Transaktion während des Hashens offen — bei großen
  Bibliotheken 25 Minuten lang. Ein zweiter Scan lief in eine Sperre und brach mit
  „Exception while reading from stream" ab. Hashen und PMP-Auswertung laufen jetzt außerhalb
  der Transaktion, und pro Bibliothek läuft nur noch ein Scan gleichzeitig.
- Der Penumbra-Abgleich fiel komplett aus, wenn `Penumbra.json` fehlte, obwohl alle
  Collections vorhanden waren. Jetzt mit Rückgriff auf die Sicherungsdatei.
- Der geteilte Penumbra-Stand wurde nur beim Programmstart hochgeladen und war dadurch
  tagealt. Ein Watcher auf den Penumbra-Ordner hält ihn aktuell; das Alter wird angezeigt.
- Nicht zugeordnete Bibliotheken führten zu Abstürzen beim Öffnen des Papierkorbs und der
  Duplikatansicht.
- Der Live-Abgleich verband sich nach Standby oder Netzwerkaussetzern nie wieder — ohne
  jeden Hinweis. Jetzt mit Wiederverbindung und Statusanzeige.
- Kontextmenü: Einträge waren links abgeschnitten.
- Tooltips waren weiß auf weiß und damit unlesbar.
- Die obere Leiste überlagerte sich bei schmalem Fenster; sie bricht jetzt um.
- Beim Beenden gingen Log-Einträge verloren.
- Migrationen laufen unter einer Sperre, damit zwei gleichzeitig startende Clients sich
  nicht ins Gehege kommen.
- Oberfläche durchgängig auf Deutsch.

### Sonstiges

- Thumbnail-Zwischenspeicher wird bei 500 MB begrenzt.
- Beim Löschen einer Kategorie steht jetzt in der Rückfrage, dass die Mods endgültig
  entfernt werden und der Ordner auch beim anderen Benutzer verschwindet.
