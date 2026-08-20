# DbDelta — Backlog / task list

**Questo file è l'UNICA lista di lavoro del progetto.** Non ne esistono altre.
`CLAUDE.md` lo nomina, la memoria di sessione ci punta e non ne duplica il
contenuto. Chi chiude una voce la depenna QUI, nello stesso commit del codice —
vedi «Manutenzione» in fondo.

## Stato — 2026-08-20

- **v1.0.2 pubblicata** (2026-08-13), «Latest», MSI non firmata allegata.
- **844 test verdi** nei sette progetti che girano senza Docker (Core 500,
  Headless 184, Persistence.Unit 68, Golden 68, Property 12, Architecture 6,
  Shared 6). **Due** dei tre DB-backed vanno **rossi**, non skipped, con Docker
  spento — LiveDb e Cli acceptance, che costruiscono il container in un
  inizializzatore di campo senza rete. Davanti a quel muro la prima domanda è
  `docker ps`. Persistence integration invece **skippa da sé** da `f8df44a`
  (`SqlExecutorTests.cs:27-45` e `:74`), ed è per questo che gira anche nel job
  Windows. `dotnet format --verify-no-changes` esce 0.
- **27 voci aperte** — P1 1 · P2 1 · P3 10 · P4 8 · P5 7 — più **11** in
  «Deciso — NON riaprire». Tutte riverificate sul codice il 2026-08-18, non
  ereditate dai documenti. **Le 4 critiche sono chiuse**, e 6 delle 7 alte:
  vedi P0 e P1. Il conteggio va ricontato, non decrementato a mente: `awk` sulle
  righe di tabella, o si scolla come si era già scollato.
- **CI verde su `3c48735`** (run `32140004994`), entrambi i job. I DB-backed
  aggiungono **66** test ai locali della riga sopra: LiveDb 38, Cli acceptance
  21, Persistence integration 7 su Linux (4 passati + 3 skipped su Windows).
  Il totale non si scrive: è «i locali + 65», così invecchia un numero solo.
  L'exit code di `script` e la forma JSON di `compare` girano solo lì.
- **La guardia dello skip Testcontainers deve avvolgere `Build()`**, non solo
  `StartAsync()`: `Validate()` alza `ArgumentException` quando il runner non ha
  proprio un endpoint Docker, e il job Windows andava rosso a intermittenza su
  commit che non toccavano quei test. Corretto il 2026-08-18 (`f8df44a`).
- L'hash di `main` e lo stato di origin invecchiano il commit dopo: chiedili a
  `git status -sb` e `git log -1`, non a questo file.

**Gli SHA anteriori al 2026-08-18 non risolvono più.** La storia è stata
riscritta quel giorno per togliere una credenziale, quindi ogni hash citato nei
documenti in `docs/review/` e nei messaggi più vecchi restituisce
`Not a valid object name`. Cerca per messaggio: `git log --oneline --all --grep="…"`.

Le date sono di **registrazione**: quando la voce è stata scritta la prima
volta, non quando è stata verificata. Sforzo: XS = poche righe, S = ore,
M = un giorno, L = giorni.

---

## P0 — Critiche: perdono dati, allargano privilegi o uccidono il processo

**Vuota.** Le quattro voci, tutte registrate il 2026-07-30, sono state chiuse il
2026-08-18 dal commit che porta questa riga.

| Voce chiusa | Come | Prova |
|---|---|---|
| Permesso su oggetto non risolto → GRANT a livello DATABASE | `AppendOnTarget` non tace più su un target senza nome: alza `UnscriptablePermissionException`, sul modello del rifiuto già usato per gli indici. CLI exit 30, banner nell'app | `UnscriptablePermissionRefusalTests` — 3 rifiuti + 2 controlli in negativo (`DATABASE` senza `ON`, oggetto nominato con `ON`) |
| Due permessi con la stessa `ObjectIdentity` uccidono l'app | **Cancellato il join**, non guardato: la lista DTO è una proiezione posizionale della lista grezza (`Mapper.ToDto` è un solo `Select`), quindi la coppia della riga *i* **è** `raw[i]`. Non può lanciare e non può perdere una riga | `DuplicateIdentityRowsTests`. **Sonda di mutazione fatta:** rimessa la `ToDictionary`, il test cade |
| `stackalloc` senza tetto + nomi di dispositivo riservati | Tetto di 100 caratteri sullo stem, niente più buffer dimensionato sull'input, `CON`/`PRN`/`AUX`/`NUL`/`COM0-9`/`LPT0-9` prefissati con `_`, punto finale tolto. Il confronto è sulla parte prima del punto, come fa Windows | `ProjectsFolderTests` — 200.000 caratteri, sei nomi riservati, `NUL.v2`, più due controlli in negativo |
| Le credenziali seguivano l'utente sul server nuovo | `OnServerNameChanged` azzera `UserName`/`Password` **prima** dell'auto-fill DPAPI, che rimette quelle di QUESTO server se esistono. Con i campi vuoti `IsAutoConnectEligible` dice no da sé | `EndpointCredentialResetTests` — azzeramento, `IsValid` che torna false, più il controllo in negativo su Windows auth |

