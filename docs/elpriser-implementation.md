# Elpriser, tariffer og MCP — implementeringsstatus

Applikationskode implementeret 13. november 2026. Funktionerne er slået fra som standard. Liveaccept er ikke gennemført, og der er ikke udrullet til Azure.

## Beregningskontrakt

- `EnergyMonitor.Core/Pricing` bruger decimalbeløb i DKK/kWh og halvåbne UTC-intervaller. API-datoer er inklusive danske kalenderdage, højst 90 dage pr. forespørgsel.
- Aftaler er daterede og kan være spot eller fastpris. Time-/døgnaftaler bruger det tidsvægtede gennemsnit af spotpriserne i aftalens afregningsinterval. Kvarteraftaler bruger kvarterprisen. Negative priser bevares.
- Fineste måleopløsning vælges pr. dansk dag. En ufuldstændig kvarterserie udfyldes ikke med time-/døgntotaler. Samme interval erstattes ved rettelse; opløsninger lægges aldrig sammen.
- Grovere målinger fordeles efter varighed. Resultatet markeres som estimeret, når fordelingen afhænger af finere afregning eller ændrede prisdele. Kildens estimeringsflag bevares separat fra dækningsgraden.
- Faste beløb indtastes som DKK pr. dansk kalenderdag. En 23-/25-timers dag får samme dagsabonnement; delperioder får deres andel af den konkrete dags varighed. Månedlige abonnementer skal omregnes til dagsbeløb for deres gyldighedsperiode.
- Aftalens energipris, tillæg og leverandørabonnement er ekskl. moms. Tariffer angiver Included, Excluded, Exempt eller Unknown. Momsandelen opgøres separat. Ingen øreafrunding under intervalberegningen.
- Ukendte tillæg er null. Manglende målinger, spotpriser, aftaler, tariffer eller moms giver ingen fuld total. `KnownSubtotal` er kun kendte komponenter. Status og `IsEstimated` kan samtidig være Partial og true.
- Beregnet betyder efter registrerede vilkår; ikke fakturaafstemt.

## Kilder og historik

