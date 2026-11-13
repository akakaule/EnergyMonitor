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

### 6.6 Telegram-beskeder

Følgende hændelser sender en besked til de eksisterende Telegram-modtagere gennem den eksisterende outbox (samme gentagelse ved fejl som alarmer):

- En nøgle er oprettet: navn, ejer, ejendomme, udløbsdato og hvem der oprettede den.
- En nøgle er tilbagekaldt: navn, ejer og hvem der tilbagekaldte den.
- Nøgleadgang er sat på pause eller genoptaget, og af hvem.
- En aktiv nøgle udløber om 7 dage (én besked pr. nøgle, sendt med den daglige rapport).

Beskeden indeholder aldrig nøglen eller dens hash. Er Telegram ikke sat op, sker handlingen alligevel, og beskeden bliver i outboxen som i dag.

### 6.7 Andre berørte steder

- **Eksport** (`/api/admin/export`) medtager nøglernes metadata, men aldrig `hash`.
- **Sletning af ejendomsdata eller skift af målepunkt** ændrer ikke nøglerne. Hvis en ejendom slettes helt (findes ikke i dag), fjernes den fra nøglernes `properties`.

### 6.8 Faste garantier, der håndhæves af test

Inspireret af HandleMCP, hvor en test fejler, hvis nogen tilføjer kode til at bestille eller betale:

- **Kun læseværktøjer.** En test fejler, hvis et registreret MCP-værktøj ikke er markeret `ReadOnly`, eller hvis listen af værktøjer ændres uden at testen opdateres. Det gør det til en bevidst handling at give MCP mere magt.
- **Ingen fulde målepunkts-id'er eller adresser i MCP-svar.** En test kalder alle seks værktøjer med testdata og fejler, hvis et svar indeholder et 18-cifret målepunkts-id eller en adresse. `get_price_breakdown` returnerer i dag aftalekonfiguration og importerede tarif-links, så det skal kontrolleres og om nødvendigt maskeres (`…4392`), som web-UI'et allerede gør.
- **Nøgler kan aldrig nå `/api`.** Dækket af testene i afsnit 10.

### 6.9 Aktivitet pr. nøgle

HandleMCP har ingen oversigt over, hvem der har brugt serveren. For husstandens energidata er det værd at have:

- Pr. nøgle tælles kald pr. værktøj pr. dansk dag. Tællerne samles i hukommelsen og skrives højst hvert 5. minut (samme rytme som `lastUsedAtUtc`), og de gemmes i 90 dage.
- Der gemmes ikke argumenter, svar eller IP-adresser, kun nøgle-id, dag, værktøjsnavn og antal.
- UI'et viser det i nøgletabellen som "Sidst brugt" og en udfoldelig linje med kald de seneste 30 dage fordelt på værktøj.

## 7. API (kun administratorer, under `/api/admin`, med eksisterende CSRF-header)

| Metode | Sti | Beskrivelse |
|---|---|---|
| GET | `/mcp` | Status: `serverEnabled` (`Mcp:Enabled`), `keysEnabled`, `oauthConfigured`, `endpoint` (offentlig URL til `/mcp`), `tools` (navne og beskrivelser), `scope`. |
| PUT | `/mcp/settings` | `{ keysEnabled }`. |
| GET | `/mcp/keys` | Alle nøgler uden `hash`, nyeste først, med beregnet `status`: `active`, `expired` eller `revoked`. |
| GET | `/mcp/keys/{id}/activity` | Kald pr. værktøj pr. dag de seneste 30 dage (afsnit 6.9). |
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