**Non fatto, e dichiarato:** l'irrobustimento del parser UDP di
`SqlServerDiscovery` (valida solo `buffer[0]==0x05`). Il vettore è chiuso dal
lato che conta — nessuna credenziale parte più da sola verso un host suggerito —
ma un pacchetto malformato resta interpretato in modo permissivo. Voce a sé,
non urgente ora che nulla la segue.

---

## Credenziale reale nel repo — 2026-08-18, chiusa

Trovata durante lo smoke: la password `sa` di `192.168.3.243` era un letterale
in `ConnectionStringRedactorTests` — il test della classe che esiste per tenere
le password fuori dai log — e nella copia della stessa riga nel piano M11-bis.
Committata il 2026-05-21, pubblica da allora.

- **Storia riscritta** il 2026-08-18 (`git filter-repo --replace-text`),
  force-push di `main` e dei 13 tag. Clone da zero verificato: **0 occorrenze
  su 404 commit**. Backup pre-riscrittura in
  `D:\tmp\dbdelta-backup\pre-rewrite-20260818-141753.bundle`.
- **Ogni clone esistente va riclonato**, non aggiornato con `pull`: tutti gli
  SHA dal 2026-05-21 in poi sono cambiati. Le Release e le loro MSI sono
  intatte, i tag ripuntano ai commit nuovi.
- **La rotazione della password è stata rifiutata dal proprietario**
  (2026-08-18). La riscrittura toglie il valore dalla storia, non annulla che
  sia stato pubblico per tre mesi, e GitHub trattiene gli oggetti sciolti
  finché non fa GC. Registrato perché sia una scelta, non una dimenticanza.
- Secret scanning e push protection erano **già attivi** e non hanno preso
  nulla: riconoscono formati di chiave di provider noti, non una password
  qualsiasi dentro una stringa di connessione. Non contarci per questa classe.

---

## Smoke dal vivo — 2026-08-18, `.243` e `.242`, sola lettura

Prima sessione in cui qualcuno ha **guardato** l'app invece di dedurla.

| Verifica | Esito |
|---|---|
| Confronto `PcrmV2Pl_test` → `PcrmV2Pl_Badii`, 841 oggetti | 786 identiche / 13 diverse / 24 solo prov. / 18 solo dest., **ripartizione identica all'A/B della 11b**: le ondate 1 e 2 non spostano un verdetto. GUI e CLI concordano |
| **Exit code di `dbdelta script`** | **1** con 13 differenze pendenti. Il 2026-08-16 lo stesso comando sulla stessa coppia usciva **0**: la voce 5 è chiusa sui dati veri, non solo in container |
| Churn dei vincoli auto-nominati nello script (2448 righe) | **0 nomi con hash**, **0 `DROP CONSTRAINT`**. Ma vedi la voce 12 in P1: su questa coppia i nomi coincidono, quindi è un controllo in negativo, non la prova della voce |
| Percorso permessi (`--include-permissions`) | Un solo permesso: `GRANT CONNECT TO [pcrm_ro];` — database-scoped, senza `ON`, e il rifiuto dell'ondata 1 correttamente **non** scatta |
| Banda ambra del censimento, in GUI e in CLI | Vista dal vivo: «Non esaminati: 52 proprietà estese» |
| Dialogo di conferma (voce 3) | Visto: nomina l'oggetto che verrà eliminato, mostra lo script, redige le password nelle due stringhe di connessione, Annulla neutro e Esegui in cremisi |

**Il database di prova si muove sotto i piedi.** Fra il giro delle 09:56 e il
report delle 14:39 i «solo provenienza» sono passati da 24 a 25: alle 14:15
qualcuno ha creato `__Bak20260818_CorrieriAlertRifiuti` in `PcrmV2Pl_test`.
Sembrava un difetto del report e non lo era. Prima di dare la colpa al codice
per un conteggio che non torna fra due esecuzioni, chiedi a `sys.objects` cosa
è nato nel frattempo.

**Due difetti trovati guardando, che nessun test headless poteva vedere:**

1. **Il pannello dello script si apriva in fondo a destra** — UIA lo misurava a
   `HorizontalScrollPercent = 100`, `VerticalScrollPercent = 100` — su una
   finestra larga 620 px e **non ridimensionabile**, dentro un box dentro un
   box: il lettore incontrava la coda dello script con i primi caratteri di
   ogni riga tagliati, e non poteva allargare. È l'unica superficie che esiste
   per dare consenso informato prima di un'operazione irreversibile.
   **Corretto**: finestra a 880 px e ridimensionabile, padding annidato tolto,
   pannello 220 → 420 px, e l'offset azzerato dopo il layout. Riverificato
   sull'app: `Vpercent = 0` e nessuno scorrimento orizzontale.
2. **La `PasswordBox` esponeva il valore in chiaro via UIAutomation** — chiusa
   il 2026-08-20, vedi P4. Il difetto si riproduceva in headless: non serviva
   la GUI, serviva chiedere al peer invece che al pixel.

---

## P1 — Alte: risultato o script sbagliato

Voci chiuse il 2026-08-20, ognuna dal commit che porta la sua riga:

