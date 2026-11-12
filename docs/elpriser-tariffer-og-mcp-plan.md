# Plan: Elpriser, tariffer og MCP i Elforbrug

Dato: 12. november 2026  
Status: Applikationsimplementering tilføjet; liveaccept og aktivering afventer (se elpriser-implementation.md)

## Mål og arkitektur

Tilføj pålidelige omkostningsberegninger til Elforbrug, og udstil forbrug og omkostninger i dashboardet, HTTP-API'et og en MCP-server.

Løsningen bygges videre på den eksisterende .NET-applikation og Azure Table Storage. Dashboard, HTTP-API og MCP skal bruge samme beregningsservice, så et spørgsmål fra en agent giver samme resultat som brugerfladen.

Arbejdet opdeles i seks etaper. Først etableres prisgrundlag og beregninger; derefter tilføjes brugerflade og MCP-adgang.

## 1. Verificér datakilder og fastlæg beregningsgrundlaget

Afklar de eksterne afhængigheder med konkrete data fra boligerne:

- Hent spotpriser for DK1/DK2 fra Energinets `DayAheadPrices`.
- Afprøv Eloverbliks `getchargelinkswithcharges` for historiske tariftilknytninger og priser. Brug `getcharges` til aktuelle oplysninger, hvis historikfunktionen ikke er tilgængelig. Dagens tariffer må ikke bruges bagudrettet uden dokumentation.
- Kontrollér tarifkoder, enheder, momsbehandling, gyldighedsperioder og hvilke afgifter og abonnementer der faktisk følger med.
- Undersøg, om målepunkterne kan levere kvartersforbrug. Den nuværende integration anmoder om timer, men parseren understøtter allerede kvarterer.
- Identificér login- og klientmønstret fra NemligMCP/BankMCP, så MCP-adgangen kan passe ind i den eksisterende opsætning.

**Acceptkriterium:** Der findes verificerede eksempler for hver bolig. Det er dokumenteret, hvilke prisdele der kan hentes automatisk, hvilke der skal konfigureres, og hvor historikken er ufuldstændig.

## 2. Tilføj prisdata og historik over elaftaler

Opret særskilte modeller og lagring for:

| Data | Indhold |
|---|---|
| Spotpriser | Prisområde, UTC-interval, pris, valuta, enhed og kilde |
| Tariftilknytninger | Målepunkt, tarifejer, tarifkode og gyldighed |
| Tariffer og afgifter | Satser, tidsbånd, enheder, momsbehandling og gyldighed |
| Elaftaler | Bolig, spot-/fastpris, leverandørtillæg, abonnement og afregningsinterval |
| Kildestatus | Seneste vellykkede hentning, dækningsperiode og fejl |

Priser og pengeberegninger bruger `decimal`. Alle aftaler og satser får gyldighedsperioder, så et leverandørskifte ikke ændrer tidligere måneders beregningsgrundlag.

Leverandørens egne vilkår indtastes i første version. Prisområdet forsøges udledt fra målepunktsoplysninger og kan ellers vælges eksplicit. Ukendte tillæg skal være ukendte, ikke automatisk nul.

Nye tabeller og indstillinger tilføjes bagudkompatibelt. Eksisterende boliger fungerer fortsat, mens prisopsætningen mangler.

**Acceptkriterium:** Korrekt prisgrundlag kan gemmes og hentes for en vilkårlig understøttet dato, også hen over aftale- og tarifskift.

## 3. Byg en fælles beregningsmotor

Beregningslogikken placeres i `EnergyMonitor.Core`, mens en fælles service samler forbrug, priser og aftaler.

Den variable omkostning beregnes pr. interval ud fra:

> Forbrug × (energipris + leverandørtillæg + nettarif + øvrige relevante tariffer og afgifter)

Moms tilføjes ud fra komponenternes momsbehandling. Faste abonnementer opgøres særskilt og fordeles over perioden efter en dokumenteret regel. Samme afgift må ikke tilføjes både fra Eloverblik og fra konfiguration.

Følgende regler skal være en del af kontrakten:

- Beregn efter aftalens afregningsinterval. Kvarterpriser og timeforbrug kan kræve et estimat; en antaget fordeling må aldrig fremstå som målt.
- Dagsforbrug giver kun et tydeligt markeret estimat, når prisen varierer i løbet af dagen.
- Manglende forbrug, priser eller aftalevilkår giver manglende komponenter og et særskilt kendt delbeløb — aldrig en misvisende fuld total.
- Negative priser bevares. Tidszoner og sommer-/vintertid håndteres med UTC-intervaller og danske kalenderdage.
- Afrunding sker ved opgørelse og visning, ikke løbende for hvert lille interval.

Resultaterne indeholder komponentfordeling, datadækning, beregningsmetode, anvendte kilder og status som **beregnet, estimeret, delvis eller utilgængelig**. Datadækning og estimering registreres separat, så et resultat kan være begge dele.

