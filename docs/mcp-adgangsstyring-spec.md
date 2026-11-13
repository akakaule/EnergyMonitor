# Adgangsstyring for MCP i web-UI'et — specifikation (udkast)

Status: udkast til gennemgang, 13. november 2026. Intet er implementeret.

## 1. Baggrund

MCP-serveren på `/mcp` (seks skrivebeskyttede værktøjer, se [implementeringsstatus](elpriser-implementation.md#http-og-mcp)) styres i dag kun gennem konfiguration:

- `Mcp:Enabled` slår den til. Den kræver `Mcp:Authority`, `Mcp:Audience` og `Mcp:Resource`, ellers starter appen ikke (`EnergyMcp.cs`).
- Adgang kræver et signeret OAuth-access token med audience og scope `energy.read`, og tokenets subject skal stå på `Auth:Users`. Politikken `mcp-read` bruger samme `RoleRequirement(false)` som web-UI'et, så **enhver** bruger på listen, læser eller administrator, har MCP-adgang, så snart de kan skaffe et token.
- Login med Google (som dev bruger) kan ikke udstede access tokens til vores API. MCP kan derfor reelt ikke tages i brug uden en ekstra OAuth-udbyder som Entra ID.
- Web-UI'et kan hverken vise, give eller tilbagekalde MCP-adgang. Frontend modtager `features.mcp`, men bruger det ikke.

## 2. Mål

1. En administrator kan i Indstillinger se, om MCP er slået til, og hvordan en klient forbindes.
2. En administrator kan give en bestemt bruger MCP-adgang, begrænset til udvalgte ejendomme og en udløbsdato, og kan tilbagekalde den igen med øjeblikkelig virkning.
3. Det skal virke med det login, vi har i dag (Google), uden at sætte en OAuth-autoriseringsserver op.
4. Det skal kunne ses, hvilke adgange der findes, hvem der ejer dem, og hvornår de sidst blev brugt.

### Ikke med i denne omgang

- Redigering af selve brugerlisten (`Auth:Users`) i UI'et. Den forbliver i konfigurationen.
- Skriveadgang via MCP. Alle værktøjer forbliver skrivebeskyttede.
- Begrænsning til enkelte værktøjer pr. adgang.
- At appen selv bliver en fuld OAuth-autoriseringsserver.

## 3. Løsning i korte træk

Vi indfører **MCP-adgangsnøgler**: lange, tilfældige nøgler, som en administrator opretter i UI'et til en bestemt bruger. MCP-klienten sender nøglen som `Authorization: Bearer <nøgle>`.

- Nøglen vises **én gang** ved oprettelse. Serveren gemmer kun en hash.
- Hver nøgle har en ejer (et subject fra `Auth:Users`), en udløbsdato, en liste af tilladte ejendomme og et navn, så man kan kende den igen ("Claude Code på laptop").
- Ved hvert kald tjekkes nøglen, og ejeren slås op i `Auth:Users`. Fjernes en bruger fra listen, holder alle brugerens nøgler op med at virke.
- Den eksisterende OAuth-vej (`Mcp:Authority` m.fl.) bevares som et valgfrit alternativ, men er ikke længere påkrævet.

**Hvorfor nøgler frem for OAuth som eneste vej:** nøgler virker med Google-login, kræver ingen ekstra udbyder og kan styres helt fra UI'et. Ulempen er, at en nøgle er en ihændehaverværdi: lækker den, kan den bruges, indtil den udløber eller tilbagekaldes. Det imødegås med obligatorisk udløb, begrænsning til ejendomme, kun læseadgang, hurtig tilbagekaldelse og "sidst brugt"-visning.

## 4. Brugerhistorier

1. Som administrator vil jeg slå MCP-adgang til og fra for hele installationen uden en ny udrulning, så jeg kan stoppe al MCP-adgang med det samme.
2. Som administrator vil jeg oprette en nøgle til mig selv til Claude Code, kun for "Hus", gyldig i 90 dage, og få en færdig kommando, jeg kan kopiere.
3. Som administrator vil jeg oprette en nøgle til en læser i husstanden.
4. Som administrator vil jeg se alle nøgler med ejer, ejendomme, udløb og sidste brug, og tilbagekalde en nøgle, jeg ikke genkender.
5. Som læser vil jeg kunne se, hvilke nøgler der er udstedt til mig. Kun administratorer kan oprette dem (se beslutninger).

## 5. Datamodel

Ny tabel `emmcpkeys` (via `ITableBackend`, ligesom de andre stores). Partition `key`, rækkenøgle = nøglens id.

| Felt | Type | Bemærkning |
|---|---|---|
| `id` | string | 8 tegn, base32, tilfældig. Indgår i nøglen og bruges til opslag. |
| `name` | string | 1–40 tegn, frit navn. |
| `owner` | string | Subject fra `Auth:Users`. Skal findes på listen ved oprettelse. |
| `properties` | string[] | Tilladte ejendoms-id'er. Tom liste er ugyldig. `["*"]` betyder alle, også fremtidige. |
| `hash` | string | SHA-256 (hex) af hele nøglen. Nøglen er 256 bit tilfældig, så en langsom hash er ikke nødvendig. |
| `createdAtUtc` | DateTimeOffset | |
| `createdBy` | string | Subject på den administrator, der oprettede den. |
| `expiresAtUtc` | DateTimeOffset | Obligatorisk. Højst 365 dage frem. |
| `revokedAtUtc` | DateTimeOffset? | Sat ved tilbagekaldelse. Rækken slettes ikke, så historikken bevares. |
| `revokedBy` | string? | |
| `lastUsedAtUtc` | DateTimeOffset? | Opdateres højst hvert 5. minut pr. nøgle, så hvert kald ikke giver en skrivning. |

Global indstilling i `StateStore` under nøglen `settings|mcp`: `{ "keysEnabled": true }`. Mangler den, er standarden `false`. Mønstret er det samme som `NotificationSettingsService`.

**Nøgleformat:** `emk_<id>_<hemmelighed>`, hvor hemmeligheden er 32 tilfældige bytes i base64url (43 tegn). Det faste præfiks gør det let for f.eks. GitHubs secret scanning eller en log-gennemgang at genkende en lækket nøgle.

## 6. Server

### 6.1 Konfiguration

- `Mcp:Enabled` bliver ved med at være den overordnede kontakt, der sættes ved udrulning. Er den `false`, svarer `/mcp` 404 som i dag, og UI'et viser "Slået fra i serverkonfigurationen".
- `Mcp:Authority`, `Mcp:Audience` og `Mcp:Resource` bliver valgfrie. Er de alle sat, accepteres OAuth-tokens som i dag. Er de ikke sat, accepteres kun adgangsnøgler, og `/.well-known/oauth-protected-resource/mcp` udstilles ikke.
- Er de kun delvist sat, afvises opstart som i dag.

### 6.2 Godkendelse af et kald til `/mcp`

Nyt authentication-scheme `McpKey`, som kun bruges af `/mcp`:

1. Læs `Authorization: Bearer`. Matcher værdien ikke formatet `emk_…_…`, overlades kaldet til `McpBearer` (OAuth), hvis den er konfigureret.
2. Slå rækken op på `id`. Sammenlign SHA-256 af den modtagne nøgle med `hash` med `CryptographicOperations.FixedTimeEquals`.
3. Afvis med 401, hvis nøglen ikke findes, hashen ikke passer, den er udløbet eller tilbagekaldt, eller `keysEnabled` er `false`. Svaret er det samme i alle tilfælde, så det ikke afslører, om et id findes.
4. Opret en principal med claims `sub = owner`, `mcp_key = id` og `mcp_properties`.
5. Politikken `mcp-read` udvides til at acceptere schemes `McpKey` og `McpBearer`. Den kører fortsat `RoleRequirement(false)`, dvs. `owner` skal stadig stå på `Auth:Users`, og det kontrolleres ved hvert kald.

Nøgler giver **kun** adgang til `/mcp`. De accepteres ikke under `/api`, og cookies giver fortsat ikke adgang til `/mcp`.

### 6.3 Begrænsning til ejendomme

`EnergyTools` får adgang til de tilladte ejendomme for det aktuelle kald (via `IHttpContextAccessor`, eller ved at sende dem med til `EnergyQueryService`):

- Værktøjer med `propertyId`: er ejendommen ikke tilladt, svares `property_not_found`, præcis som for en ejendom, der ikke findes. Svaret må ikke afsløre, at den findes.
- `get_energy_overview`: medtager kun tilladte ejendomme.
- OAuth-tokens har adgang til alle ejendomme, som i dag.

### 6.4 Begrænsning af kald

Indbygget ASP.NET Core rate limiting på `/mcp`, nøglet på `mcp_key` (eller `sub` ved OAuth): 60 kald pr. minut med en lille kø. Overskrides grænsen, svares 429.

### 6.5 Logning

- Oprettelse, tilbagekaldelse, ændring af `keysEnabled` og afviste kald logges med nøgle-id, ejer og administrator. Selve nøglen eller hashen logges aldrig.
- Den eksisterende test `NothingSecret_ReachesTheLogs` udvides til at dække nøgler.

### 6.6 Andre berørte steder

- **Eksport** (`/api/admin/export`) medtager nøglernes metadata, men aldrig `hash`.
- **Sletning af ejendomsdata eller skift af målepunkt** ændrer ikke nøglerne. Hvis en ejendom slettes helt (findes ikke i dag), fjernes den fra nøglernes `properties`.

## 7. API (kun administratorer, under `/api/admin`, med eksisterende CSRF-header)

| Metode | Sti | Beskrivelse |
|---|---|---|
| GET | `/mcp` | Status: `serverEnabled` (`Mcp:Enabled`), `keysEnabled`, `oauthConfigured`, `endpoint` (offentlig URL til `/mcp`), `tools` (navne og beskrivelser), `scope`. |
| PUT | `/mcp/settings` | `{ keysEnabled }`. |
| GET | `/mcp/keys` | Alle nøgler uden `hash`, nyeste først, med beregnet `status`: `active`, `expired` eller `revoked`. |
| POST | `/mcp/keys` | `{ name, owner, properties, expiresInDays }` med `expiresInDays` ∈ {30, 90, 365}. Svarer 201 med metadata og `secret` (den fulde nøgle), som kun returneres her. Fejlkoder: `invalid_name`, `unknown_owner`, `invalid_properties`, `invalid_expiry`. |
| DELETE | `/mcp/keys/{id}` | Tilbagekalder. Idempotent. Svarer 204. |
| GET | `/users` | Subjects og roller fra `Auth:Users` (til ejer-valget). Kun læsning. |

Læsere får `GET /api/mcp/keys/mine` med deres egne nøgler (uden `hash`).

## 8. UI

### 8.1 Menu

Nyt punkt **MCP** under *System* mellem Drift og Notifikationer, med et ikon i samme stil som de andre (et stik), og en statuslinje:

| Tilstand | Statuslinje | Prik |
|---|---|---|
| `Mcp:Enabled` er false | Slået fra på serveren | grå |
| `keysEnabled` er false | Adgang sat på pause | gul |
| Aktive nøgler | "2 aktive nøgler" | ingen |
| En nøgle udløber inden for 14 dage | "Nøgle udløber om 5 dage" | gul |
| Ingen nøgler | Ingen nøgler | ingen |

### 8.2 Panelet "MCP"

1. **Forbindelse** (kort): status, endpoint-URL med kopiér-knap, kontakten *Tillad adgang med nøgler*, og en foldbar liste over de seks værktøjer med beskrivelser. Er `Mcp:Enabled` false, vises kun en note om, at det slås til ved udrulning (`mcpEnabled` i Bicep), og resten er skjult.
2. **Adgangsnøgler** (kort): tabel med Navn, Ejer, Ejendomme, Oprettet, Udløber, Sidst brugt, Status og en *Tilbagekald…*-knap. Tilbagekaldelse kræver bekræftelse i samme stil som "Slet forbrugsdata". Udløbne og tilbagekaldte nøgler vises nedtonet under en foldbar "Tidligere nøgler".
3. **Ny adgangsnøgle** (kort): Navn, Ejer (vælg fra brugerlisten, standard er en selv), Ejendomme (afkrydsning pr. ejendom plus "Alle ejendomme, også nye"), Gyldighed (30 / 90 / 365 dage, standard 90) og *Opret nøgle*.
4. **Vis nøgle én gang**: efter oprettelse erstattes formularen af en boks med nøglen, en kopiér-knap og en tydelig tekst om, at den ikke kan vises igen. Under den vises færdige eksempler med nøglen indsat:
   - Claude Code: `claude mcp add --transport http energy-monitor <endpoint> --header "Authorization: Bearer <nøgle>"`
   - Generisk JSON-konfiguration til klienter, der understøtter en `headers`-blok.

   Boksen forsvinder, når man forlader sektionen, og nøglen ligger ikke i `localStorage` eller i URL'en.

### 8.3 Læsere

Læsere ser menupunktet MCP med deres egne nøgler i en skrivebeskyttet tabel og en note om, at en administrator opretter nøgler.

## 9. Sikkerhed

- Nøglen vises kun i svaret på POST. Den gemmes kun som hash, logges aldrig og indgår ikke i eksport.
- Sammenligning sker i konstant tid. Svaret på en ugyldig nøgle er det samme uanset årsag.
- Udløb er obligatorisk (højst 365 dage). Der er ingen forlængelse; man opretter en ny nøgle.
- Ejeren kontrolleres mod `Auth:Users` ved hvert kald, så fjernelse fra listen virker straks for alle brugerens nøgler.
- Nøgler giver kun læseadgang og kun til `/mcp`, og de begrænses til valgte ejendomme.
- `keysEnabled` er en nødbremse, der virker uden genstart.
- Administrator-endpoints kræver rollen admin og CSRF-headeren, som resten af `/api/admin`.
- Rate limiting begrænser misbrug af en lækket nøgle.

## 10. Test

Backend (xUnit, både in-memory og Azurite):

- En gyldig nøgle giver adgang til `tools/list` og `tools/call`.
- Forkert hemmelighed, ukendt id, udløbet, tilbagekaldt nøgle eller `keysEnabled=false` giver 401 med samme svar.
- Ejeren fjernet fra `Auth:Users` giver 403/401 med en ellers gyldig nøgle.
- En ejendom uden for nøglens liste giver `property_not_found`, og overview udelader den.
- Nøgler afvises under `/api`, og cookies afvises på `/mcp`.
- `POST` returnerer hemmeligheden én gang, og `GET` returnerer aldrig `hash` eller `secret`.
- Validering af navn, ejer, ejendomme og gyldighed.
- Kun administratorer kan oprette og tilbagekalde; læsere ser kun egne nøgler.
- Opstart uden `Mcp:Authority` m.fl. lykkes, når `Mcp:Enabled` er true; delvis OAuth-konfiguration afvises.
- `lastUsedAtUtc` opdateres og skrives højst hvert 5. minut.
- 429 ved for mange kald.
- Nøglen optræder ikke i logs.

Frontend (Vitest):

- Menupunkt og statuslinje i hver tilstand.
- Oprettelse viser nøglen én gang og skjuler den, når man skifter sektion.
- Tilbagekaldelse kræver bekræftelse.
- Læservisning uden administratorhandlinger.

## 11. Udrulning

1. Implementér bag `Mcp:Enabled` (slået fra som standard), og udrul til dev.
2. Slå `Mcp__Enabled=true` til på dev-containerappen. OAuth-indstillingerne er ikke nødvendige.
3. Opret en nøgle i UI'et, forbind Claude Code, og kør `tools/list` og ét fuldt kald for én ejendom. Tilbagekald nøglen, og bekræft at næste kald får 401.
4. Opdatér `README.md` og `elpriser-implementation.md` med den nye vej.
5. Produktion først efter accept i dev.

## 12. Acceptkriterier

- En administrator kan i UI'et oprette en nøgle, forbinde Claude Code med den og hente data for de valgte ejendomme, uden ændringer i konfigurationen ud over `Mcp:Enabled`.
- En tilbagekaldt eller udløbet nøgle afvises ved næste kald.
- En nøgle kan ikke læse ejendomme uden for sin liste.
- Hemmeligheden findes ikke i lager, logs, eksport eller API-svar ud over oprettelsessvaret.
- Alle tests i afsnit 10 består.

## 13. Beslutninger

Truffet 13. november 2026:

1. **Kun administratorer opretter og tilbagekalder nøgler.** Læsere kan ikke oprette nøgler til sig selv; de kan kun se de nøgler, der er udstedt til dem (afsnit 8.3).
2. **OAuth-vejen beholdes** som valgfrit alternativ til nøgler (afsnit 6.1). Den er nødvendig for klienter, der ikke kan sende en fast bearer-header.

## 14. Åbne spørgsmål

1. **Hvilke klienter skal understøttes?** Claude Code og andre klienter, der kan sende en fast header, virker med nøgler. Hvis Claude.ai-connectors eller Claude Desktop skal bruges direkte, skal det undersøges, om de kan sende en fast bearer-header, eller om de kræver OAuth. Kræver de OAuth, bruges OAuth-vejen, som derfor beholdes.
2. **Brugerlisten i UI'et:** skal `Auth:Users` også flyttes til databasen i en senere omgang, så administratorer kan tilføje husstandsmedlemmer uden en ny udrulning?
3. **Notifikation ved oprettelse:** skal en Telegram-besked sendes, når en nøgle oprettes eller tilbagekaldes?