| Voce chiusa | Come | Prova |
|---|---|---|
| Sotto un login a privilegio minimo ogni utente usciva Different | Il NULL di `sys.server_principals` non è più letto come «nessun login»: `authentication_type` (1 INSTANCE / 3 WINDOWS) dice che il login esiste comunque, e il reader lo porta su `DatabaseUser.LoginNameIsHidden`. Un nome che non si può leggere si appaia su «è mappato a un login», mai sul NULL. **Le strutture che confrontano due utenti sono DUE** — `ComparisonEngine.UsersEqual` e `ScriptGenerator.DefaultSchemaIsOnlyDifference` — e passano entrambe da `DatabaseUser.LoginMatches`, così non possono scollarsi. L'emittente **rifiuta** (`UnscriptableUserException`, CLI exit 30, banner nell'app) invece di scrivere `WITHOUT LOGIN` per un utente che un login ce l'ha; il corpo del diff viewer rende un commento e non lancia | `HiddenLoginNameTests` — 7 test, di cui 2 controlli in negativo (`Two_visible_login_names_that_differ_still_flag_the_user_different`, `A_user_genuinely_without_a_login_still_emits_WITHOUT_LOGIN`). `CatalogVisibilityTests.A_login_name_hidden_from_the_reader_does_not_make_the_user_different` (live) lo prova sul meccanismo vero. **Tre sonde di mutazione, tutte cadute**, una per struttura |
| Il rebuild non portava i default nel `_tmp` | Due buchi, una causa. I DEFAULT **nominati** restano di proposito fuori dal `_tmp` (il nome appartiene ancora alla tabella che verrà sostituita), quindi la loro espressione ora viaggia nella `SELECT` della copia. E `ColumnsNeedingABackfillDefault` non taglia più corto sui rebuild: era l'unico caso in cui nessun altro può fornire il valore, e il preflight lo saltava. Un default **inline** non si tocca: `EmitCreate` lo scrive già sulla colonna del `_tmp`, e nominarlo nella INSERT lo ripeterebbe soltanto | `RebuildBackfillTests` — 6 test, di cui 3 controlli in negativo (default inline, colonna nullable, rebuild senza colonne nuove). **Tre sonde di mutazione, tutte cadute**, una per buco più una sulla chiave del backfill. **Non cambia nulla per la CLI:** `BackfillPreflight` è usato solo dall'app (`MainWindowViewModel.cs:1149`), quindi una colonna NOT NULL senza alcun default resta un Msg 515 lì — come prima, e ora dichiarato |
| Le FK auto-nominate non combaciavano mai fra due server | `ForeignKeysQuery` legge `is_system_named` (entrambi i lettori: quello del confronto e quello del diff viewer) e `ConstraintPairing` non esclude più le FK: si appaiano su **cosa vincolano e cosa puntano**, mai sull'hash. `ON DELETE`, `ON UPDATE` e il flag disabled restano fuori dalla chiave — quelle sono «la stessa FK, cambiata». **Le strutture chiavate sul nome erano CINQUE, non tre**: i due delta FK, il set di re-add orchestrato e i due lookup che cercano la FK dell'altro lato col nome di questo. Passano tutte da `ScriptGenerator.MatchFk`, un metodo solo | `SystemNamedForeignKeyTests` — 10 test, di cui 3 controlli in negativo. **Tre sonde di mutazione, tre test distinti caduti**, una per struttura; la terza ha richiesto di stringere il test finché non ha visto la differenza. Provato dal vivo in `ConstraintReaderTests`: un `REFERENCES` inline torna `IsSystemNamed = true` con nome `FK__Righe__…` |

Tre voci chiuse il 2026-08-18:

| Voce chiusa | Come | Prova |
|---|---|---|
| Terza copia della regex password rotta | Il template si costruisce col `SqlConnectionStringBuilder`, in **entrambe** le direzioni. Il regex si fermava al primo `;`, e un `;` dentro un valore quotato è legale: su `Password='a;b'` il frammento `b'` restava sul disco in chiaro | `PasswordTemplateTests` — 4 test. **Sonda di mutazione fatta:** rimesso il regex, ne cadono 2, e una è quella che asserisce l'assenza |
| `dbdelta script` usciva 0 con differenze pendenti, e `--include-permissions` non arrivava a `Compare` | Stessa regola di `compare` e `report`. E `opts` passato a `Compare`: era innocuo **solo** perché `ComparisonEngine` non legge mai `IgnorePermissions` — lo legge un punto solo, `ScriptGenerator`, e non era scritto da nessuna parte | `ScriptCommandTests` — l'asserzione a `:26` diceva 0 su uno script che crea una tabella, ora dice 1; il test sui DB identici resta il controllo in negativo |
| Quattro `async void` senza rete | La piattaforma lo copre: `Dispatcher.UIThread.UnhandledException` con `Handled = true`, un punto solo invece di quattro `try`. E `_ = SaveAsAsync()` ora è atteso, così il fallimento arriva lì invece di sparire | `UnhandledErrorBannerTests`. **Non coperto:** che il gancio sia davvero installato — l'headless costruisce il proprio `TestApp` e non esegue `OnFrameworkInitializationCompleted`. Va nello smoke dal vivo |

