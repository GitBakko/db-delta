# DbDelta — Backlog / task list

**Questo file è l'UNICA lista di lavoro del progetto.** Non ne esistono altre.
`CLAUDE.md` lo nomina, la memoria di sessione ci punta e non ne duplica il
contenuto. Chi chiude una voce la depenna QUI, nello stesso commit del codice —
vedi «Manutenzione» in fondo.

## Stato — 2026-08-20

- **v1.0.2 pubblicata** (2026-08-13), «Latest», MSI non firmata allegata.
- **893 test verdi** nei sette progetti che girano senza Docker (Core 517,
  Headless 204, Persistence.Unit 80, Golden 68, Property 12, Architecture 6,
  Shared 6). **Due** dei tre DB-backed vanno **rossi**, non skipped, con Docker
  spento — LiveDb e Cli acceptance, che costruiscono il container in un
  inizializzatore di campo senza rete. Davanti a quel muro la prima domanda è
  `docker ps`. Persistence integration invece **skippa da sé** da `f8df44a`
  (`SqlExecutorTests.cs:27-45` e `:74`), ed è per questo che gira anche nel job
  Windows. `dotnet format --verify-no-changes` esce 0.
- **18 voci aperte** — P1 1 · P2 1 · P3 7 · P4 2 · P5 7 — più **12** in
  «Deciso — NON riaprire». Tutte riverificate sul codice il 2026-08-18, non
  ereditate dai documenti. **Le 4 critiche sono chiuse**, e 6 delle 7 alte:
  vedi P0 e P1. Il conteggio va ricontato, non decrementato a mente: `awk` sulle
  righe di tabella, o si scolla come si era già scollato.
- **CI verde su `3c48735`** (run `32140004994`), entrambi i job. I DB-backed
  aggiungono **68** test ai locali della riga sopra: LiveDb 39, Cli acceptance
  22, Persistence integration 7 su Linux (4 passati + 3 skipped su Windows).
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

Una voce chiusa il 2026-08-20 dal commit che porta questa riga:

| Voce chiusa | Come | Prova |
|---|---|---|
| La griglia non era mai stata misurata a 10k oggetti | Misurata. **Costruzione e prima enumerazione di 10.000 righe: 134 ms. Sei battute nella casella di ricerca: 156 ms, cioè 26 ms per battuta. Raggruppamento: sotto il millisecondo.** I limiti asseriti sono larghi di proposito (10 s): quello che devono prendere è un cambio di **ordine di grandezza**, non un runner di CI occupato | `ResultsGridScaleTests` — 3 test che stampano i numeri misurati nell'output. **Il `Refresh()` a ogni battuta è deciso, non rinviato:** 26 ms su 10.000 righe stanno sotto la soglia di percezione e ben dentro l'intervallo fra due tasti — il debounce non serve, e sta ora in «Deciso — NON riaprire» |
| Il round-trip per kind copriva 4 su 13 | `Every_modelled_kind_survives_a_deploy_and_a_re_read` semina **una di ogni cosa** nella sorgente — schema, tipo alias, table type, tabella con PK/DEFAULT/CHECK/indice unico, vista, funzione, procedura, trigger, sequenza, sinonimo, ruolo, utente — deploya sul target vuoto, rilegge e pretende Identical. **L'asserzione non ha filtri per kind:** è un filtro che aveva lasciato passare i sei reader mai provati, e ciò che la fixture non crea è Identical per assenza comunque | `DependencyRoundTripTests`. **Ha trovato un difetto vero al primo colpo** — vedi la voce qui sotto |
| Una colonna di tipo ALIAS portava `COLLATE` e il deploy si fermava | `sys.columns` riporta una collation per una colonna di tipo alias esattamente come per una `nvarchar`, ma SQL Server rifiuta la clausola: «COLLATE clause cannot be used on user-defined data types». La collation appartiene al **tipo**, non alla colonna. `Column.IsUserDefinedType` arriva da `sys.types.is_user_defined` e i due emittenti che scrivono colonne la saltano. **La regola resta quella di sempre** — COLLATE esplicita su ogni colonna stringa, come Redgate: questa è l'unica eccezione, e non è l'omit-when-default che il proprietario aveva fatto revertire | `AliasTypeColumnTests` — 3 test, di cui uno è il controllo in negativo sulla regola generale e uno copre il secondo emittente (`TableTypeUdtScriptEmitter`, stessa trappola un file più in là). **Sonda di mutazione fatta:** tolta la guardia cadono sia il test di unità sia il round-trip dal vivo. **Nessun test headless o golden poteva vederlo:** serviva un apply vero |
| L'invariante di convergenza copriva 2 kind su 14 | Copre **tutti e tredici**, e non nel posto in cui la voce lo cercava. In `Core` sono 4 — View, Procedure, Function e Trigger, i kind *modulari*, il cui corpo è testo nel modello e si confronta senza un server. Gli altri nove non si possono coprire lì senza scrivere un parser T-SQL: li copre il round-trip dal vivo, che è la stessa domanda — «emetti, rileggi, deve essere Identical» — fatta a un SQL Server vero | `DeployedModuleConvergesTests` (33 test, Core) e `DependencyRoundTripTests.Every_modelled_kind_survives_a_deploy_and_a_re_read` (live). **Sonda di mutazione sul Core:** tolto il taglio dei terminatori ripetuti in `BodyNormalizer` ne cadono 12. Nota: rompendo *solo* l'emittente non cade nessuna convergenza — il normalizzatore la copre — quindi le due metà hanno anche i loro test diretti |