1. **Forbindelse** (kort): status, endpoint-URL med kopiér-knap, kontakten *Tillad adgang med nøgler*, og en foldbar liste over de seks værktøjer med beskrivelser. Under listen står i klart sprog, hvad en forbundet assistent kan se (forbrug, omkostninger, aftaler og datastatus for de valgte ejendomme), og hvad den ikke kan (ændre noget, se adresser eller fulde målepunkts-id'er, se alarmer eller brugere). Tanken er lånt fra HandleMCP, der på samme måde skriver præcis op, hvad der sendes og ikke sendes. Er `Mcp:Enabled` false, vises kun en note om, at det slås til ved udrulning (`mcpEnabled` i Bicep), og resten er skjult.
2. **Adgangsnøgler** (kort): tabel med Navn, Ejer, Ejendomme, Oprettet, Udløber, Sidst brugt, Status og en *Tilbagekald…*-knap. Tilbagekaldelse kræver bekræftelse i samme stil som "Slet forbrugsdata". Udløbne og tilbagekaldte nøgler vises nedtonet under en foldbar "Tidligere nøgler".
3. **Ny adgangsnøgle** (kort): Navn, Ejer (vælg fra brugerlisten, standard er en selv), Ejendomme (afkrydsning pr. ejendom plus "Alle ejendomme, også nye"), Gyldighed (30 / 90 / 365 dage, standard 90) og *Opret nøgle*.
4. **Vis nøgle én gang**: efter oprettelse erstattes formularen af en boks med nøglen, en kopiér-knap og en tydelig tekst om, at den ikke kan vises igen. Under den vises færdige eksempler med nøglen indsat:
   - Claude Code: `claude mcp add --transport http energy-monitor <endpoint> --header "Authorization: Bearer <nøgle>"`
   - Claude.ai og Claude Desktop: URL'en og header-værdien `Authorization: Bearer <nøgle>` til *Request headers*, hver med kopiér-knap.
   - Generisk JSON-konfiguration til klienter, der understøtter en `headers`-blok.

   Boksen forsvinder, når man forlader sektionen, og nøglen ligger ikke i `localStorage` eller i URL'en.

### 8.3 Forbindelse fra Claude

Både adgangsnøgler og OAuth skal virke fra Claudes klienter. Ifølge Anthropics dokumentation (læst 13. november 2026):

- **Claude Code:** en fast nøgle sendes med `--header`, f.eks. `claude mcp add --transport http energy-monitor <endpoint> --header "Authorization: Bearer <nøgle>"`. Kræver serveren OAuth, tilføjes den uden header, og man logger ind via `/mcp` i Claude Code.
- **Claude.ai og Claude Desktop (custom connector):** en fast nøgle indtastes under *Request headers* i dialogen *Add custom connector*. Til OAuth registrerer Claude selv sin klient hos autoriseringsserveren med Dynamic Client Registration (DCR) eller Client ID Metadata Documents, eller man udfylder et forhåndsregistreret client id og secret.

Konsekvenser for OAuth-vejen:

- Den valgte autoriseringsserver skal enten understøtte DCR/Client ID Metadata Documents eller have en forhåndsregistreret klient til Claude. Entra ID understøtter ikke DCR, og der er rapporteret fejl med Entra-beskyttede connectors, så OAuth-vejen skal afprøves med den konkrete udbyder før den lægges i produktion.
- Den beskyttede ressources metadata (`/.well-known/oauth-protected-resource/mcp`) og `WWW-Authenticate`-svaret, som findes i dag, er det, Claude bruger til at finde autoriseringsserveren.

Panelet viser vejledning til begge klienter: kommandoen til Claude Code og de værdier, der skal i felterne URL og *Request headers* for en custom connector.

### 8.4 Læsere

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
- Oprettelse, tilbagekaldelse og pause lægger én Telegram-besked i outboxen uden nøglen, og en fejlet afsendelse forhindrer ikke handlingen.
- Advarsel om udløb sendes én gang pr. nøgle.
- Alle registrerede værktøjer er `ReadOnly`, og listen svarer til den forventede (afsnit 6.8).
- Intet MCP-svar indeholder et fuldt målepunkts-id eller en adresse (afsnit 6.8).
- Aktivitetstællere opdateres, skrives samlet og indeholder ikke argumenter (afsnit 6.9).

Frontend (Vitest):

- Menupunkt og statuslinje i hver tilstand.
- Oprettelse viser nøglen én gang og skjuler den, når man skifter sektion.
- Tilbagekaldelse kræver bekræftelse.
- Læservisning uden administratorhandlinger.

## 11. Udrulning

1. Implementér bag `Mcp:Enabled` (slået fra som standard), og udrul til dev.
2. Slå `Mcp__Enabled=true` til på dev-containerappen. OAuth-indstillingerne er ikke nødvendige.
3. Opret en nøgle i UI'et, forbind Claude Code, og kør `tools/list` og ét fuldt kald for én ejendom. Gentag med en custom connector i Claude.ai. Tilbagekald nøglen, og bekræft at næste kald får 401, og at Telegram-beskederne er kommet.
4. Afprøv OAuth-vejen med den valgte udbyder fra både Claude Code og Claude.ai (se 8.3).
5. Opdatér `README.md` og `elpriser-implementation.md` med den nye vej.
6. Produktion først efter accept i dev.

## 12. Acceptkriterier

- En administrator kan i UI'et oprette en nøgle, forbinde både Claude Code og en Claude.ai custom connector med den og hente data for de valgte ejendomme, uden ændringer i konfigurationen ud over `Mcp:Enabled`.
- OAuth-vejen virker fortsat, når den er konfigureret.
- Oprettelse, tilbagekaldelse og pause giver en Telegram-besked uden nøglen.
- En tilbagekaldt eller udløbet nøgle afvises ved næste kald.
- En nøgle kan ikke læse ejendomme uden for sin liste.
- Hemmeligheden findes ikke i lager, logs, eksport eller API-svar ud over oprettelsessvaret.
- Alle tests i afsnit 10 består.

## 13. Beslutninger

Truffet 13. november 2026:

1. **Kun administratorer opretter og tilbagekalder nøgler.** Læsere kan ikke oprette nøgler til sig selv; de kan kun se de nøgler, der er udstedt til dem (afsnit 8.3).
2. **OAuth-vejen beholdes** som valgfrit alternativ til nøgler (afsnit 6.1).
3. **Claude skal kunne forbinde med både nøgle og OAuth**, fra Claude Code og fra Claude.ai/Desktop (afsnit 8.3).
4. **Telegram-beskeder** sendes ved oprettelse, tilbagekaldelse, pause og forestående udløb (afsnit 6.6).

## 14. Åbne spørgsmål

1. **Brugerlisten i UI'et:** skal `Auth:Users` også flyttes til databasen i en senere omgang, så administratorer kan tilføje husstandsmedlemmer uden en ny udrulning?
2. **OAuth-udbyder:** hvilken autoriseringsserver skal bruges til OAuth-vejen? Den skal understøtte DCR/Client ID Metadata Documents eller en forhåndsregistreret klient til Claude (afsnit 8.3).

## 15. Inspiration fra HandleMCP

[HandleMCP](https://handlemcp.dk/) (læst 13. november 2026) er en MCP-server til bl.a. nemlig.com. Den kører lokalt på brugerens egen computer (stdio via `uvx`), så den har ingen nøgler, ingen OAuth og ingen adgangsstyring. Login til butikken gemmes som en session i styresystemets nøglering, aldrig som kodeord. Modellen passer ikke direkte til os, fordi vores server ligger i skyen og skal nås fra Claude.ai. Det tager vi med:

| Fra HandleMCP | Hos os |
|---|---|
| En test fejler, hvis nogen tilføjer kode til at bestille eller betale. | Test af, at alle værktøjer er skrivebeskyttede, og at listen ikke ændres ubemærket (6.8). |
| Skrivning er begrænset til kurven, og operationer *sætter* antal i stedet for at lægge til, så gentagelser er ufarlige. | Ikke relevant nu, da vi kun læser. Får MCP senere skriveværktøjer, skal de være idempotente på samme måde. |
| Præcis liste over, hvad der sendes og ikke sendes, vist ved første brug. | Klar beskrivelse i panelet af, hvad en assistent kan og ikke kan se (8.2), og dataminimering i svarene (6.8). |
| "Gem sessionen, ikke koden." | Vi gemmer kun nøglens hash (5). |
| Ingen oversigt over aktivitet, ingen tilbagekaldelse, ingen nøgle pr. klient. | Det er netop huller, vi lukker: nøgle pr. klient, tilbagekaldelse, "sidst brugt" og aktivitet (6.9). |

Ikke med: en lokal stdio-variant. Den ville kræve, at hver klient har adgang til Azure-lageret, og den virker ikke fra Claude.ai.