**Non fatto, e dichiarato:** unificare i due contratti JSON. `compare` emette
`{kind, schema, name, status}`, `report` emette `{kind, schemaName, objectName,
status, lastModifiedSource, lastModifiedTarget}`. Collassarli **rompe** gli
script di chi usa la CLI già rilasciata, quindi è una decisione del proprietario
— vedi P5. Nel frattempo la forma di `compare` non è più senza rete:
`Compare_json_keeps_its_published_field_names` la fissa, ed era l'unica delle
due senza test.

| Voce | Reg. | Sforzo | Evidenza verificata |
|---|---|---|---|
| **La voce 12 NON è verificabile sulle istanze reali disponibili**, e non per la coppia sbagliata. Lo smoke del 2026-08-18 ha misurato i nomi auto-generati su tre confronti: `PcrmV2Pl_test` vs `PcrmV2Pl_Badii` (stesso server) e `PcrmV2Pl_test` (.243) vs `PcrmV2Pl` (.242) danno **24 PK auto-nominati su 24 con nome IDENTICO**, e 45 DEFAULT su 45 idem. Il motivo è strutturale: **un restore conserva gli `object_id`**, quindi ogni database che discende da un backup di un altro porta gli stessi hash, e l'appaiamento per nome — quello rotto — lì funzionerebbe comunque. La divergenza nasce solo deployando lo **script** dello schema due volte. Servono due DB usa-e-getta: stessa proposta già scartata per la 11b, quindi è una decisione del proprietario, non una svista | 2026-07-31 (impegno) · 2026-08-17 (voce 12) | S | Misurato: `sys.key_constraints`/`sys.default_constraints` con `is_system_named=1` sui tre database. Nessun altro DB su `.243` ha vincoli auto-nominati in numero utile (`PartnerCrmCashGlobo` 0, `PartnerCrmEconocom` 1, `TnTrace*` 0, `BetamedV2` 0) |

---

## P2 — Valore alto, sforzo contenuto: da fare per prime a parità di gravità

Voci chiuse il 2026-08-20, ognuna dal commit che porta la sua riga:

| Voce chiusa | Come | Prova |
|---|---|---|
| La Release non allegava né hash né attestazione | `Get-FileHash -Algorithm SHA256` scrive `…msi.sha256` nel formato che `sha256sum -c` legge (due spazi, nome nudo, LF — `Set-Content` avrebbe scritto CRLF), e `actions/attest-build-provenance@v4` firma lo stesso file. Il job prende `id-token: write` e `attestations: write`. Entrambi i passi stanno **dopo** lo smoke install, cioè dove andrà la firma: firmare riscrive il file | `.github/workflows/release.yml:12-18` (permessi), `:66-81` (hash + attestazione + entrambi i file allegati). Gira solo su un tag: non c'è CI che lo eserciti prima |
| Il confronto girava sul thread UI | `engine.Compare` non prende un token e stava in linea: su un catalogo grosso la finestra si bloccava per tutta la durata del diff, **Annulla compreso**. Ora sta su `Task.Run` e il token è ricontrollato all'uscita, così un annullamento durante il diff butta il risultato invece di pubblicarlo. Filare il token DENTRO `Compare` resta fuori: la firma è condivisa con i tre comandi CLI | `ViewModels/AppStateViewModel.cs:355-364`. **Non coperto, e dichiarato:** nessun test headless osserva su quale thread gira il diff — servirebbe un server vivo o un seam che esiste solo per il test |
| Cinque header promettevano un ordinamento che non esisteva | In una `DataGridTemplateColumn` `CanUserSort` da solo disegna la freccia e non ordina niente: manca `SortMemberPath`, che è l'unica cosa che dice **su cosa**. Aggiunto ai cinque. Le due colonne di data puntano al `DateTime`, **non** alla stringa `dd/MM/yyyy` che la cella stampa: come testo `31/12/2025` viene dopo `01/02/2026` | `ResultsGridSortTests` — 4 test headless: uno è l'invariante su OGNI colonna che offre un sort, quindi vale anche per le colonne future, e uno è il controllo in negativo sulla colonna checkbox. **Sonda di mutazione fatta:** puntando la data alla stringa stampata cade il test della data, e solo quello |
| Le docs mentivano in cinque punti | Il blocco di stato di questo file era già stato rimesso in pari il 2026-08-18. Restavano: il sito che non nominava mai la MSI (`getting-started.md` mandava tutti a compilare da sorgente), il README che linkava le note Redgate come «Architecture», la sezione Blazor morta in `CONTRIBUTING.md`, e `cli.md` che dichiarava `apply` «GO-split inside a single transaction». **Quest'ultima era la peggiore**: la transazione è decisa per script, e uno che se la apre da sé viene lasciato fare — avvolgerlo porta `@@TRANCOUNT` a 2 e il suo `COMMIT` diventa un decremento. Ora la tabella nomina i tre esiti (`script` / `client` / `none`) come li nomina l'output JSON. Trovate e corrette anche due bugie che l'elenco non aveva: «Renovate-bot opens PRs weekly» (nessun renovate.json nel repo) e «CONTRIBUTING.md (coming soon)» (esiste dal 2026-05) | `README.md`, `CONTRIBUTING.md`, `docfx/articles/cli.md`, `docfx/articles/getting-started.md`, `docfx/index.md`. **Le quattro `docs/0*.md` restano dove sono**: sono note su Redgate, non su DbDelta, e il README ora lo dice invece di linkarle come documentazione nostra |
| Nessun dialogo rispondeva a Invio/Esc | `IsCancel` su ogni pulsante di uscita (nove dialoghi) e `IsDefault` su quello che conferma, dove confermare non distrugge niente. **`ConfirmDialog` e `ConfirmExecuteDialog` restano senza `IsDefault` di proposito**: uscire da una conferma è gratis, entrarci la esegue. I due gestori scritti a mano sono cancellati — quello di `ConfirmDialog` intercettava Esc nel costruttore, quello di `SaveProjectDialog` rispondeva solo mentre la casella di testo aveva il fuoco | `DialogKeyboardTests` — 5 test headless: l'invariante su TUTTI e nove i dialoghi, due controlli in negativo (nessun doppio default, niente default sulle due conferme distruttive) e due funzionali. **Due sonde di mutazione:** togliendo `IsCancel` cade l'Esc, togliendo `IsDefault` cade l'Invio — ed è quest'ultima a provare che a rispondere è l'attributo e non un resto del gestore cancellato |