| Voce | Reg. | Sforzo | Evidenza verificata |
|---|---|---|---|
| **Codice morto, resta la metà che è una decisione tua.** Chiuse il 2026-08-20 le parti senza decisioni: Serilog (il file `SerilogBootstrap` non aveva un solo chiamante e portava tre `PackageReference` nella CLI pubblicata) e le tre interfacce **senza consumatori** — `IProjectStore`, `ISchemaSource`, `IScriptEmitter`: una sola implementazione a testa, nessuno che le prenda come parametro o le tenga come campo. Restano le **6 view irraggiungibili**, e con loro la decisione: le connessioni si autosalvano a ogni compare riuscito e alimentano gli «Usati di recente», quindi finché il connection manager resta irraggiungibile quella lista cresce e nessuno può potarla — **o si lega un pulsante, o si cancella tutto**. `ConnectionPickerView` tiene per giunta la stringa di connessione intera, password compresa, in una `TextBox` normale, che l'albero UIA pubblica in chiaro: oggi non è raggiungibile, quindi non è un'esposizione, ma il giorno che qualcuno la lega lo diventa | 2026-08-14 | M | `Views/ConnectionPickerView.axaml`, `Views/ResultsTreeView.axaml`; `OpenConnectionManagerAsync` referenziato solo da `MainWindowViewModel.cs:404` |
| **`ComparisonOptions`: 20 flag dichiarati, 6 letti.** Più `ProjectOptions`, owner/table mappings che non raggiungono alcun motore, parser `.dbd` v1 legacy. **Assorbe `IgnoreConstraintNames`**, che è morto: deciderlo da solo significa scegliere al posto di questa voce per tutti e 14 | 2026-08-14 | M | `Core/Options/ComparisonOptions.cs:10-37`; grep `HasFlag` → 6 occorrenze in tutto; `DbDeltaProject.cs:19-34` |
| **Tassonomia degli avvisi di deploy mai implementata** (`DeployRisk`, `--abort-on-warnings`). Due fette parziali sono atterrate e non la chiudono. **La metà documentale è chiusa il 2026-08-20:** i quattro `docs/0*.md` portano ora in testa un riquadro che dice cosa descrivono — Redgate, non DbDelta — e il README non li linka più come documentazione nostra. Chi legge §9.4 lo sa prima di arrivare allo switch. Quello che resta è una **funzione mai chiesta da nessuno**, non un difetto | 2026-07-30 | M | grep `AbortOnWarnings\|DeployRisk` → zero; il riquadro in `docs/01_architecture.md:3-11` |
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
| I quattro segmenti di `Synonym` erano campi morti | Cancellati, insieme al parser che li riempiva. Nessuno li leggeva: l'emittente scrive `BaseObjectName` verbatim e il confronto legge quella stessa stringa. Il difetto registrato — il parser non faceva un-escape di `]]` — era un difetto di codice morto, e la chiusura più corta era toglierlo | `Synonym.cs` porta ora tre membri. `M5KindsTests`, `ScriptGeneratorOrphanedKindsTests` e i golden restano verdi senza toccare un'asserzione: la prova che nessuno leggeva quei campi |
| Quattro test lasciavano il tema dell'applicazione come l'avevano trovato | `Application.RequestedThemeVariant` è un'impostazione di processo e ciclare il tema la scrive; un solo test la ripristinava, a mano, in un `try/finally`. Ora la cattura il costruttore e la rimette il `Dispose` che la classe già aveva, per tutti | `ThemeCycleTests`. **Non c'è un test che lo guardi**, e sarebbe finto: il difetto è invisibile finché nessun altro test legge un brush dipendente dal tema — è il motivo per cui non aveva ancora morso |
| Il censimento non aveva un'asserzione sull'output CLI | Asserito **attraverso il confine di processo**, senza aprire gli internals: l'acceptance lancia il binario e legge lo stdout. **Ha trovato un difetto che non era nella voce:** il testo italiano usciva nella code page della console, quindi `dbdelta compare > out.txt` scriveva byte che nessun lettore UTF-8 decodifica — una «à» diventava un carattere di sostituzione. La CLI ora fissa `Console.OutputEncoding` a UTF-8 (protetto: un host senza console alza `IOException`) e l'harness legge con la stessa codifica | `CompareCommandTests.The_text_output_declares_what_the_comparison_did_not_examine`. **Sonda di mutazione fatta:** spento il blocco che scrive il caveat in `TextFormatter`, il test cade |
| Il parser delle risposte UDP del browser SQL era permissivo | È l'unico punto del codice che legge byte non nostri — un datagramma da chiunque risponda per primo a un broadcast — e quello che restituisce finisce in un elenco che l'utente clicca, e da lì in una stringa di connessione. Ora rispetta la **lunghezza dichiarata** nell'header invece di leggere fino in fondo al datagramma, i nomi devono somigliare a nomi (allow-list `[A-Za-z0-9.-_$#]`, 128 caratteri per il server e 16 per l'istanza) e una sola risposta non può riempire l'elenco da sola (tetto 64) | `SqlBrowserResponseTests` — 12 test, di cui 2 controlli in negativo (un nome con punti e trattini passa; un pacchetto che non è una risposta non produce nulla). **Tre sonde di mutazione, tre test distinti caduti.** Due sonde precedenti sono state buttate: non compilavano (`IDE0059`, `IDE0051`) e il `--no-build` mostrava il verde dell'assembly di prima. Il parser è ora `internal` e `DbDelta.Persistence` apre gli internals anche agli unit test: è un parser di byte, non ha bisogno di un container |
| La regola DRY era violata dal codice che la governa | Sette delle otto copie inline di icona+etichetta sono ora `Views/Controls/IconButtonContent.axaml` — 65 righe in meno in `MainWindow.axaml`. Il controllo espone `Geometry` e `Text` **più** `IconSize` e `StrokeThickness`: le tre famiglie di pulsanti erano disegnate a 16/14/13 px di proposito, e schiacciarle su un valore solo sarebbe stato un cambio di design nascosto dentro un refactor. **L'ottava resta inline ed è l'eccezione dichiarata**: «Allinea destinazione» ha un triangolo pieno senza contorno e l'etichetta SemiBold in `PrimaryFgBrush` | `IconButtonContentTests` — 3 test, di cui uno è il controllo in negativo sui tre pesi. **Sonda di mutazione:** con un `Data` fisso nel template i primi due test passavano lo stesso — l'asserzione è stata stretta finché non ha visto la differenza (`BeSameAs`), e allora la sonda è caduta. Il guscio va costruito **con un confronto pubblicato**, altrimenti la barra azioni non entra nell'albero visuale e se ne contano cinque su sette |
| Gli invarianti UI non erano asseriti in sé | `UiInvariantTests` — 13 test — chiede alle **brush**, non ai nomi delle classi: `.ghost` non vuol più dire trasparente e il nome è l'unica cosa di cui non fidarsi. Copre la regola #1 su tutte e undici le classi di pulsante e la regola #2 sui cinque controlli monoriga. **I due «buchi minori» non erano difetti:** il `CheckBox` non ha uno stile suo ma il default Fluent misura già 32 (ora asserito, quindi se si muove lo si sa), e `SaveProjectDialog` usa `ghost` per Annulla perché la regola #1 dice esattamente questo — `ghost` **è** il neutro pieno, e Annulla è un'azione di utilità | `UiInvariantTests`, più il controllo in negativo su `Button.swap`, che è 36×36 per eccezione dichiarata: senza, la regola dell'altezza si soddisferebbe mettendo 32 su tutto. **Due sonde di mutazione:** rimesso `Transparent` su `.ghost` cade un caso della teoria; portata l'altezza a 28 cade l'altro test |

