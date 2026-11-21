# Elpriser time for time (i dag og i morgen) — specifikation (udkast)

Status: **udkast til godkendelse**, 21. november 2026. Intet er implementeret.

## 1. Overblik

### Hvad og hvorfor

Appen henter allerede Energinets day-ahead-priser frem til i overmorgen, men bruger dem kun til at beregne, hvad forbruget *har* kostet. Denne funktion viser priserne **fremad**: hvad strømmen koster time for time i dag og i morgen, og hvornår den er billigst og dyrest. Så kan vask, opvask, elbil og varmepumpe lægges i de billige timer.

### Hvad brugeren får

1. **Et priskort på Oversigt** med et søjlediagram over dagens timer, en markering af den aktuelle time, skift mellem *I dag* og *I morgen*, og de billigste og dyreste sammenhængende timer.
2. **En linje i den daglige rapport kl. 09:00** om resten af dagen: gennemsnit, billigste og dyreste periode.
3. **En Telegram-besked om eftermiddagen**, når morgendagens priser er klar.
4. **Et MCP-værktøj**, `get_spot_prices`, så Claude kan svare på »hvornår er strømmen billigst i morgen?«.

### Sådan hænger det sammen

```text
Energi Data Service (DayAheadPrices, kvarterpriser)
        │  eksisterende SpotPriceClient + ny synkronisering kl. 13:30
        ▼
   tabellen spotprices (findes)          Eloverbliks aktuelle tariffer (findes)
        │                                        │
        └──────────────┬─────────────────────────┘
                       ▼
        HourlyPrices (ny, ren beregning i Core)
        timepris = (spot + elselskabets tillæg + tariffer og afgifter) × moms
                       │
      ┌────────────────┼──────────────────┬──────────────────┐
      ▼                ▼                  ▼                  ▼
 GET /api/energy/   priskort på      rapportlinje og     MCP-værktøjet
 {id}/prices        Oversigt         »priser i morgen«   get_spot_prices
```

### De vigtigste valg

| Valg | Beslutning | Begrundelse |
|---|---|---|
| Hvilken pris vises | **Ca. pris pr. kWh inkl. alt** (spot, elselskabets tillæg, nettarif, afgifter, moms). Ren spotpris vises som en del af tallet. | Nettariffen er flere gange højere kl. 17–21 end om natten. Den billigste *spot*time er derfor ikke altid den billigste time at bruge strøm i. |
| Opløsning | Timer i diagrammet og i beskederne. Kvarterpriserne gemmes fortsat og udleveres af API'et. | 96 søjler kan ikke læses på en telefon, og husholdningens forbrug planlægges i timer. |
| »Billigst« | Den sammenhængende periode på 3 timer med laveste gennemsnit (kan indstilles). | En enkelt billig time kan man sjældent bruge til noget; en vask tager 2–3 timer. |
| Morgendagens priser | Ny synkronisering kun af priser kl. 13:30 og én besked, når morgendagen er komplet. | Priserne offentliggøres normalt lidt efter kl. 13. I dag hentes de først kl. 17:00. |
| Slås til med | Det eksisterende `Pricing:Enabled`. Ingen ny funktionskontakt. | Funktionen er en visning af data, prismodulet allerede henter. |

### Ikke med i denne omgang

- Automatisk styring af varmepumpen efter prisen.
- Alarmer ved meget høje eller negative priser (negative timer nævnes dog i beskeden om i morgen).
- Priser før 1. oktober 2025 (importøren understøtter kun kvartermarkedet).
- Prognoser ud over de offentliggjorte day-ahead-priser.

## 2. Brugerhistorier

1. Som beboer vil jeg om morgenen kunne se, hvornår strømmen er billigst resten af dagen, så jeg kan sætte opvaskemaskinen til det tidspunkt.
2. Som beboer vil jeg om eftermiddagen have besked om morgendagens priser, så jeg kan planlægge uden at åbne appen.
3. Som beboer vil jeg se den pris, jeg faktisk betaler pr. kWh, ikke kun børsprisen.
4. Som ejer af to boliger i hvert sit prisområde vil jeg se priserne for hver bolig.
5. Som bruger af Claude vil jeg kunne spørge til priserne gennem MCP.

## 3. Beregning