| Voce | Reg. | Sforzo | Evidenza verificata |
|---|---|---|---|
| **Resta la virtualizzazione dei pannelli diff.** Il taglio di prefisso e suffisso comuni prima della tabella LCS è chiuso il 2026-08-18: un corpo da 30.000 righe cambiato in una riga si confronta senza allocare nulla di grosso (prima: `int[m+1,n+1]`, ~3,6 GB in un colpo). I tre `ItemsPanel` non virtualizzanti restano, e aspettano la voce 10 | 2026-08-14 | S | `DiffViewerView.axaml:152-156, 193-195, 229-235` |

---

## P3 — Debito strutturale

| Voce | Reg. | Sforzo | Evidenza verificata |
|---|---|---|---|
| **~1.500 righe morte**: 6 view irraggiungibili, connection manager senza binding, Serilog mai chiamato con tre `PackageReference`, 3 interfacce senza consumatori. **Dentro c'è una decisione del proprietario**: le connessioni si autosalvano a ogni compare riuscito e alimentano gli «Usati di recente», quindi finché il manager resta irraggiungibile quella lista cresce e nessuno può potarla — o si lega un pulsante, o si cancella tutto. **Un motivo in più per cancellare:** `ConnectionPickerView` tiene la stringa di connessione intera — password compresa — in una `TextBox` normale, che l'albero UIA pubblica in chiaro. Oggi non è raggiungibile, quindi non è un'esposizione; il giorno che qualcuno la lega a un pulsante lo diventa | 2026-08-14 | M | `Views/ConnectionPickerView.axaml`, `Views/ResultsTreeView.axaml`, `Cli/Logging/SerilogBootstrap.cs`, `Cli.csproj:13-15`; `OpenConnectionManagerAsync` referenziato solo da `MainWindowViewModel.cs:404` |
| **`ComparisonOptions`: 20 flag dichiarati, 6 letti.** Più `ProjectOptions`, owner/table mappings che non raggiungono alcun motore, parser `.dbd` v1 legacy. **Assorbe `IgnoreConstraintNames`**, che è morto: deciderlo da solo significa scegliere al posto di questa voce per tutti e 14 | 2026-08-14 | M | `Core/Options/ComparisonOptions.cs:10-37`; grep `HasFlag` → 6 occorrenze in tutto; `DbDeltaProject.cs:19-34` |
| **Tassonomia degli avvisi di deploy mai implementata** (`DeployRisk`, `--abort-on-warnings`). Due fette parziali sono atterrate e non la chiudono. **Il difetto più concreto oggi è documentale:** chi scrive una pipeline CI leggendo `docs/01_architecture.md` §9.4 usa uno switch che non esiste | 2026-07-30 | M | grep `AbortOnWarnings\|DeployRisk` → zero; `docs/01_architecture.md:225`, `:1152-1154`, `:1480-1481` |
| **La griglia non è mai stata misurata a 10k oggetti**, e il motore emette una coppia per ogni oggetto, Identical inclusi. Da fare **dopo** la ricerca e il rebuild della griglia: misurare prima significa cronometrare un difetto già noto. **Assorbe il `Refresh()` a ogni battuta**, che fino al 2026-08-20 stava nella voce P2 della griglia insieme al sort: un refresh per tasto è un predicato per riga, e il debounce esiste già in `ProjectEndpointPanelViewModel` — ma quel costo non è **mai stato misurato**, e la misura è questa voce. Il debounce va dopo il numero, non prima | 2026-07-30 | M | `MainWindowViewModel.cs:639-641`, `:601-620`; nessun test di scala |
| **Il round-trip per kind copre 4 su 13** (Table, View, Function, Procedure), e uno dei tre test gira solo nella matrice notturna. Sei reader non sono mai passati da un apply vero. **Trappola:** i filtri `.Where` non si cancellano e basta, i commenti sopra `SeededDrift` spiegano perché esistono | 2026-07-30 | M | `DependencyRoundTripTests.cs:48`; `CompatMatrixTests.cs:104-107`; `CompressionRoundTripTests.cs:45-68` |
| **L'invariante di convergenza copre 2 emitter su 14** (View, Procedure). L'esempio più fresco della regola non seguita è la voce 12: ha cambiato come `TableScriptEmitter` scrive i vincoli, ha aggiunto 17 test, e nessuno è «emetti, rileggi, deve essere Identical» | 2026-08-01 | M | `Core.UnitTests/Diff/DeployedModuleConvergesTests.cs:50` e `:67`; `git show --stat 142fcb7` |
| **Gli indici su viste indicizzate sono invisibili**, non solo non scriptabili: due database che differiscono solo per quello escono Identical. Media e non alta **solo grazie al censimento**, che almeno lo dichiara. Non è cambiare un JOIN: vanno appesi a un `View`, che oggi non ha un contenitore di indici | 2026-07-30 | L | `Providers.LiveDb/Readers/IndexReader.cs:44-45`; `UnexaminedReader.cs:50-58` |
| **`LiveDbObjectBodyResolver`, 714 righe**, apre due connessioni per clic e fino a 16 query. Lo switch non ha case per `TableType` né `Schema`: **quei due pannelli sono vuoti oggi, e costa un case in più** — falla anche se i giorni per la riscrittura non ci sono. Prerequisito della virtualizzazione del diff viewer | 2026-08-14 | L | `Providers.LiveDb/ObjectBody/LiveDbObjectBodyResolver.cs:33`, `:35-47`, `:227-271` |
| **Undo dopo un commit riuscito**: nessun down script, nessun journal, nessun backup COPY_ONLY. Rinvio deliberato del proprietario (2026-08-01). Abbassata a media: il percorso distruttivo è tutto sotto consenso e dal commit `c73583c` il dialogo elenca per nome ciò che verrà droppato. Manca la rete di recupero **dopo**, che è debito strutturale, non perdita spontanea | 2026-07-30 | L | grep `COPY_ONLY\|DownScript\|DeployJournal` → zero; `docs/review/2026-07-30-undo-architecture.md` |
| **Parità Redgate ferma a 17 scenari** dal 2026-05-28: mancano DROP in topologia inversa con schemabound, indici filtrati/columnstore, CHECK cross-tabella, extended properties. Serve un server vivo e la GUI Redgate (la CLI è license-blocked, exit 35) | 2026-05-28 | L | `tests/Fixtures/Parity/01-source.sql` → 17 scenari; ultimo audit `docs/parity/redgate-2026-05-28.md` |

