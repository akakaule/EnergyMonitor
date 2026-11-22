# Forbrug og udetemperatur — specifikation

Status: **implementeret**, 22. november 2026, inklusive alarmer der tager højde for udetemperaturen (afsnit 9). Kontrolleret med de falske kilder; ikke afprøvet mod det rigtige Open-Meteo (se afsnit 8).

## 1. Overblik

### Hvad og hvorfor

Et sommerhus, der står tomt, bruger næsten kun strøm på varmepumpen, og hvor meget afhænger af, hvor koldt det er. Uden udetemperaturen kan man ikke se, om et højere forbrug skyldes kulden, eller at noget er galt. Denne funktion henter udetemperaturen og viser, hvor meget af forbruget kulden forklarer.

### Hvad brugeren får

1. **Position på ejendommen** (bredde- og længdegrad) under Indstillinger. Den bruges kun til at hente udetemperaturen.
2. **Kortet »Forbrug og udetemperatur«** på ejendommens side: hvad en grad koldere koster i kWh pr. døgn, grundforbruget, hvor stor en del af udsvingene kulden forklarer, og seneste døgn sammenlignet med det forventede. Et punktdiagram viser hvert døgn og den forventede linje.
3. **Et MCP-værktøj**, `get_heating_analysis`, med de samme tal.
4. **Alarmer for lavt og højt forbrug, der tager højde for kulden** (afsnit 9): en kuldeperiode er ikke længere »højt forbrug«, og en mild uge skjuler ikke et hus, der er holdt op med at varme.

### Sådan hænger det sammen

```text
Open-Meteo (timetemperatur, 2 m)      Eloverblik (døgnforbrug, findes)      Sensibo (indetemperatur, findes)
        │ efter hver synkronisering            │                                     │
        ▼                                      │                                     │
   tabellen weather (ny)                       │                                     │
        └──────────────────────┬───────────────┴─────────────────────────────────────┘
                               ▼
              HeatingAnalysis (ny, ren beregning i Core)
              kWh pr. døgn = grundforbrug + kWh pr. graddag × graddage
                               │
            ┌──────────────────┼────────────────────┐
            ▼                  ▼                    ▼
   GET /api/properties/   kort på ejendommens   MCP-værktøjet
   {id}/heating           side                  get_heating_analysis
```

### De vigtigste valg

| Valg | Beslutning | Begrundelse |
|---|---|---|
| Vejrkilde | Open-Meteo, uden nøgle | Gratis til privat brug, giver de seneste 92 dage i ét kald, og kræver ingen konto. DMI's åbne data kræver en API-nøgle og et valg af målestation. |
| Hvad måles kulden imod | Varmepumpens målte indetemperatur, når ejendommen har en tilknyttet varmepumpe med mindst 14 døgn; ellers 17 °C | Et sommerhus holdt på 10 °C ville mod 17 °C se syv grader koldere ud, end det er. 17 °C er basis for danske graddage. |
| Hvilket forbrug | Hele målepunktets | Der er ingen måler på varmepumpen. Alt andet forbrug havner i grundforbruget. |
| Model | En ret linje (mindste kvadrater) gennem døgnene | Kan forklares med to tal og kræver ingen indstilling. |
| Slås til med | En position på ejendommen. Ingen funktionskontakt. | Uden position hentes intet. |

### Ikke med i denne omgang

- Udetemperatur på varmepumpesiden og i den daglige rapport.
- Vejr ud over temperatur (vind, sol), som også påvirker varmeforbruget.
- Opslag af position ud fra adressen.

## 2. Beregning

`EnergyMonitor.Core/Weather/HeatingAnalysis.cs`.

- Et døgn er med, når forbruget er komplet, og der er en udetemperatur for **hver** time i det danske døgn (23, 24 eller 25 timer). Døgnets udetemperatur er gennemsnittet af timerne.
- Døgnets indetemperatur er gennemsnittet af timegennemsnittene fra varmepumpen og kræver målinger i mindst 20 af døgnets timer.
- **Graddage** for et døgn: indetemperatur (eller 17 °C) minus udetemperatur, dog aldrig under 0.
- Enten måles alle døgn mod indetemperaturen, eller også gør ingen: er der indetemperatur for mindst 14 døgn, bruges kun de døgn (`usesIndoor`). De to grundlag blandes aldrig.
- Linjen `kWh = grundforbrug + kWh pr. graddag × graddage` findes med mindste kvadrater. `rSquared` (0–1) er den del af udsvingene fra døgn til døgn, linjen forklarer.
- Færre end 14 døgn, eller døgn der alle er lige kolde, giver ingen model (`InsufficientData`).
- For hvert døgn beregnes det forventede forbrug ud fra linjen.

Brugerfladen kalder tallene usikre, når `rSquared` er under 0,3, eller når en grad koldere ikke giver højere forbrug.

## 3. Hentning og lagring