Energinets [DayAheadPrices](https://api.energidataservice.dk/dataset/DayAheadPrices?limit=2) blev læst uden login. Felterne er TimeUTC, PriceArea, DayAheadPriceDKK. DKK/MWh divideres med 1000. Importøren understøtter kvartermarkedet fra 1. oktober 2025; ældre perioder kræver en anden verificeret importør. DK1/DK2 vælges eksplicit pr. aftale: adresse/postnummer bruges ikke til at gætte prisområdet.

[Eloverbliks aktuelle OpenAPI](https://api.eloverblik.dk/customerapi/swagger/customerapi-v1.0/swagger.json) dokumenterer historiske links med `query: [{meteringPointId, from, to}]`. 404 kan betyde deaktiveret integration. Der faldes da tilbage til `getcharges`; disse aktuelle oplysninger gemmes alene som dokumentation og bruges aldrig bagudrettet.

Historiske svar normaliseres til særskilt lagrede tariftilknytninger og satser efter operatorens verificerede `tariffMappings`. En mapping består af code, owner, type, component (Network/Tax/Subscription), unit (kWh/day), vat (Included/Excluded/Exempt) og UTC-gyldighed. Enheden og moms udledes ikke fra en udokumenteret kode. Linkets faktor og gyldighed skærer prisserien til. Ukendte links udløser manglende tariffer i beregningen. Manuelle satser har forrang for samme kode; kildernes beløb lægges aldrig dobbelt til.

Indstillinger viser aftalehistorik og gyldighedsdato. Historiske rettelser bevarer senere aftaleskift. Tariffer og mappings kan registreres i den avancerede opsætning. `tariffsConfirmed` skal først vælges efter fuld kontrol af tarifinventaret, inklusive afgifter og abonnementer. `requiredTariffCodes` kan fastholde krav til kendte afgiftskoder selv ved kildehuller. Gemte mappings kræver en ny synkronisering før import.

Pris- og tarifsynkronisering følger den eksisterende manuelle/planlagte forbrugssynkronisering efter alarmbehandling. Prisfejl registreres særskilt uden at ændre forbrugssynkroniseringens resultat eller stoppe alarmer. Der genindlæses op til 90 dage ved hver kørsel; rettelser erstatter data. Spotpriser hentes frem til i overmorgen. Kildestatus angiver faktisk returneret spotprisenveloppe; den garanterer ikke hulfrie data. Tariffer får ikke en udokumenteret dækningsperiode.

`Eloverblik:PreferredAggregation=Quarter` kan afprøve kvartersmålinger. Ved 30008 faldes tilbage til Hour, derefter Day. Standard er fortsat Hour, indtil de konkrete målepunkter er afprøvet.

Azure-tabeller oprettes ved brug. Priser og importerede tariffer er opdelt pr. område/bolig og UTC-måned. Store konfigurationer og rå dokumentation deles i mindre rækker pga. Tables grænse på 64 KiB pr. string. Rå dokumentation indeholder kun seneste svar; normaliseret historik bevares. Ved afbrudt konfigurationsskrivning behandles boligen som ukonfigureret. Skift af målepunkt og sletning af boligdata fjerner boligernes prisopsætning, importeret tarifhistorik og kildestatus; delte spotpriser bevares.

## HTTP og MCP

HTTP under `/api/energy`: overview, `{id}/consumption`, costs, compare, breakdown, status, configuration og administratorens charge-evidence. Alle periodiske læsninger deler samme service som MCP. GET configuration kræver læseadgang; PUT kræver admin og den eksisterende CSRF-header.

Det officielle `ModelContextProtocol.AspNetCore` SDK leverer Streamable HTTP på `/mcp`. Kun seks tools registreres: get_energy_overview, get_consumption, get_costs, compare_periods, get_price_breakdown, get_data_status. Consumption accepterer day/interval. Svar indeholder valuta/enhed, tidszone, periode, dækning og mangler; breakdown indeholder anvendte priser, vilkår og importerede links.

MCP kræver separat JWT-bearerautentifikation med signatur, issuer, audience, expiry, læsescope og eksisterende brugerallowlist. Metadata findes på `/.well-known/oauth-protected-resource/mcp` og annonceres i 401-challenge. Cookies og udviklingsheader giver ikke adgang. Brug en OAuth-autoriseringsserver med delegerede API-access tokens for ressourcen og en klientregistrering, som den valgte MCP-klient understøtter. Konfigurér HTTPS-authority, audience, public resource URL og read scope. Browser-OIDC og MCP-provider kan være forskellige, men allowlist skal indeholde MCP-tokenets verificerede subject/oid. Ingen tokens gemmes eller returneres.

NemligMCP/BankMCP findes ikke i den tilgængelige arbejdsmappe, og konkret provider/klient er ikke oplyst. Der er derfor implementeret en konfigurerbar ressource-server; et specifikt loginflow er ikke verificeret.

## Kontroller og resterende accept

Automatiserede kontroller dækker negative priser, moms, dagsabonnementer, DST, kvarter-/time-/døgnmålinger, manglende priser/forbrug/tillæg, tarifspring, målerbytte, genstart og idempotente rettelser i Azurite. HTTP/MCP-paritet og bearer-only adgang kontrolleres med signerede testtokens. Frontendtests dækker delbeløb, estimater, negative totaler, DST-gyldighed og bevaring af fremtidige aftaler. Én kodegennemgang fandt og førte til rettelser af historikredigering, DST, lagringsstørrelse, estimeringsflag og kildestatus. Aspire blev opdateret til 13.6.5 for at afhjælpe NU1903 i Release-buildet.

Følgende kræver fortsat liveaccept:

1. Verificer historiske tarifkoder, enheder, moms, afgifter og kvarteradgang for **hver** bolig med den rigtige Eloverblik-adgang. Ingen boligspecifikke data er verificeret i denne ændring.
2. Vælg og konfigurér OAuth-provider og MCP-klient, registrer API-audience/scope og allowlist-subject, og afprøv interaktivt login, tool discovery og et fuldt beløb.
3. Aktivér priser i dev, registrér verificerede aftaler og mappings, synkronisér, og sammenhold en afsluttet periode med en faktisk elregning. Dokumentér afvigelser før produktionsaktivering.

Planens samlede liveacceptkriterium er derfor endnu ikke opfyldt.

Endelig lokal verifikation: Release-build uden warnings/fejl; 203 backendtests bestået med Azurite (ingen skips); 18 frontendtests, ESLint og produktionsbuild bestået; Docker-image bygget; main/dev/prod Bicep valideret; NuGet-audit og npm-audit for produktionsafhængigheder uden fund. Den indbyggede /code-review-kommando var ikke eksponeret i sessionen; én separat reviewer gennemførte en skrivebeskyttet kodegennemgang som fallback. UI er komponenttestet; der er ikke udført manuel browsertest.