---

## P4 — Igiene

Una voce chiusa il 2026-08-20 dal commit che porta questa riga:

| Voce chiusa | Come | Prova |
|---|---|---|
| La `PasswordBox` esponeva il valore in chiaro via UIAutomation | `PasswordChar` maschera i pixel; l'albero UIA è una **seconda superficie** sullo stesso controllo, e il peer standard pubblicava `Text` attraverso `IValueProvider`. Ora la casella è una `MaskedTextBox` col proprio peer, che pubblica i pallini — non `null`: togliere del tutto il pattern nasconderebbe la password e insieme il campo a uno screen reader | `PasswordBoxTests` — il difetto si riproduce **in headless**, senza pilotare la GUI: il peer restituiva `hunter2`. Due test, di cui uno è il controllo in negativo sull'accessibilità. **Sonda di mutazione fatta:** rimessa la `TextBox` normale, cadono entrambi |

| Voce | Reg. | Sforzo | Evidenza verificata |
|---|---|---|---|
| **«Apri» invece di «Carica» in quattro punti**, non due. Due sono messaggi d'errore su un progetto da caricare e vanno cambiati; **due sono tooltip di apertura pannello e browser, dove «Apri» è semanticamente giusto — chiedere al proprietario** se la regola li include | 2026-05-22 | XS | `ViewModels/MainWindowViewModel.cs:367`, `:802`; `Views/MainWindow.axaml:582`, `:587` |
| **Il censimento non ha un'asserzione sull'output CLI.** `TextFormatter` è `internal` e la CLI tiene gli internals chiusi apposta: **non aprirla con `InternalsVisibleTo`**, asserisci sullo stdout dentro un'acceptance che già gira | 2026-08-16 | XS | `Cli/Output/TextFormatter.cs:9` e `:31-35`; `CompareCommandTests.cs:322-323` documenta la scelta |
| **Quattro test mutano `Application.Current.RequestedThemeVariant` senza ripristinarlo.** Non ha ancora morso perché nessun altro test legge un brush dipendente dal tema. La classe implementa già `IDisposable` | 2026-08-14 | XS | `ThemeCycleTests.cs:47-59, 74-86, 138-146, 151-163` contro `:121-134` |
| **`SynonymReader` non fa un-escape di `]]`** — ma la conseguenza raccontata non esiste: i quattro segmenti sono campi morti e l'emittente usa `BaseObjectName` verbatim. **La chiusura più corta è cancellare i campi**, non gestire il `]]` | 2026-07-30 | XS | `Providers.LiveDb/Readers/SynonymReader.cs:52`; `SynonymScriptEmitter.cs:14` |
| **La regola DRY dell'app è violata dal codice che governa:** il markup icona+etichetta dei pulsanti (`<StackPanel Horizontal><Path/><TextBlock/>`) è inline **8 volte** in `Views/MainWindow.axaml`, e la regola dice «prima della seconda copia». O si estrae un `Views/Controls/IconButtonContent.axaml` con due proprietà (`Geometry`, `Text`), o si scrive l'eccezione come definitiva. Trovato dall'audit di allineamento del 2026-08-18, non da una lettura del codice | 2026-08-18 | S | `Views/MainWindow.axaml` righe 100, 123, 142, 203, 218, 475, 489, 502; regola in `src/DbDelta.App.Avalonia/CLAUDE.md` §3 |
| **Una FK auto-nominata e DISABILITATA continua a portare l'hash della sorgente.** `NOCHECK CONSTRAINT` vuole un nome e l'unico disponibile è quello che il server della sorgente ha coniato; emetterla senza nome e saltare la disabilitazione cambierebbe silenziosamente l'enforcement, che è peggio del churn. Quella forma quindi non converge: due server continueranno a vederla diversa a ogni confronto. Via d'uscita: un nome nostro, cioè una rinomina al deploy — non una clausola più furba | 2026-08-20 | S | `Core/ScriptGen/ForeignKeyScriptEmitter.cs` (`NameClause`, commento `ponytail:`); `SystemNamedForeignKeyTests.A_disabled_auto_named_foreign_key_keeps_its_name` lo fissa |
| **Il parser delle risposte UDP del browser SQL è permissivo:** valida solo `buffer[0] == 0x05` e poi si fida della stringa. Scorporata dalla voce P0 sulle credenziali quando quella è stata chiusa: nulla parte più da sola verso un host suggerito, quindi resta igiene, non esposizione | 2026-07-30 | S | `Persistence/Sql/SqlServerDiscovery.cs:196-199` |
| **Gli invarianti UI non sono asseriti in sé.** `Themes.axaml` è coperto da `AccentBandContrastTests` dal 2026-08-01, ma nulla fallisce se domani qualcuno rimette `Background=Transparent` su `.ghost` o toglie il `MinHeight` 32. Due buchi minori: manca uno stile CheckBox, e `SaveProjectDialog.axaml:31` usa `ghost` per Annulla | 2026-07-30 | S | `Styles/AppStyles.axaml:6-22` e `:91-105`; grep `Tokens.axaml` su `tests/` → zero |