| Voce | Reg. | Sforzo | Evidenza verificata |
|---|---|---|---|
| **Restano due «Apri», ed è una decisione del proprietario.** I due messaggi d'errore sono stati corretti il 2026-08-20 e sono asseriti da `MainWindowViewModelTests.The_project_errors_say_Carica_and_never_Apri` (sonda di mutazione fatta). Gli altri due sono tooltip che aprono un pannello e un browser: la regola in memoria parla di azioni di **caricamento file**, quindi non li copre di suo. Se la regola è più larga di così, lo dice il proprietario | 2026-05-22 | XS | `Views/MainWindow.axaml:582` («Apri i messaggi del server»), `:587` («Apri la version history nel browser») |
| **Una FK auto-nominata e DISABILITATA continua a portare l'hash della sorgente.** `NOCHECK CONSTRAINT` vuole un nome e l'unico disponibile è quello che il server della sorgente ha coniato; emetterla senza nome e saltare la disabilitazione cambierebbe silenziosamente l'enforcement, che è peggio del churn. Quella forma quindi non converge: due server continueranno a vederla diversa a ogni confronto. Via d'uscita: un nome nostro, cioè una rinomina al deploy — non una clausola più furba | 2026-08-20 | S | `Core/ScriptGen/ForeignKeyScriptEmitter.cs` (`NameClause`, commento `ponytail:`); `SystemNamedForeignKeyTests.A_disabled_auto_named_foreign_key_keeps_its_name` lo fissa |

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
| Debounce sulla ricerca della griglia | 2026-08-20 | **Misurato invece che immaginato**: 26 ms per battuta su 10.000 righe (`ResultsGridScaleTests`), sotto la soglia di percezione e dentro l'intervallo fra due tasti di chi scrive. Il pattern esiste già in `ProjectEndpointPanelViewModel` e si copierebbe in quindici righe, ma toglierebbe un costo che non si sente e aggiungerebbe un test dipendente dal tempo. Riaprire solo se la misura cambia |
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