- `OpenMeteoClient` kalder `GET /v1/forecast?latitude=…&longitude=…&hourly=temperature_2m&past_days=92&forecast_days=1&timeformat=unixtime`. Positionen sendes med fire decimaler.
- Hentningen sker efter hver forbrugssynkronisering (kl. 08:00, kl. 17:00 og »Synkronisér nu«), én gang pr. ejendom med position. Det er 2–4 kald i døgnet; Open-Meteos gratis grænse er langt højere.
- Kun timer, der er slut, gemmes. Resten af svaret er en prognose.
- Tabellen `weather`: partition = ejendom, række = UTC-døgn med 24 timeværdier. Et nyt svar erstatter de timer, det indeholder, og bevarer resten, så historikken vokser ud over de 92 dage.
- En fejl gemmes som en kode (`weather_rate_limited`, `weather_unreachable`, `weather_http_…`, `weather_bad_response`) og vises på kortet. Den påvirker hverken forbrugssynkroniseringen eller alarmerne, og der sendes ingen besked.
- Ændres positionen, slettes ejendommens gemte temperaturer og hentes igen ved næste synkronisering.

## 4. HTTP-API

`GET /api/properties/{id}/heating?days=90` — læseadgang. `days` er 30–365, standard 90.

```json
{
  "propertyId": "sommerhus", "status": "Ok", "unit": "kWh", "periodDays": 90,
  "baseTemperatureC": 17, "usesIndoor": true,
  "model": { "baseKWhPerDay": 1.8, "kWhPerDegreeDay": 0.42, "rSquared": 0.81, "days": 74 },
  "latest": { "date": "2026-11-20", "kWh": 6.4, "outdoorC": 3.1, "indoorC": 10.2, "degreeDays": 7.1, "expectedKWh": 4.8 },
  "lastWeatherSyncUtc": "2026-11-22T07:00:12Z", "weatherErrorCode": null,
  "days": [ { "date": "…", "kWh": 5.1, "outdoorC": 6.0, "indoorC": 10.1, "degreeDays": 4.1, "expectedKWh": 3.5 } ]
}
```

Tallene er opdigtede. `status` er `Ok`, `InsufficientData` eller `NoLocation`.

Positionen sættes med de eksisterende `POST` og `PUT /api/admin/properties` (`latitude`, `longitude`; begge eller ingen, ellers `invalid_location`) og returneres af `GET /api/properties`.

## 5. Brugerflade

- **Indstillinger → ejendommen**: felterne Breddegrad og Længdegrad. Decimaler kan skrives med komma, og et koordinatpar kopieret fra et kort (`55.9321, 11.6012`) kan sættes ind i det første felt. Tomme felter fjerner positionen.
- **Ejendommens side**: kortet »Forbrug og udetemperatur« med valg af periode (3 mdr., 6 mdr., 1 år), tre nøgletal, en linje om seneste døgn (`fre. 20. nov.: 8,4 kWh ved 5,8 °C ude · forventet 8,2 kWh (+3 %)`) og punktdiagrammet. Hvert punkt kan læses op og vises ved berøring. Uden position viser kortet en henvisning til Indstillinger.
- Kortet nævner Open-Meteo som kilde, som licensen (CC BY 4.0) kræver.

## 6. MCP

Nyt skrivebeskyttet værktøj `get_heating_analysis(propertyId, days)` med samme svar som HTTP-endepunktet. Antallet af værktøjer bliver ni (ti med varmepumpen).

## 7. Konfiguration

| Indstilling | Standard | Betydning |
|---|---|---|
| `Weather:BaseUrl` | `https://api.open-meteo.com/` | Vejrkilden. |
| `Weather:PastDays` | `92` | Hvor langt tilbage hvert kald henter (højst 92). |
| `Weather:BaseTemperatureC` | `17` | Den indetemperatur, der regnes med uden en målt. |

Maskinen skal kunne nå `api.open-meteo.com`. Ejendommens position sendes til Open-Meteo; der sendes intet andet.

## 8. Test og accept

- Core: linjen genfindes præcist, graddage går ikke under 0, indetemperatur erstatter basis og blandes ikke, for få eller ens døgn giver ingen model, lav `rSquared` for forbrug uden sammenhæng.
- Klient: forespørgslens form, manglende timeværdier udelades, ISO-tider læses som UTC, fejl bliver til koder uden kildens tekst.
- Pipeline med de falske kilder: hældningen genfindes, når det falske forbrug følger den falske temperatur (scenariet `heating`); intet hentes uden position; kun afsluttede timer gemmes; flytning sletter temperaturerne; en fejl hos vejrkilden gemmes og fejler ikke synkroniseringen; en tilknyttet varmepumpe skifter til indetemperatur.
- Frontend: kortet i alle tilstande, punktdiagrammet, indtastning af position.

Status 22. november 2026: 350 backendtest (15 Table Storage-test sprunget over uden Azurite) og 112 frontendtest består. Kortet og indstillingerne er set i en browser mod den kørende løsning med de falske kilder ved 1280 px og 390 px.

Efter afsnit 9: 362 backendtest og 115 frontendtest består.