---

## P5 — Del proprietario o non verificabile da soli

| Voce | Reg. | Sforzo | Stato reale |
|---|---|---|---|
| **Resta da vedere girare il pulsante Annulla.** Il dialogo di conferma e la banda ambra del censimento sono stati visti dal vivo il 2026-08-18. Annulla no: su `PcrmV2Pl_test` → `PcrmV2Pl_Badii` il confronto dura 2,6 s e il pulsante non è raggiungibile. Il secondo ingresso nel catch di annullamento — la lettura interrotta a metà, che il driver riporta come `SqlException -2` — lo può produrre solo un server lento o un catalogo enorme. **Il banner di rifiuto NON è qui**: sta in «Deciso — NON riaprire», perché la prova richiederebbe due DB seminati apposta e il proprietario l'ha scartata | 2026-08-16 | S | Serve una coppia grossa o lenta, non queste |
| **I 300 s non sono mai stati misurati contro un server lento.** Via d'uscita già provata: l'utente può scrivere `Command Timeout=0` e la stringa torna intatta. Candidate a sforare: lettura colonne e lettura indici | 2026-08-17 | S | `ConnectionFactory.cs:27`; `ConnectionTimeoutTests.cs` non tocca alcun container |
| **Code signing** — bloccato sul certificato, unico blocco vero. Lo step va **fra «Build MSI» e «Smoke install»**, altrimenti lo smoke esercita un artefatto diverso da quello pubblicato. Con `signtool` servono `/fd sha256` e un timestamp server | 2026-05-28 | M | Nessun passo di firma in `release.yml` |
| **Annuncio pubblico** — il draft è completo ma **fermo a 1.0.1 mentre la release è 1.0.2**. Da fare dopo le docs (P2): oggi `docfx/articles/getting-started.md` manda i nuovi arrivati a compilare da sorgente invece di scaricare la MSI | 2026-05-28 | S | `docs/announcements/v1.0.1-draft.md`; `README.md:16-33` |
| **Decisione: unificare i due contratti JSON della CLI?** `compare` dice `schema`/`name`, `report` dice `schemaName`/`objectName` e porta le due date. Collassarli su un solo generatore cancella ~30 righe e lascia un contratto solo — ma **rompe** chi già consuma `compare --format json` dalla CLI rilasciata. Alternativa: tenerli e documentare la differenza. Entrambe le forme sono ora fissate da un test | 2026-08-14 | S | `Cli/Output/JsonFormatter.cs` contro `Shared/Reports/JsonReportGenerator.cs` |
| **Sezione D (parking-lot v2) ancora «BRAINSTORM PENDING»** dal 2026-05-28, nessuna sessione ha lasciato traccia. Copre: provider Scripts-Folder/Snapshot/Source-Control, migration script, kind Tier-3, estensioni SSMS/VS, OTel, auto-update, CLI Linux/macOS. **Tutte bloccate su questo brainstorming** | 2026-05-28 | M | Ultima spec in `docs/superpowers/specs/` → 2026-06-04 |
| **Trimming mai attivato** e il motivo del rinvio regge: `XmlSerializer` è ancora costruito per riflessione, un trim spezzerebbe il salvataggio dei `.dbd`. **Assorbe la voce «MSI 94 MB»**: erano la stessa leva vista da causa e sintomo. Il runtime condiviso pretenderebbe .NET 10 preinstallato, che è il costo che la scelta evita | 2026-05-25 | M | `release.yml:39` e `:42` (`--self-contained true`); `Persistence/Xml/XmlProjectStore.cs:337` |