Ny ren funktion `EnergyMonitor.Core/Pricing/HourlyPrices.cs`. Den følger kontrakten i [implementeringsstatus](elpriser-implementation.md#beregningskontrakt): decimaler, halvåbne UTC-intervaller, ingen afrunding undervejs, negative priser bevares.

**Input:** en dansk kalenderdag, tidszonen, spotpriser for dagen, boligens seneste elaftale (kan mangle) og satserne fra Eloverbliks aktuelle tariffer (`ApproximatePricing.FromCurrentCharges`, kan være tom).

**Pr. time** (en lokal klokketime; dagen har 23 eller 25 timer ved skift til og fra sommertid):

| Felt | Indhold |
|---|---|
| `spot` | Tidsvægtet gennemsnit af timens kvarterpriser, DKK/kWh ekskl. moms. `null`, hvis et kvarter mangler. |
| `energy` | `spot` ved spotaftale, `FixedPrice` ved fastprisaftale. |
| `supplier` | Aftalens `SupplierMarkup`, ellers 0. |
| `network`, `tax` | Summen af de kWh-satser, der gælder i timen (`FromHour`/`ToHour`). |
| `vat` | Aftalens momssats (ellers 25 %) af de fire dele ovenfor. |
| `total` | Summen af delene inkl. moms. `null`, hvis `energy` er `null`. |

Abonnementer (kr. pr. dag) indgår ikke: de afhænger ikke af, hvornår strømmen bruges.

**Pr. dag:**

- `status`: `Complete` (alle timer har pris), `Partial` (nogle mangler) eller `Pending` (ingen priser endnu, typisk i morgen før kl. 13).
- `average`, `min`, `max` af `total`, kun når status er `Complete`.
- `cheapest` og `mostExpensive`: den sammenhængende periode på `Pricing:WindowHours` timer (standard 3, tilladt 1–6) med laveste og højeste gennemsnit af `total`. Ved lighed vindes af den tidligste. Kan beregnes for hele dagen eller kun fra et givet tidspunkt (»resten af dagen«); er der færre timer tilbage end perioden, udelades de.
- `negativeHours`: timer, hvor `spot` er under 0.
- `assumptions`: de samme koder som »ca. pris« (`current_tariffs`, `charges_missing`, `supplier_markup_missing`) plus `no_agreement`, når boligen ikke har en elaftale.

**Når noget mangler:**

| Situation | Resultat |
|---|---|
| Ingen elaftale på boligen | Prisområdet kendes ikke. Der vises ingen priser, men en henvisning til Indstillinger → Elpriser. Området gættes ikke. |
| Ingen tariffer fra Eloverblik | `total` er spot + tillæg + moms, og kortet siger, at nettarif og afgifter ikke er med. |
| Fastprisaftale | Kun tarifferne varierer over døgnet. Kortet viser det, og spotprisen vises til orientering. |

Tallene er, som »ca. pris«, beregnet med dagens tariffer og de registrerede vilkår. De er ikke afstemt mod en regning.

## 4. Synkronisering

- `PriceSyncService` får en metode, der kun henter spotpriser (i dag til i overmorgen) for de områder, boligernes aftaler bruger. Den kalder ikke Eloverblik.
- Ny planlagt slot `prices` med `Schedule:PriceSyncTime` (standard `13:30`, tom værdi slår den fra). Den reserveres i lageret som de øvrige slots, så en genstart hverken gentager eller mister den.
- Synkroniseringen kl. 17:00 henter fortsat priserne og fungerer som andet forsøg.
- Fejl registreres som i dag i `pricesources` og påvirker ikke forbrugssynkronisering eller alarmer.

## 5. HTTP-API

`GET /api/energy/{id}/prices?from=YYYY-MM-DD&to=YYYY-MM-DD` — læseadgang, findes kun med `Pricing:Enabled`. Datoerne er inklusive danske dage, højst 7, og må ligge op til to dage frem. Fejl følger de øvrige `/api/energy`-endepunkter (`invalid_range`, 404).

```json
{
  "propertyId": "sommerhus", "area": "DK1", "currency": "DKK", "unit": "kWh",
  "timeZone": "Europe/Copenhagen", "windowHours": 3,
  "assumptions": ["current_tariffs"],
  "lastPriceSyncUtc": "2026-11-21T12:30:04Z",
  "days": [{
    "date": "2026-11-22", "status": "Complete",
    "average": 2.31, "min": 1.42, "max": 3.87,
    "cheapest": { "fromUtc": "…", "toUtc": "…", "average": 1.47 },
    "mostExpensive": { "fromUtc": "…", "toUtc": "…", "average": 3.71 },
    "negativeHours": 0,
    "hours": [{ "startUtc": "…", "endUtc": "…", "spot": 0.52, "energy": 0.52, "supplier": 0.05,
                "network": 0.14, "tax": 0.72, "vat": 0.36, "total": 1.79 }]
  }]
}
```

Tallene i eksemplet er opdigtede. Med `resolution=quarter` returneres kvarterpriserne i stedet for timer; perioderne beregnes stadig på timer.

## 6. Brugerflade

**Priskort på Oversigt**, under ejendomskortene, kun når `features.pricing` er sat.

- Ét kort pr. prisområde. Boliger i samme område og med samme vilkår deler kort; ellers får hver bolig sit.
- Overskrift »Elpris«, den aktuelle times pris som stort tal (`2,31 kr./kWh`) og teksten »ca. pris inkl. tariffer, afgifter og moms«.
- Valg mellem *I dag* og *I morgen*. *I morgen* viser »Priserne for i morgen kommer normalt lidt efter kl. 13«, indtil de er hentet.
- Søjlediagram med en søjle pr. time. Den aktuelle time er fremhævet, forløbne timer er nedtonede, og de billigste og dyreste perioder har egen farve og står også som tekst: »Billigst kl. 02–05 · gns. 1,47 kr.« Farve er aldrig den eneste markering.
- Tryk eller hold over en søjle viser timens pris opdelt i spot, elselskab, nettarif, afgifter og moms.
- Antagelser vises som under »ca. pris« (f.eks. manglende tariffer).
- Kortet genindlæses ved timeskift, mens fanen er synlig.

`BarChart` skal udvides: den antager i dag kWh og værdier på nul eller derover. Den skal kunne formatere andre enheder og tegne negative værdier under en nullinje. Den skal fortsat være læsbar ved 390 px og med skærmlæser (en beskrivelse pr. søjle).

**Indstillinger → Notifikationer** får en afkrydsning »Send elpriser for i morgen«, slået til som standard.

## 7. Telegram

**Linje i den daglige rapport** (kl. 09:00), én pr. prisområde, om timerne fra rapporttidspunktet til midnat:

```text
Elpris i dag (Sommerhus, DK1): gns. 2,31 kr./kWh. Billigst kl. 13–16 (1,62), dyrest kl. 17–20 (3,71).
```

Linjen udelades, hvis dagens priser ikke er komplette. Deler boligerne område og vilkår, nævnes de sammen.

**Besked om i morgen**, sat i kø første gang en prissynkronisering efterlader morgendagen komplet:

```text
Energy Monitor · elpriser i morgen, søn. 22. nov.
Sommerhus (DK1): gns. 2,05 kr./kWh. Billigst kl. 02–05 (1,47), dyrest kl. 17–20 (3,40).
2 timer med negativ spotpris.
```

- Beskedens id er `prices|<dato>`, så en dag får én besked, uanset hvor mange synkroniseringer der kører.
- Den går gennem den eksisterende outbox med samme genforsøg og modtagere som rapporten.
- Er priserne ikke komplette efter synkroniseringen kl. 17:00, sendes ingen besked den dag. Det er ikke en alarm.
- Tallene i eksemplerne er opdigtede.

## 8. MCP

Nyt skrivebeskyttet værktøj `get_spot_prices(propertyId, from, to)` med samme svar som HTTP-endepunktet og samme grænser. Det deler `EnergyQueryService` med API'et, så svarene er ens. Antallet af værktøjer bliver otte (ni med varmepumpen).

## 9. Konfiguration

| Indstilling | Standard | Betydning |
|---|---|---|
| `Pricing:Enabled` | findes | Slår også denne funktion til. |
| `Pricing:WindowHours` | `3` | Længden af billigste og dyreste periode, 1–6 timer. |
| `Schedule:PriceSyncTime` | `13:30` | Dansk tid for den ekstra prissynkronisering. Tom slår den fra. |
| Notifikationer: `TomorrowPricesEnabled` | til | Gemmes sammen med de øvrige notifikationsindstillinger. Eksisterende indstillinger uden feltet læses som slået til. |

Ingen nye tabeller, hemmeligheder eller udgående værter.

## 10. Test og accept

Automatiske test:

- `HourlyPrices`: sommertidsdage med 23 og 25 timer, negative priser, manglende kvarter (timen får ingen pris, dagen bliver `Partial`), tariffer pr. time, fastpris, ingen aftale, lighed mellem perioder, »resten af dagen« med færre timer end perioden.
- Pipeline med de falske kilder: slot kl. 13:30 kører én gang, beskeden om i morgen sendes én gang efter to synkroniseringer og slet ikke, når morgendagen er ufuldstændig; rapportlinjen udelades uden komplette priser. `FakeUpstreams` får et scenarie, hvor morgendagens priser mangler.
- API: grænser for datoer, 404, lukket uden `Pricing:Enabled`, samme svar gennem HTTP og MCP.
- Frontend: kortet med komplette, ventende og delvise priser, negative søjler, opdelingen pr. time, 390 px.

Accept: priserne for en konkret dag stemmer med Energi Data Service, og en times `total` kan genfindes ved at lægge delene sammen. Kortet og begge Telegram-beskeder er set i den kørende løsning.

## 11. Rækkefølge

1. Beregningen i Core og API-endepunktet.
2. Priskortet på Oversigt, inklusive udvidelsen af `BarChart`.
3. Synkronisering kl. 13:30, rapportlinjen og beskeden om i morgen.
4. MCP-værktøjet, README og implementeringsstatus.

Hvert trin kan tages i brug for sig.

## 12. Åbne spørgsmål

1. **Pris inkl. alt eller ren spotpris som hovedtal?** Specifikationen vælger inkl. alt. Det gør tallene afhængige af, at Eloverbliks tariffer og elaftalen er registreret rigtigt.
2. **Er 3 timer den rigtige periode?** Alternativet er at vise både den billigste enkelttime og den billigste periode.
3. **Skal beskeden om i morgen komme hver dag,** eller kun når noget skiller sig ud (f.eks. negative timer, eller en forskel mellem billigst og dyrest over en grænse)?
4. **Eget menupunkt »Elpriser«** med flere dage og historik, eller er kortet på Oversigt nok til en start?