**Acceptkriterium:** Håndberegnede testeksempler stemmer, og hver total kan forklares ud fra sine komponenter. »Beregnet« betyder beregnet efter registrerede vilkår, ikke afstemt med en faktura.

## 4. Integrér synkronisering, API og brugerflade

Tilføj pris- og tarifsynkronisering med genkørsel, håndtering af rate limits og særskilt status. Prisfejl må ikke blokere forbrugsimport eller eksisterende alarmer.

Indlæs som udgangspunkt prisgrundlag for de eksisterende 90 dages forbrug, hvor kilderne understøtter det. Hent nye priser løbende og genbesøg nyere data for rettelser.

Beregn omkostninger ved forespørgsel i første version. Det undgår et ekstra lager af beregnede totaler, som kan blive forældede. Brug afgrænsede forespørgsler og tilføj kun cache, hvis målinger viser behovet.

Brugerfladen får:

- Prisområde og elaftale med gyldighedsdato under indstillinger.
- Forbrug og omkostning for valgt periode.
- Fordeling på energi, net, afgifter, moms og abonnementer.
- Forklaring af estimater og manglende oplysninger.

Ved skift af målepunkt følger prisrelaterede boligdata den eksisterende håndtering af historik, så to målere ikke blandes.

**Acceptkriterium:** En bolig kan konfigureres og vise efterprøvelige omkostninger. Rettelser i forbrug eller aftaler slår igennem uden dobbelttælling.

## 5. Udstil en MCP-server med læseadgang

Tilføj `/mcp` i `EnergyMonitor.Web` med Streamable HTTP og det officielle C# MCP-SDK.

| Tool | Formål |
|---|---|
| `get_energy_overview` | Boliger, forbrug, omkostninger og datastatus |
| `get_consumption` | Forbrug for bolig, periode og opløsning |
| `get_costs` | Omkostninger med komponenter og beregningsgrundlag |
| `compare_periods` | Sammenligning af forbrug og omkostninger med datadækning |
| `get_price_breakdown` | Anvendte priser, tariffer og aftalevilkår |
| `get_data_status` | Mangler, seneste synkronisering og kildefejl |

MCP-svar bliver strukturerede og indeholder valuta, enheder, tidszone, periode og kvalitetsoplysninger. Periodegrænser og begrænsninger på datamængder er ens i API og MCP.

Autentifikation implementeres særskilt fra browsercookies: OAuth-baseret adgang med validering af udsteder, målgruppe, udløb og læserettigheder samt kobling til brugerens eksisterende adgang. Serveren annoncerer autorisationsserveren efter MCP-specifikationen. Den konkrete provider fastlægges i etape 1.

**Acceptkriterium:** Den valgte MCP-klient kan logge ind, opdage værktøjerne og hente samme beløb som dashboardet. Uautoriserede kald afvises uden browserredirect, og MCP udstiller ingen skrivefunktioner.

## 6. Verificér og udrul

Hver etape følger RED-GREEN-REFACTOR med relevante tests før implementering. De vigtigste scenarier er:

- Kvarters-, time- og dagsdata samt døgn med 23 og 25 timer.
- Manglende priser, negative priser, moms, abonnementer og tarifskift.
- Overlappende måleopløsninger, genkørsler og efterfølgende rettelser.
- API/MCP-paritet, adgangskontrol og fravær af tokens i svar og logs.
- Lagring og genstart med Azurite samt fejl fra simulerede datakilder.

Kør repositoryets Release-build med warnings som fejl, backendtests, frontend lint/test/build og relevante container- og infrastrukturchecks. Afslut med én `/code-review`.

Aktivér først i dev, sammenhold en afsluttet periode med en faktisk elregning, og forklar eventuelle afvigelser før produktionsaktivering. Prisfunktioner og MCP får separate aktiveringsindstillinger.

**Samlet acceptkriterium:** Spørgsmålet »Hvad kostede sommerhuset sidste måned, og hvordan fordeler beløbet sig?« giver samme dokumenterede svar gennem dashboard og MCP, med tydelig angivelse af eventuelle estimater eller mangler.

## Grundlag og kilder

Planen bygger på kodegennemgang og API-dokumentation. Den oprindelige plan byggede på kodegennemgang og API-dokumentation. Implementering, automatiske kontroller og resterende liveaccept er beskrevet i [implementeringsstatus](elpriser-implementation.md).

- [Energi Data Service: DayAheadPrices](https://www.energidataservice.dk/tso-electricity/DayAheadPrices)
- [Energi Data Service: DatahubPricelist](https://www.energidataservice.dk/tso-electricity/DatahubPricelist)
- [Eloverblik Customer API](https://api.eloverblik.dk/CustomerApi/index.html)
- [Eloverblik OpenAPI-specifikation](https://api.eloverblik.dk/customerapi/swagger/customerapi-v1.0/swagger.json)
- [Officielt C# MCP-SDK](https://github.com/modelcontextprotocol/csharp-sdk)
- [MCP-autorisationsspecifikation](https://modelcontextprotocol.io/specification/latest/basic/authorization)