---

## Deciso — NON riaprire

Verificate il 2026-08-18 e riclassificate: il comportamento è quello descritto,
ma è una **scelta difesa nel codice o negli handoff**, non un difetto. Sono qui
perché una sessione futura non le riscopra come nuove.

| Voce | Reg. | Perché resta |
|---|---|---|
| Sovra-rilevazione di `BEGIN TRANSACTION` | 2026-07-30 | Doc-comment, test che l'asserisce come limite noto, e dal 2026-07-30 la provenienza viene prima del regex (`-- dbdelta:transaction=script`). Residuo solo sugli script **estranei** applicati con `apply` |
| Nessun Annulla sull'esecuzione, cap 600 s | 2026-07-30 | Remarks di venti righe sulla costante + tabella «Accettato consapevolmente». `f49b728` ha reso annullabile il **compare**, non l'esecuzione. La CLI ha già `--command-timeout 0` |
| DROP di uno schema non spuntato → Msg 3729 | 2026-07-30 | `EmitSchemaDrops` è guidato dalla selezione e il doc-comment lo dichiara: fallimento rumoroso dal server, non danno silenzioso. Cambiarlo è una decisione di prodotto |
| Freccia «più recente» fra orologi di server diversi | 2026-08-14 | Il remark XML documenta e accetta il limite, `HasComparableDates` esclude i casi senza vincitore. **Il disclaimer mal collocato lo chiude la voce tooltip in P2** |
| Emissione di un columnstore | 2026-08-14 | Il rifiuto è cablato in tre emittenti, mappato su exit code CLI e mostrato come rifiuto nell'app: la perdita silenziosa è già fermata. L'emissione è funzione mancante dichiarata |
| Il rifiuto scatta solo sul lato sorgente | 2026-08-16 | `EmitRebuild` itera su `newT.Indexes` e `EmitDrop` è esente di proposito: se solo il target ha l'indice, la convergenza lo vuole via |
| Rifiuto non provato su `.243`/`.242` | 2026-08-16 | Proposto e **scartato dal proprietario**: quei DB non portano indici non rowstore, l'acceptance copre lo scenario su un SQL Server 2022 vero |
| Compressione per-partizione | 2026-07-31 | `TOP 1` per `partition_number` con commento che spiega perché; accettata per iscritto due volte |
| Cosmetica non allineata a Redgate | 2026-05-28 | `CREATE OR ALTER`, `[X_tmp]`, `IDENTITY(1,1)`, `xp_logevent`: il proprietario **deselezionò** l'allineamento |
| `ponytail:` scansione lineare in `ConstraintPairing` | 2026-08-17 | Debito tracciato col soffitto e la via d'uscita già scritti nel commento |
| Kind Tier-3 assenti | 2026-05-28 | Fuori scope v1, e dal 2026-08-14 il silenzio non è più silenzioso: `UnexaminedCensus` li dichiara |

**Le 17 proposte scartate con motivo** stanno in
`docs/review/2026-08-14-improvement-scan.md`, Parte 3. Valgono quanto la lista:
evitano di riscoprire come nuove cose già valutate e respinte.

---

## Manutenzione — regola di allineamento

`docs/BACKLOG.md`, la memoria di sessione e `CLAUDE.md` **raccontano la stessa
storia**. Concretamente:

1. **Una voce si chiude QUI, nello stesso commit del codice** che la chiude.
   Mai «lo aggiorno dopo».
2. **Nessun altro file tiene una lista di lavoro.** Gli handoff in
   `docs/review/` restano come *storia* — perché una cosa è stata fatta così,
   quali trappole sono state pagate — e non vanno letti come stato corrente.
3. **La memoria di sessione punta a questo file e non ne copia il contenuto.**
   Tiene solo ciò che il repo non può dire: decisioni del proprietario,
   trappole d'ambiente, cadenza di lavoro.
4. **Un blocco di stato invecchia in giorni.** Se una riga qui nomina un hash,
   una versione o un conteggio di test, va riverificata prima di crederle:
   `git status -sb`, `git log -1`, il CHANGELOG.
5. **Una voce non si depenna senza evidenza.** «Fatto» significa `file:riga` o
   un commit, non un ricordo — questa lista è stata riscritta il 2026-08-18
   proprio perché 14 voci su 58 descrivevano uno stato che il codice non aveva
   più.

Cadenza di lavoro, trappole d'ambiente e decisioni del proprietario: memoria di
sessione. Storia e motivazioni: `docs/review/`.