**Mangler stadig**: et rigtigt svar fra Open-Meteo. Fra udviklingsmiljøet svarede tjenesten kun 429 (dagskvoten for den delte adresse var brugt), så klienten er skrevet efter den dokumenterede svarform og fejlformatet, ikke efter et set svar. Første synkronisering på den rigtige installation viser, om det passer: kortet viser enten tal eller en fejlkode. Om tallene giver mening for sommerhuset, kan først vurderes med rigtige data og nogle ugers indetemperatur.

## 9. Alarmer, der tager højde for udetemperaturen

### Hvad ændres

Reglerne *Lavt forbrug* og *Højt forbrug* sammenlignede hvert døgn med medianen af de foregående 14 komplette døgn. Det giver to fejl i et opvarmet hus:

- En kuldeperiode ligner højt forbrug, selv om huset bare varmer mere.
- Et hus, der holder op med at varme i frostvejr, kan stadig bruge »det sædvanlige« i forhold til en mild uge, og så kommer der ingen alarm.

Nu sammenlignes døgnet med **det forbrug, dets udetemperatur forklarer**, når det kan beregnes pålideligt. Ellers bruges medianen som før.

| | Før | Nu, når forventningen findes |
|---|---|---|
| Sammenlignes med | Median af de foregående 14 døgn | Forventet forbrug ved døgnets udetemperatur |
| Lavt forbrug | Under 50 % af medianen | Under 50 % af det forventede |
| Højt forbrug | Over 200 % af medianen | Over 200 % af det forventede |
| Reglen er slået fra, når | Medianen er under 3 kWh | Det forventede er under 3 kWh |
| Optrapning, kvittering, lukning, beskeder | Uændret | Uændret |

Procenterne og de 3 kWh er de samme indstillinger som før.

### Hvornår findes en forventning

For hvert døgn, der vurderes, lægges linjen fra afsnit 2 gennem de **foregående** op til 90 døgn. Døgnet selv er aldrig med i sin egen forventning. Forventningen bruges kun, når:

- ejendommen har en position, og der er udetemperatur for hver time i døgnet,
- linjen bygger på mindst 30 døgn (`Weather:AlarmMinDays`),
- linjen forklarer mindst halvdelen af udsvingene (`Weather:AlarmMinRSquared`, standard 0,5),
- koldere døgn faktisk koster mere,
- døgnet har en indetemperatur, hvis linjen måles mod varmepumpens indetemperatur,
- reglen har »Tag højde for udetemperaturen« slået til (standard til, også for regler gemt før denne ændring).

Mangler noget af det, vurderes døgnet mod medianen. Det gælder også, når vejrkilden fejler. Et løbende forløb kan derfor have døgn vurderet på hver sin måde; hændelsen viser altid det seneste døgns grundlag.

Udetemperaturen hentes nu **før** reglerne køres, så de nyeste døgn kan vurderes.

### Hvad man ser

- Alarmteksten: `Døgnforbrug 7,0 kWh d. 6/11 er under 50 % af det forventede (16,0 kWh) ved −8,0 °C ude.`
- Under alarmen: `Målt 7,0 kWh · forventet 16,0 kWh ved −8,0 °C ude · grænse 8,0 kWh` i stedet for medianen. Hændelsen gemmer udetemperaturen (`outdoorC`).
- Indstillinger: kontakten »Tag højde for udetemperaturen« på de to regler.
- Kortet »Forbrug og udetemperatur« siger, om sammenhængen er tydelig nok til alarmerne (`dependableForAlarms`).

### Ikke ændret

- Oversigtens »i forhold til median« og den daglige rapports sammenligning er stadig medianen. De kan derfor vise en stor afvigelse et døgn, hvor der med rette ingen alarm er.
- Døgn, der allerede har en hændelse, vurderes ikke igen.

### Begrænsninger

- En ret linje gennem hele målepunktets forbrug er en grov model. I et beboet hus forklarer kulden ofte under halvdelen af udsvingene, og så bruges medianen fortsat.
- Flere afvigende døgn i træk indgår i linjen for de følgende døgn og trækker forventningen i deres retning. Med op til 90 døgn i linjen er virkningen lille, men den findes.
- Tærsklerne (30 døgn, 0,5) er startværdier.

### Test

- Regelmotoren: kuldeperiode uden alarm, mildt døgn uden alarm, normalt udseende døgn der er lavt i kulden, slået fra pr. regel, tilbagefald til median, for lille forventning, løbende forløb på tværs af grundlag.
- Forventningen: kun foregående døgn, tilbageholdt ved for få døgn, svag sammenhæng eller omvendt hældning, og kræver indetemperatur når linjen bruger den.
- Hele løsningen med de falske kilder (scenarierne `cold-snap` og `heating`): en kuldeperiode giver alarm mod medianen, men ikke med kendt udetemperatur; et hus, der bruger 40 % af det forventede i kulden, giver alarm med udetemperaturen og ikke mod medianen; slået fra eller fejlende vejrkilde falder tilbage til medianen.
- Afprøvet i den kørende løsning: ingen alarm for kuldeperioden, og én alarm mod medianen, når kontakten slås fra på reglen.
