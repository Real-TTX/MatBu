# MatBu

MatBu (Matthix + Backup) ist die Basis für einen robusten Backup- und Sync-Server mit Master-/Slave-Verbindungen über HTTPS.

## Starten

```powershell
docker compose build
docker compose up -d
```

Die Oberfläche ist danach unter `http://localhost:9293` erreichbar. Die Daten liegen im Docker-Volume `matbu_matbu-data`.

Für einen Release-Stack:

```powershell
docker build -t matbu:latest .
docker compose -f docker-compose.release.yml up -d
```

## Aktueller Stand

- Dashboard mit Aktivitätsübersicht und Systemstatus
- getrennte Bereiche für Tasks, Objects und Benutzer
- responsive Tabellen-/Toolbar-Grundlayout
- SQLite-Persistenz im Volume (`/data/matbu.db`) mit Tabellen für Objects, Tasks, Users und Sessions
- ASP.NET-Core-API für Summary, Tasks, Objects, Benutzer und Login-Startpunkt
- lokale Anmeldung mit PBKDF2-Passwort-Hashing und 12-Stunden-Session-Cookie im persistenten Store
- Hintergrund-Scheduler für aktivierte Tasks mit Intervallen bis zur Transfer-Engine
- Object-Typ `DockerVolume` als vorbereitete Backup-Quelle
- isolierter Docker-Volume-Worker mit read-only Socket-Mount und TAR-Archivierung
- SMB-Zielablage über einen im Worker gemounteten Share-Pfad mit Erreichbarkeitsprüfung
- nativer SMB-Upload über `smbclient` mit temporärer Auth-Datei im Worker
- 30-Sekunden-SMB-Timeouts und atomare `.partial`-Archivierung
- Dockerfile und Dev-/Release-Compose auf Port 9293
- serverseitige Razor Pages fuer Dashboard, Tasks, Objects, Benutzer und Jobs
- CI-Farbe `#0b7f8a` mit System/Hell/Dunkel-Darstellung
- Monitoring-Health-Endpunkt mit persistentem Admin-Token

## Monitoring-API

Nach Admin-Login kann das Token ueber `GET /api/monitoring/token` abgerufen werden. Der Health-Endpunkt ist anschliessend ohne Session-Cookie nutzbar:

```http
GET /api/monitoring/health
X-MatBu-Token: <monitoring-token>
```

Der Endpunkt antwortet mit `200 Healthy`, wenn alle Objects erreichbar sind, kein Task aktuell im Fehlerzustand steht und innerhalb der letzten 24 Stunden kein Job fehlgeschlagen ist. Bei Problemen wird `503 Degraded` mit den betroffenen Objects, Tasks und der Anzahl aktueller Jobfehler geliefert. Ein Admin kann das Token mit `POST /api/monitoring/token/regenerate` rotieren. Das Token liegt persistent im Volume unter `/data/monitoring.token`.

## Nächste technische Bausteine

1. per-Object-Credential-Verwaltung statt globaler SMB-Umgebungsvariablen
2. resumable Transfer-Jobs mit Checkpoints, Retry und Offline-Wiederaufnahme
3. Master-/Slave-Handshake über HTTPS und Monitoring-/Notification-API

Der initiale lokale Zugang lautet `admin` / `admin` und sollte vor produktivem Einsatz geändert werden.
