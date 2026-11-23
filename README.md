# Energy Monitor

Private monitoring of electricity consumption for a home and a summer house. It imports consumption from Energinet's [Eloverblik Customer API](https://api.eloverblik.dk/CustomerApi/index.html), shows it in a mobile-friendly dashboard (installable as a PWA), sends a daily status report on Telegram, and raises alarms when a day's consumption is unusually low or high, when data stops arriving, or when the data source fails. With electricity prices enabled it shows what a kWh costs hour by hour today and tomorrow, and when it is cheapest (see [Hourly prices](#hourly-prices-today-and-tomorrow)). With a position on a property it fetches the outdoor temperature and shows how much of the consumption the cold explains (see [Consumption and outdoor temperature](#consumption-and-outdoor-temperature)). It also monitors and controls the summer house's heat pump through [Sensibo](https://sensibo.github.io/), records its temperature history, and raises a frost alarm when the house gets too cold (see [Heat pump](#heat-pump-sensibo)).

This is the v1 of `energy-monitor-mvp-spec.md`. The user interface and all messages are in Danish; times are `Europe/Copenhagen`.

## How it fits together

```text
Eloverblik Customer API        Telegram Bot API        Sensibo API
(+ Energi Data Service for prices and Open-Meteo for outdoor temperature, both without a key)
          ▲                           ▲                      ▲
          │                           │                      │
   ┌──────┴───────────────────────────┴──────────────────────┴──┐
   │ EnergyMonitor.Web (.NET 11, one container)                  │
   │  · background scheduler: sync 08:00/17:00, prices 13:30,    │
   │    report 09:00 (Danish time), heat pump every 15 minutes   │
   │  · rule engine and notification outbox                      │
   │  · HTTP API + the built React PWA          │
   └──────┬──────────────────────────────────┘
          │
   SQLite file on a private Linux machine, reached through Tailscale
   (Azure Table Storage and Key Vault are still supported, but not in use)
```

| Path | What |
|---|---|
| `src/EnergyMonitor.Core` | Pure domain: models, Danish-day arithmetic, daily aggregation and data quality, the rule engine, report text. No Azure or HTTP dependencies. |
| `src/EnergyMonitor.Web` | Eloverblik client, Table Storage stores, Telegram channel, scheduler and jobs, API, sign-in, the heat pump module (`HeatPump/`). Serves the PWA from `wwwroot`. |
| `src/frontend` | React + TypeScript + Vite PWA. Builds into `src/EnergyMonitor.Web/wwwroot`. |
| `src/EnergyMonitor.AppHost` | Aspire AppHost for local runs: Azurite, the app, and the fake upstreams. |
| `tests/EnergyMonitor.Tests` | xUnit tests: domain, Eloverblik client, the whole pipeline in-process, API and authorization, storage contract. |
| `tests/EnergyMonitor.FakeUpstreams` | A fake Eloverblik, Telegram and Sensibo with switchable scenarios, for tests and local runs. |
| `deploy/local` | Docker Compose for the private Linux machine (in use). |
| `infra` | Bicep for one Azure resource group per environment (not in use at the moment). |

## Deviations from the specification

| Specification | This implementation | Why |
|---|---|---|
| .NET 8 isolated Azure Functions | .NET 11 ASP.NET Core app with a `BackgroundService`, hosted in Azure Container Apps | Requested when implementation started. .NET 8 left support in November 2026. |
| React PWA on Azure Static Web Apps | The PWA is served by the same container as the API | With one always-on container there is no reason for a second host. Same origin means no CORS and no tokens in the browser: sign-in is a server-side cookie. |
| Timer-triggered functions | One scheduler loop that claims each slot in Table Storage before running it | Gives the same schedule, survives restarts without repeating or losing a slot. Requires exactly one replica, which the Bicep enforces. |
| `User` table | The allowlist is configuration (`Auth:Users`), not a table | Two users; configuration cannot go stale or be edited through the app. |
| Day quality `stale` | Stored qualities are `Complete`, `Partial`, `Missing`. Stale is derived when reading: the newest complete day is more than 72 hours old (`isStale`, and the "Data mangler" alarm) | A day does not change quality by getting older; the property's freshness does. |
| Repeated notifications "at most one per day" | One message when an event is raised and one more only if its severity rises | The daily report already lists every active alarm, so reminders would be duplicates. |
| Rules evaluated on all complete days | Only days inside the 7-day re-fetch window are evaluated | Otherwise the first 90-day import would raise alarms about weeks-old consumption. |

Other choices worth knowing:

- **Missing is never zero.** A day with no usable measurements is stored with `kWh = null` and quality `Missing`. Eloverblik's "no data" (error 30015), quality `A02` (not available) and `A05` (incomplete) never count as measurements. Sums for 7 and 30 days are only shown when every day in the window is complete.
- **Daily-only metering points** (hourly aggregation not supported, error 30008) are stored as daily values. Hours are never made up from them.
- **Changing a property's metering point deletes its history and alarms**, so consumption from two meters is never mixed in one baseline.
- Whether Eloverblik's `dateTo` is inclusive is not stated in its Swagger. Interval times are taken from the response and upserts are idempotent, so either reading is handled.
- After a restart or deployment users must sign in again (cookie keys are not persisted). No data is affected.

## Run locally

Requires the .NET 11 SDK, Node 24+, Docker (for Azurite) and the [Aspire CLI](https://aspire.dev).

```sh
cd src/frontend && npm ci && npm run build && cd ../..   # builds the PWA into the web project
aspire run                                               # or: dotnet run --project src/EnergyMonitor.AppHost
```

Open <http://localhost:5090>. Locally you are signed in as `dev@local` (admin) and the app talks to the fake Eloverblik and Telegram at <http://localhost:5099>. Under **Indstillinger**: *Hent målepunkter*, create *Hjem* and *Sommerhus*, then *Synkronisér nu*.

Try the scenarios (then press *Synkronisér nu*):

```sh
curl -X POST http://localhost:5099/control/scenario/low            # summer house: unusually low latest day
curl -X POST http://localhost:5099/control/scenario/missing        # summer house: no data for the last 5 days
curl -X POST http://localhost:5099/control/scenario/outage         # Eloverblik answers 503
curl -X POST http://localhost:5099/control/scenario/telegram-down  # Telegram fails; messages stay queued
curl -X POST http://localhost:5099/control/scenario/heating        # summer house: consumption follows the fake outdoor temperature
curl -X POST http://localhost:5099/control/scenario/weather-down   # the weather service answers 429
curl -X POST http://localhost:5099/control/scenario/cold           # heat pump: 3,5 °C indoors (frost alarm)
curl -X POST http://localhost:5099/control/scenario/offline        # heat pump: lost its connection 3 hours ago
curl -X POST http://localhost:5099/control/scenario/sensibo-down   # Sensibo answers 503
curl -X POST http://localhost:5099/control/scenario/normal         # reset
curl http://localhost:5099/control/telegram                        # the messages "sent" so far
```

To use the real services locally, copy the tokens from Key Vault into the web project's user secrets and set `UseRealUpstreams=true` for the AppHost. This needs `az login` with read access to the vault's secrets:

```powershell
./scripts/pull-secrets.ps1 -RealUpstreams
```

Run it again on each new workstation, and after a token is renewed in the vault. Do not copy secrets between machines in any other way.

For frontend work, `npm run dev` in `src/frontend` proxies `/api` to the running app.

## Tests

```sh
docker run -d --rm -p 10002:10002 mcr.microsoft.com/azure-storage/azurite \
  azurite-table --tableHost 0.0.0.0 --inMemoryPersistence --skipApiVersionCheck
dotnet test --project tests/EnergyMonitor.Tests     # 350 tests; the 15 Table Storage ones are skipped without Azurite
cd src/frontend && npm run lint && npm test         # ESLint and 112 component/format tests
```

The pipeline tests run the real application in-process with a controllable clock, wired to the fake upstreams.

## Configuration

| Setting | Meaning | Where |
|---|---|---|
| `Eloverblik:RefreshToken` | Personal token from eloverblik.dk | Key Vault secret `Eloverblik--RefreshToken` |
| `Telegram:BotToken` | Bot token from BotFather | Key Vault secret `Telegram--BotToken` |
| `Sensibo:ApiKey` | API key from <https://home.sensibo.com/me/api> | Key Vault secret `Sensibo--ApiKey` |
| `Sensibo:Enabled`, `Sensibo:IntervalMinutes` | The heat pump module (default off) and how often it is recorded (default 15) | App settings / `.env` |
| `Auth:ClientSecret` | Client secret of the sign-in app registration | Key Vault secret `Auth--ClientSecret` |
| `Auth:Authority`, `Auth:ClientId` | OIDC provider (Entra ID or any standard OIDC) | Bicep parameters |
| `Auth:Users:n:Subject` / `:Role` | Allowlist. Subject is an e-mail address the provider reports as verified (Google), or the user's `sub` or object id (`oid`, Entra ID). The sign-in name (`preferred_username`) is not accepted: it can change, and with self sign-up it can be chosen. Role is `admin` or `viewer` | Bicep parameter `users` |
| `Telegram:ChatIds` | Default recipients until they are changed in Indstillinger | Bicep parameter |
| `Schedule:SyncTimes`, `Schedule:ReportTime` | Danish local times, default `08:00`, `17:00` and `09:00` | App settings |
| `Schedule:PriceSyncTime` | Danish local time of the extra fetch of day-ahead prices, default `13:30`; empty turns it off. Only with `Pricing:Enabled` | App settings |
| `Pricing:WindowHours` | Length of the cheapest and the most expensive period of a day, default 3 (1–6) | App settings |
| `Pricing:NotifyDeviationPercent`, `Pricing:NotifyHighPrice` | When tomorrow's prices are worth a Telegram message: an average this far from the latest days' (default 25 %), or an hour at this price or more (default 4.00 DKK/kWh incl. VAT). 0 turns either off | App settings |
| `Weather:BaseUrl`, `Weather:PastDays`, `Weather:BaseTemperatureC` | The outdoor temperature source (default Open-Meteo), how far back each fetch reaches (default and maximum 92 days), and the indoor temperature assumed for a property without a recorded one (default 17) | App settings |
| `Sync:BackfillDays`, `Sync:RefetchDays` | Default 90 and 7 | App settings |
| `ConnectionStrings:tables` | Table endpoint (managed identity) or an Azurite connection string | Bicep / Aspire |
| `KeyVault:Uri` | When set, secrets are read from this vault and re-read every 30 minutes | Bicep |
| `Auth:Mode` | `Oidc` (default), `None` (no sign-in; private network only) or `Development` (local runs) | App settings |
| `Storage:Mode`, `Storage:Path` | `Tables` (default, Azure Table Storage/Azurite), `Sqlite` with a file path, or `InMemory` | App settings |

Secrets exist only in Key Vault (or local user secrets). They are never sent to the browser, never part of an API response, and are kept out of logs: metering point ids are logged as their last four digits, and Telegram and Sensibo requests (whose URLs contain the bot token and API key) are excluded from HTTP logging, with the query redacted from traces. Tests assert this on captured log output.

### Eloverblik token

1. Sign in at <https://eloverblik.dk> with MitID, open *Datadeling* → *Opret token* and create a token.
2. Store it: `az keyvault secret set --vault-name <vault> --name Eloverblik--RefreshToken --value "<token>"`.
3. The app exchanges it for a data access token (valid about 24 hours, fetched at most once a day).

The token is valid for at most one year. The app reads its expiry date from the token and raises a warning 30, 14 and 7 days before, and a technical alarm if it expires or is rejected. If the expiry cannot be read, Indstillinger shows *udløbsdato ukendt*: put a renewal reminder in your calendar. To renew, repeat steps 1–2; the app picks up the new secret within 30 minutes.

### Telegram

Create a bot with [@BotFather](https://t.me/BotFather), store its token as `Telegram--BotToken`, send the bot a message from each recipient's account, and enter the chat ids under Indstillinger → Notifikationer. *Send testbesked* confirms delivery. `INotificationChannel` is the seam for adding another channel.

## Alarm rules

The electricity rules are evaluated only on complete Danish calendar days; the heat pump rules on every collection (every 15 minutes), and only for a property with a linked heat pump. Defaults are starting values for calibration, editable per property under Indstillinger. A low consumption is only an indicator of a possible fault.

| Rule | Default | Result |
|---|---|---|
| Low consumption | Below 50 % of the median of the 14 preceding complete days, median at least 3 kWh, monitoring *Aktiv* | `Observer` on the first day, `Alarm` from the second consecutive day |
| High consumption | Above 200 % of that median, median at least 3 kWh | `Observer` |
| Missing data | No new complete day for 72 hours | `Data mangler` (not a consumption alarm) |
| Source failure | 3 consecutive failed syncs, or a rejected or expired token | Technical alarm |
| Indoor too cold (heat pump) | Latest indoor temperature below 5 °C | `Observer`, then `Alarm` after 120 minutes; closes at 6 °C |
| Heat pump offline | Not seen by Sensibo for more than 60 minutes | `Alarm` |
| Sensibo failure | 3 consecutive failed collections, or a rejected API key | Technical alarm |

- The median excludes the analysed day and needs at least 7 complete days; until then the dashboard shows *Utilstrækkelig historik* and the relative rules are off.
- An event is unique on (property, rule, first affected day). Repeated syncs and restarts cannot duplicate it, and a closed event is never reopened for the same day. A heat pump rule that clears and triggers again on the same day raises a second event (`…~2026-11-14~1`), so a second cold spell is never silent.
- The observed value, median, threshold and rule parameters are stored with the event as a snapshot.
- A failed Telegram delivery stays in an outbox and is retried every minute for up to 72 hours. The event itself is not touched.

## Running on a private Linux machine (Tailscale)

This is how the app runs since 13 November 2026. Azure hosting was retired; only the Key Vault was kept, as a copy of the secrets.

The app has **no sign-in** (`Auth:Mode=None`): every request acts as the administrator, and `/mcp` (when enabled) is open too. It is private because Kestrel listens on `127.0.0.1` only and [`tailscale serve`](https://tailscale.com/kb/1312/serve) publishes it to your tailnet over HTTPS. Never expose port 8080 any other way, and do not use Tailscale Funnel.

Requires Docker with Compose and Tailscale (with MagicDNS and HTTPS certificates enabled for the tailnet) on the machine.

```sh
git clone https://github.com/akakaule/EnergyMonitor.git && cd EnergyMonitor/deploy/local
az login --use-device-code && ../../scripts/pull-secrets.sh && az logout   # writes .env (mode 600) from Key Vault
mkdir -p data && sudo chown 1654 data && sudo chmod 750 data   # 1654 is the app user inside the image
sed -i "s/^TAILNET_HOST=.*/TAILNET_HOST=$(tailscale status --json | jq -r .Self.DNSName | sed 's/\.$//')/" .env   # the only Host name the app answers, besides localhost
# Moving from Azure: put the copied energymonitor.db into data/ first (see below)
docker compose up -d --build
sudo tailscale serve --bg 8080             # https://<machine>.<tailnet>.ts.net
```

- **Storage** is one SQLite file, `deploy/local/data/energymonitor.db` (`Storage:Mode=Sqlite`, `Storage:Path`).
- **Backup**: `scripts/backup-db.sh` copies the file while the app runs, checks the copy, keeps 30 days in `~/backups/energymonitor` (readable by you only) and sends a Telegram message if it fails. Run it nightly from cron or a systemd user timer, and copy the backups to another disk. Indstillinger → *Hent eksport* is a JSON export with masked metering point ids.
- **Update**: `git pull && docker compose up -d --build`.
- **Token renewed** in the vault: run `scripts/pull-secrets.sh` again (it keeps the other settings in `.env`), then `docker compose up -d`. Without `az` on the machine, run the script on one that has it and send the file over the tailnet: `tailscale file cp .env <machine>:`.
- **Logs**: `docker compose logs -f`.
- **Heat pump**: put the key in the vault (`az keyvault secret set --vault-name <vault> --name Sensibo--ApiKey --value "<key>"`), run `scripts/pull-secrets.sh`, set `Sensibo__Enabled=true` in `.env`, `docker compose up -d`, then link the heat pump to Sommerhus under Indstillinger → Varmepumpe.
- **MCP from Claude Code** on a machine in the tailnet: set `Mcp__Enabled=true` in `.env`, restart, then `claude mcp add --transport http energy-monitor https://<machine>.<tailnet>.ts.net/mcp`. Claude.ai connectors cannot reach a tailnet.

**Moving data off Azure Table Storage** (one time): with `az login` and the *Storage Table Data Reader* role on the account, run

```sh
dotnet run --project src/EnergyMonitor.Web -c Release -- copy-tables --from https://<account>.table.core.windows.net --to energymonitor.db
```

It copies every `em*` table into a new SQLite file and prints the row count per table. It refuses a file that already holds data.

## Deployment to Azure (not in use)

The Bicep and the Deploy workflow are kept for later; the steps below describe the setup that ran until 13 November 2026.

A `dev` environment was deployed from a workstation on 10 November 2026 using the Azure Container Registry variant described under *Deploying from a workstation* below. The **Deploy** workflow (GHCR) has not been run.

1. **Sign-in client.** Any standard OIDC provider works; the app uses the authorization code flow with PKCE and a server-side cookie.
   - **Google**: in the [Google Cloud console](https://console.cloud.google.com/apis/credentials) configure the OAuth consent screen (External; add yourself as a test user or publish it), then *Create credentials → OAuth client ID → Web application* with the authorised redirect URI `https://<app>/signin-oidc` (the `redirectUri` output of the deployment). Use `authAuthority = 'https://accounts.google.com'` and the client id; the allowlist subject is the Google account's e-mail address. Anyone with a Google account can reach the sign-in page, but only allowlisted, verified addresses get past it.
   - **Entra ID**: a single-tenant app registration with the web redirect URI above and a client secret. Use `authAuthority = 'https://login.microsoftonline.com/<tenant-id>/v2.0'`; the allowlist subject is the user's object id.
2. **Resource group**: `az group create -n rg-energymonitor-dev -l westeurope`.
3. **Parameters**: fill in `infra/dev.bicepparam` (tenant, client id, users). None of it is secret.
4. **GitHub**: for each environment (`dev`, `prod`) set the variables `AZURE_CLIENT_ID`, `AZURE_TENANT_ID`, `AZURE_SUBSCRIPTION_ID`, `AZURE_RESOURCE_GROUP` for a deployment identity with a federated credential for this repository, and a secret `GHCR_PULL_TOKEN` (a token with `read:packages`). Run the **Deploy** workflow. It builds the image, pushes it to GHCR and deploys `infra/main.bicep`.
5. **Secrets**: grant yourself *Key Vault Secrets Officer* on the vault and set `Eloverblik--RefreshToken`, `Telegram--BotToken` and `Auth--ClientSecret`. Restart the revision once.
6. Sign in, link the metering points under Indstillinger, press *Synkronisér nu*, and *Send testbesked*.

`dev` and `prod` are separate resource groups with separate parameter files and GitHub environments.

### Deploying from a workstation (Azure Container Registry)

No GitHub token or deployment identity is needed; the app pulls from a registry in its own resource group with its managed identity. A Basic registry adds about 5 USD/month.

```sh
# 1. Everything except the app, so the image and secrets exist before it starts
az deployment group create -g rg-energymonitor-dev -f infra/main.bicep -p infra/dev.bicepparam \
  -p createRegistry=true deployApp=false vaultAdminObjectId=$(az ad signed-in-user show --query id -o tsv)
# 2. Build the image in the registry, and store the secrets in the vault (step 5 above)
az acr build --registry <registryName output> --image energy-monitor:$(git rev-parse --short HEAD) .
# 3. The app
az deployment group create -g rg-energymonitor-dev -f infra/main.bicep -p infra/dev.bicepparam \
  -p createRegistry=true vaultAdminObjectId=<same id> containerImage=<registryLoginServer output>/energy-monitor:<tag>
```

Then add the `redirectUri` output to the app registration.

### Expected cost

An estimate to check against the [Azure pricing calculator](https://azure.microsoft.com/pricing/calculator/) for your region before deploying; the figures are not taken from a live quote.

| Resource | Sizing | Rough monthly cost |
|---|---|---|
| Container App | 0.25 vCPU, 0.5 GiB, one replica always on | about 4–15 USD after the monthly free grant, depending on how much of the time is billed at the idle rate |
| Log Analytics + Application Insights | Ingestion capped at 0.1 GB/day | below 1 USD, normally inside the free allowance |
| Storage account (tables) | A few MB, a few hundred thousand transactions | below 0.10 USD |
| Key Vault | Three secrets, re-read every 30 minutes | below 0.10 USD |
| Container registry | GHCR, or a Basic Azure Container Registry | free, or about 5 USD |

The always-on replica is the whole cost. It is the price of running the scheduler inside the app; a scheduled Container Apps Job with a scale-to-zero app would be cheaper and is the first thing to change if cost matters more than simplicity.

### Operations

- **Daily check**: the 09:00 report is the heartbeat. No report means the app or Telegram is down. Repeated sync failures raise a technical alarm, and Indstillinger → Drift shows the latest sync, the latest error code and the token state. `GET /api/system/health` returns the same without secrets.
- **Backup**: Indstillinger → *Hent eksport* (or `GET /api/admin/export`) returns configuration, rules, alarms and all stored consumption as one JSON file.
- **Deletion**: Indstillinger → *Slet forbrugsdata* removes a property's consumption and alarms. Consumption is otherwise kept until deleted.
- **Restart**: safe at any time. Slots, alarms and reports are keyed in storage, so nothing is repeated.
- Development-only tooling (`vite-plugin-pwa`'s Workbox build chain) currently reports moderate `npm audit` advisories. Nothing from those packages is shipped: `npm audit --omit=dev` is clean, and CI checks exactly that.

## Acceptance criteria

Verified locally on 10 November 2026. "Fakes" means the in-repo fake Eloverblik and Telegram.

| Spec §11 | How it was verified |
|---|---|
| 1. Integration: valid token, expired token, 429, 503, partial day, empty result, corrected measurements | `EloverblikClientTests` (request shape per Swagger, token reuse, 60/120/240 s backoff, limited attempts) and `PipelineTests` |
| 2. Time zones | `AggregationTests`, `PipelineTests.DstDays_AreCompleteWith23And25Hours` (29 March and 25 October 2026) |
| 3. Quality: empty series is missing, partial day gives no low alarm | `PipelineTests.EmptyTimeSeries_…`, `PartialDay_…`, `ApiTests.DailyConsumption_ReportsMissingDaysAsNull_NeverZero`, frontend tests |
| 4. Anomaly: one `Observer` across repeated syncs, escalation next day | `RuleEngineTests`, `PipelineTests.LowDay_…`; also driven by hand in the running stack: one alarm, one Telegram message after two syncs |
| 5. Notification: test message, failure kept for retry without duplicate event | `ApiTests.TestMessage_…`, `PipelineTests.TelegramFailure_…`; test message also sent from the UI |
| 6. UI on iPhone and desktop, freshness and latest complete date visible | Rendered in a browser against the running stack; at 390 px no element overflows and every control is at least 40 px high; screenshots reviewed at 500 px and 1280 px; manifest and service worker present. **Not checked on a physical iPhone or installed to a home screen.** |
| 7. Security: unauthorized calls rejected, no tokens in frontend, source or logs | `ApiTests` (every endpoint: 401 unauthenticated, 403 not on the allowlist, 403 viewer on admin), `PipelineTests.NothingSecret_ReachesTheLogs`, CSRF header test, production start refused without OIDC configuration |
| 8. Deploy: Bicep and Actions documented; restart without data loss or duplicate alarms | Restart tests against in-memory storage and against Azurite; `az bicep build` and `docker build` succeed; container smoke-tested. A dev environment was deployed to Azure and checked for health, 401 on the API and the sign-in redirect; CI runs green on GitHub. **The Deploy workflow has not been run.** |

Not verified against the real services: the live Eloverblik API (the client follows the Swagger fetched on 9 November 2026; no personal token was available), live Telegram, and a real Entra ID sign-in.

## Heat pump (Sensibo)

The summer house's heat pump is a module of its own, on with `Sensibo:Enabled` and `Sensibo:ApiKey`. It replaces the separate Heatpump app.

- **Varmepumpe** page: live temperature, humidity and connection; power, mode, target temperature, fan, swing, light (only what the remote supports), a one-shot timer and Climate React; history for the last day, week, month and year. It polls every 60 s while the tab is visible.
- **Recording**: every 15 minutes the scheduler reads the devices' current state and Sensibo's rolling 24 hours of measurements and stores them (one row per device and hour, in any storage mode). A gap shorter than a day is filled in by the next run. Long ranges are averaged to about 720 points.
- **Alarms** on Telegram (see [Alarm rules](#alarm-rules)) for the property the heat pump is linked to, under Indstillinger → Varmepumpe. Thresholds are edited with the property's other rules. A failed collection never judges the stale readings, so a Sensibo outage raises one technical alarm instead of false frost or offline alarms.
- The daily report gets a line such as `Sommerhus · varmepumpe: 9,5 °C inde, 60 % fugt. Tændt: varme 10 °C.`
- **The key stays on the server.** The browser calls `/api/heatpump/sensibo/…`, a fixed allowlist of the Sensibo endpoints the page uses (`users/me/pods`, `pods/{id}`, `acStates`, `historicalMeasurements`, `smartmode`, `timer`); the server adds the key. Reads need the viewer role and changes the admin role, so with `Auth:Mode=None` everyone on the tailnet can control the heat pump. Sensibo errors come back as codes (`sensibo_key_rejected`, `sensibo_rate_limited`, …), never as a 401.
- Requests are gzip-compressed, which Sensibo rewards with a higher rate limit, and there are no automatic retries.
- **MCP** gets one read-only tool, `get_heatpump_status`. There are deliberately no tools that change the heat pump.

## Consumption and outdoor temperature

For a property with a position (latitude and longitude under Indstillinger → the property). The [specification](docs/udetemperatur-spec.md) has the details.

- **Outdoor temperature** is fetched from [Open-Meteo](https://open-meteo.com/) after each sync: hourly values for the latest 92 days, kept as history from then on. No key. The machine must reach `api.open-meteo.com`, and the property's position is sent to it.
- **The property's page** gets the card *Forbrug og udetemperatur*: what a degree colder costs in kWh a day, the base load, how much of the day-to-day variation the cold explains, and the latest day against what was expected. A scatter plot shows each day and the fitted line.
- **Degree-days** are measured against the indoor temperature a linked heat pump has recorded (at least 14 days), otherwise against 17 °C. That matters for a summer house kept at 10 °C.
- The consumption is the whole metering point's, not the heat pump's alone; everything else ends up in the base load.
- A failing weather source is shown on the card as a code. It never fails the sync, raises no alarm and sends no message.
- `GET /api/properties/{id}/heating?days=90` and the MCP tool `get_heating_analysis` return the same data.
- The alarm rules do not use the outdoor temperature yet.
- Not verified against the live Open-Meteo: from the development environment it only answered 429 (quota used up for the shared address).

## Later: Home Assistant

Not implemented, but the seams exist: `IMeasurementSource`, `INotificationChannel` and the neutral `Measurement` record (`sourceId`, `propertyId`, `metric`, `value`, `unit`, `observedAtUtc`, `quality`). A future Home Assistant adapter in the summer house should push selected events and a heartbeat outbound to an authenticated endpoint here, so no inbound ports are opened there.

## Electricity prices and read-only MCP

Pricing and MCP are independently disabled by default. See [implementation and activation notes](docs/elpriser-implementation.md) for the calculation contract, historical tariff mappings, configuration and outstanding live acceptance checks. The dashboard, `/api/energy` endpoints and eight `/mcp` tools (including `get_price_estimate`, the "ca. pris" estimate, and `get_spot_prices`) share `EnergyQueryService` and the decimal calculator in Core. `/mcp` also has `get_heating_analysis` and, with the heat pump module on, `get_heatpump_status`: ten tools in all.

### Hourly prices (today and tomorrow)

With `Pricing:Enabled`, and for each property that has an electricity agreement (which gives the price area; it is never guessed). The [specification](docs/spotpriser-spec.md) has the details.

- **Oversigt** gets a card per group of properties that pay the same price: the price now, a bar per hour for today or tomorrow, the cheapest and the most expensive three hours in a row, and each hour's price split into spot price, supplier, network tariff, taxes and VAT.
- **The price is approximate and all-in**: the day-ahead price (or the fixed price), the supplier's registered markup, the tariffs and taxes Eloverblik reports today, and VAT. Subscriptions are left out. Without tariffs from Eloverblik the card says that they are not included. It has not been checked against an invoice.
- **The daily report** gets a line such as `Elpris i dag (Sommerhus, DK1): gns. 2,31 kr./kWh. Resten af dagen: billigst kl. 13–16 (1,62), dyrest kl. 17–20 (3,71).`
- **Tomorrow's prices** are fetched at 13:30 (they are published around 13:00) and again by the 17:00 sync. A Telegram message is sent only on the days they stand out: an hour with a negative spot price, an average at least 25 % from the latest seven days', or an hour at 4.00 DKK/kWh or more. At most one message per day; turn it off under Indstillinger → Notifikationer.
- `GET /api/energy/{id}/prices?from=…&to=…` (at most 7 days, at most two days ahead) and the MCP tool `get_spot_prices` return the same data.
- Not verified against the live Energi Data Service or a real Telegram chat; the thresholds are starting values.

Enable `Pricing:Enabled` in dev to register dated agreements in Settings. `Mcp:Enabled` additionally requires `Mcp:Authority`, `Mcp:Audience`, `Mcp:Resource` (the public HTTPS `/mcp` URL) and a delegated `Mcp:ReadScope` (default `energy.read`). Browser sign-in cookies do not authorize MCP. The OAuth provider must issue signed access tokens for that audience, and the authenticated subject must also match the existing `Auth:Users` allowlist. Google browser ID tokens are not API access tokens for this resource.

The Bicep equivalents are `pricingEnabled`, `mcpEnabled`, `mcpAuthority`, `mcpAudience`, `mcpResource`, `mcpReadScope`. Keep both flags off in production until per-property data, client login and an invoice have been checked in dev.
