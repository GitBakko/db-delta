# DbDelta — Backlog / task list

**Questo file è l'UNICA lista di lavoro del progetto.** Non ne esistono altre.
`CLAUDE.md` lo nomina, la memoria di sessione ci punta e non ne duplica il
contenuto. Chi chiude una voce la depenna QUI, nello stesso commit del codice —
vedi «Manutenzione» in fondo.

## Stato — 2026-08-18

- **v1.0.2 pubblicata** (2026-08-13), «Latest», MSI non firmata allegata.
- **795 test verdi** nei sette progetti che girano senza Docker (Core 473,
  Headless 165, Persistence.Unit 65, Golden 68, Property 12, Architecture 6,
  Shared 6). I tre DB-backed (LiveDb, Cli acceptance, Persistence integration)
  vogliono Docker e vanno **rossi**, non skipped, quando è spento: davanti a
  quel muro la prima domanda è `docker ps`. `dotnet format --verify-no-changes`
  esce 0.
- **49 voci aperte, verificate una per una sul codice del 2026-08-18**, non
  ereditate dai documenti: 33 confermate, 3 parziali, 5 non verificabili senza
  il proprietario o un server vero, 14 riclassificate come scelte deliberate e
  spostate in fondo. **Le 4 critiche sono chiuse**, e 3 delle 7 alte — vedi P0
  e P1.
- **CI verde su `56e2889`**, entrambi i job: **860 test** in tutto, cioè i 795
  locali più i DB-backed (LiveDb 37, Cli acceptance 21, Persistence integration
  7 su Linux, 4+3 skipped su Windows). Le due asserzioni di acceptance toccate
  in questa ondata — l'exit code di `script` e la forma JSON di `compare` —
  girano solo lì, e lì sono passate.
- **La guardia dello skip Testcontainers deve avvolgere `Build()`**, non solo
  `StartAsync()`: `Validate()` alza `ArgumentException` quando il runner non ha
  proprio un endpoint Docker, e il job Windows andava rosso a intermittenza su
  commit che non toccavano quei test. Corretto il 2026-08-18 (`56e2889`).
- L'hash di `main` e lo stato di origin invecchiano il commit dopo: chiedili a
  `git status -sb` e `git log -1`, non a questo file.

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

## P1 — Alte: risultato o script sbagliato

Tre voci chiuse il 2026-08-18 dal commit che porta questa riga:

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
| **Sotto un login a privilegio minimo ogni utente esce Different.** `UserReader` fa LEFT JOIN su `sys.server_principals`: la metadata visibility rende `LoginName` null e `UsersEqual` lo confronta senza trattarlo. Emette `CREATE USER … FOR LOGIN` per utenti già corretti | 2026-07-30 | S | `Providers.LiveDb/Readers/UserReader.cs:20-30`; `Core/Diff/ComparisonEngine.cs:156-159`; stesso JOIN in `LiveDbObjectBodyResolver.cs:112` |
| **Il rebuild non porta i default nel `_tmp`:** una colonna NOT NULL solo-sorgente fa fallire l'INSERT di copia con Msg 515, ed è esattamente il caso che il preflight di backfill esclude di proposito. Nessuna perdita dati (la transazione annulla), ma fallisce a metà script davanti all'utente | 2026-07-30 | M | `Core/ScriptGen/TableScriptEmitter.cs:58` e `:651-696`; innesco stretto a `:619-632` (flip IDENTITY o cambio seed/increment) |
| **Le FK auto-nominate non combaciano mai fra due server.** `ForeignKeysQuery` non legge `is_system_named` e `ConstraintPairing` esclude le FK. **Le strutture di `ScriptGenerator` chiavate sul nome sono CINQUE, non tre** come dicono le remarks e l'handoff: due sono lookup locali che un grep su `orchestratedFks\|fkDropKeys` non trova | 2026-07-30 | M | `Providers.LiveDb/Readers/ConstraintReader.cs:21,68,81` (non in `:35-58`); `Core/Diff/ConstraintPairing.cs:86-90`; `ScriptGenerator.cs:148, 156-158, 247-258, 268-273, 1237-1245` |
| **Smoke live mai fatto sulla voce 12** (vincoli auto-nominati), dove il bug vive negli `object_id` veri e `.243`/`.242` portano DEFAULT inline. Coperta solo da Testcontainers. Da fare insieme a «guardare l'app girare» (P5): stessi due server, stessa sessione | 2026-07-31 (impegno) · 2026-08-17 (voce 12) | S | `docs/review/2026-08-16-handoff-post-scan.md:209-213`, `:408-410`. **Correzione:** l'ondata 2 non ha toccato `ScriptGenerator`, lì l'impegno non si applicava |

---

## P2 — Valore alto, sforzo contenuto: da fare per prime a parità di gravità

| Voce | Reg. | Sforzo | Evidenza verificata |
|---|---|---|---|
| **La ricerca è cieca alle parole che la griglia mostra.** `SearchPredicate` confronta lo `Status` grezzo, mai `StatusDisplayItalian`: cercare «Diverso» non trova nulla mentre la colonna dice «Diverso». Una riga. Nella stessa voce: header che promettono un sort inesistente, e `Refresh()` a ogni battuta | 2026-08-14 | S | `MainWindowViewModel.cs:601-620` e `:630-637`; `ResultsGridView.axaml:41,188,209,222` |
| **Il report HTML è irraggiungibile dalla GUI.** Unico chiamante di produzione è la CLI; l'app ha già in mano `LastComparisonRaw`. Nessun `using` nuovo, nessun riferimento nuovo | 2026-08-14 | S | `Cli/Commands/ReportCommand.cs:83`; zero occorrenze di `HtmlReportGenerator` in `App.Avalonia/`. Usa **«Salva»**, mai «Apri» |
| **Il tooltip «modifica più recente» mente** dove la freccia non c'è: è sullo `StackPanel` radice e il `Tip` passato è sempre la costante. Sposta `ToolTip.Tip` sul `TextBlock PartArrow`, che ha già l'`IsVisible` giusto | 2026-08-14 | XS | `Views/Controls/LastModifiedCell.axaml:20-22` e `:24-31` |
| **La MRU si sovrascrive senza copia di sicurezza.** Il `catch (JsonException)` torna un documento vuoto **senza guardare `forWrite`**, quindi scavalca anche la protezione del ramo successivo. Le dodici righe di `MoveAside` esistono già nel fratello | 2026-07-30 | XS | `Persistence/Json/JsonRecentProjectsStore.cs:105-121` |
| **La modale di primo avvio chiude l'app se annullata** (`desktop.Shutdown()`), pur esistendo il pannello di benvenuto e il comando Nuovo. Togliere `Shutdown`, lasciare `return`: due righe. Seconda metà: nessun dialogo risponde a Invio/Esc — `IsDefault`/`IsCancel` li collegano senza codice, e due code-behind si cancellano nell'occasione | 2026-08-14 | S | `App.axaml.cs:69-74`; grep `IsDefault\|IsCancel` su tutto il progetto → solo `ConfirmDialog.axaml.cs` |
| **Annulla non ferma il confronto**, solo la lettura: `ComparisonEngine.Compare` non prende un token ed è chiamato sincronamente sul thread UI. Metterlo su `Task.Run` sblocca la finestra in poche righe; filare il token dentro `Compare` tocca anche i tre comandi CLI — **fai la prima e fermati** | 2026-08-17 | S | `ViewModels/AppStateViewModel.cs:355` e `:279-287`; `Core/Diff/ComparisonEngine.cs:12` |
| **La Release non allega né hash né attestazione.** Due righe di `Get-FileHash -Algorithm SHA256` più `actions/attest-build-provenance`: costo zero, **non dipendono dal certificato** e vanno prima del code signing | 2026-07-30 | S | grep `sign\|sha256\|attest\|sbom` su `.github/workflows/` → nessun hit |
| **Le docs mentono in cinque punti**, e il danno maggiore è verso di noi: il blocco di stato di questo file era fermo al 2026-06-04 (chiuso da questa riscrittura), il sito non nomina mai la MSI, il README linka le note Redgate come «Architecture», CONTRIBUTING descrive un progetto Blazor morto, `docfx/articles/cli.md` dichiara il falso sulle transazioni. Quasi tutto si chiude cancellando | 2026-08-14 | S | `docs/01_architecture.md`, `docs/04_api_endpoints.md`, `CONTRIBUTING.md`, `docfx/articles/cli.md`; `git log --since=2026-08-14` su quei file → vuoto |
| **`LineDiffer` alloca `int[m+1,n+1]` grezzo** (~100 MB per 5.000 righe, OOM oltre ~23.000) mentre il test più grande ha 3 righe. **La metà economica è indipendente e vale da sola:** tagliare prefisso e suffisso comuni prima della DP fa crollare m e n nel caso reale di poche righe cambiate | 2026-08-14 | S | `Core/Diff/LineDiffer.cs:16` e `:81-98`; la metà virtualizzazione (`DiffViewerView.axaml:152-156, 193-195, 229-235`) aspetta il resolver |

---

## P3 — Debito strutturale

| Voce | Reg. | Sforzo | Evidenza verificata |
|---|---|---|---|
| **~1.500 righe morte**: 6 view irraggiungibili, connection manager senza binding, Serilog mai chiamato con tre `PackageReference`, 3 interfacce senza consumatori. **Dentro c'è una decisione del proprietario**: le connessioni si autosalvano a ogni compare riuscito e alimentano gli «Usati di recente», quindi finché il manager resta irraggiungibile quella lista cresce e nessuno può potarla — o si lega un pulsante, o si cancella tutto | 2026-08-14 | M | `Views/ConnectionPickerView.axaml`, `Views/ResultsTreeView.axaml`, `Cli/Logging/SerilogBootstrap.cs`, `Cli.csproj:13-15`; `OpenConnectionManagerAsync` referenziato solo da `MainWindowViewModel.cs:404` |
| **`ComparisonOptions`: 20 flag dichiarati, 6 letti.** Più `ProjectOptions`, owner/table mappings che non raggiungono alcun motore, parser `.dbd` v1 legacy. **Assorbe `IgnoreConstraintNames`**, che è morto: deciderlo da solo significa scegliere al posto di questa voce per tutti e 14 | 2026-08-14 | M | `Core/Options/ComparisonOptions.cs:10-37`; grep `HasFlag` → 6 occorrenze in tutto; `DbDeltaProject.cs:19-34` |
| **Tassonomia degli avvisi di deploy mai implementata** (`DeployRisk`, `--abort-on-warnings`). Due fette parziali sono atterrate e non la chiudono. **Il difetto più concreto oggi è documentale:** chi scrive una pipeline CI leggendo `docs/01_architecture.md` §9.4 usa uno switch che non esiste | 2026-07-30 | M | grep `AbortOnWarnings\|DeployRisk` → zero; `docs/01_architecture.md:225`, `:1152-1154`, `:1480-1481` |
| **La griglia non è mai stata misurata a 10k oggetti**, e il motore emette una coppia per ogni oggetto, Identical inclusi. Da fare **dopo** la ricerca e il rebuild della griglia: misurare prima significa cronometrare un difetto già noto | 2026-07-30 | M | `MainWindowViewModel.cs:630-637`, `:601-620`; nessun test di scala |
| **Il round-trip per kind copre 4 su 13** (Table, View, Function, Procedure), e uno dei tre test gira solo nella matrice notturna. Sei reader non sono mai passati da un apply vero. **Trappola:** i filtri `.Where` non si cancellano e basta, i commenti sopra `SeededDrift` spiegano perché esistono | 2026-07-30 | M | `DependencyRoundTripTests.cs:48`; `CompatMatrixTests.cs:104-107`; `CompressionRoundTripTests.cs:45-68` |
| **L'invariante di convergenza copre 2 emitter su 14** (View, Procedure). L'esempio più fresco della regola non seguita è la voce 12: ha cambiato come `TableScriptEmitter` scrive i vincoli, ha aggiunto 17 test, e nessuno è «emetti, rileggi, deve essere Identical» | 2026-08-01 | M | `Core.UnitTests/Diff/DeployedModuleConvergesTests.cs:50` e `:67`; `git show --stat 142fcb7` |
| **Gli indici su viste indicizzate sono invisibili**, non solo non scriptabili: due database che differiscono solo per quello escono Identical. Media e non alta **solo grazie al censimento**, che almeno lo dichiara. Non è cambiare un JOIN: vanno appesi a un `View`, che oggi non ha un contenitore di indici | 2026-07-30 | L | `Providers.LiveDb/Readers/IndexReader.cs:44-45`; `UnexaminedReader.cs:50-58` |
| **`LiveDbObjectBodyResolver`, 697 righe**, apre due connessioni per clic e fino a 16 query. Lo switch non ha case per `TableType` né `Schema`: **quei due pannelli sono vuoti oggi, e costa un case in più** — falla anche se i giorni per la riscrittura non ci sono. Prerequisito della virtualizzazione del diff viewer | 2026-08-14 | L | `Providers.LiveDb/ObjectBody/LiveDbObjectBodyResolver.cs:33`, `:35-47`, `:214-257` |
| **Undo dopo un commit riuscito**: nessun down script, nessun journal, nessun backup COPY_ONLY. Rinvio deliberato del proprietario (2026-08-01). Abbassata a media: il percorso distruttivo è tutto sotto consenso e dal commit `0cde9a9` il dialogo elenca per nome ciò che verrà droppato. Manca la rete di recupero **dopo**, che è debito strutturale, non perdita spontanea | 2026-07-30 | L | grep `COPY_ONLY\|DownScript\|DeployJournal` → zero; `docs/review/2026-07-30-undo-architecture.md` |
| **Parità Redgate ferma a 17 scenari** dal 2026-05-28: mancano DROP in topologia inversa con schemabound, indici filtrati/columnstore, CHECK cross-tabella, extended properties. Serve un server vivo e la GUI Redgate (la CLI è license-blocked, exit 35) | 2026-05-28 | L | `tests/Fixtures/Parity/01-source.sql` → 17 scenari; ultimo audit `docs/parity/redgate-2026-05-28.md` |

---

## P4 — Igiene

| Voce | Reg. | Sforzo | Evidenza verificata |
|---|---|---|---|
| **«Apri» invece di «Carica» in quattro punti**, non due. Due sono messaggi d'errore su un progetto da caricare e vanno cambiati; **due sono tooltip di apertura pannello e browser, dove «Apri» è semanticamente giusto — chiedere al proprietario** se la regola li include | 2026-05-22 | XS | `MainWindowViewModel.cs:363`, `:783`; `MainWindow.axaml:564`, `:569` |
| **Il censimento non ha un'asserzione sull'output CLI.** `TextFormatter` è `internal` e la CLI tiene gli internals chiusi apposta: **non aprirla con `InternalsVisibleTo`**, asserisci sullo stdout dentro un'acceptance che già gira | 2026-08-16 | XS | `Cli/Output/TextFormatter.cs:9` e `:31-35`; `CompareCommandTests.cs:287` documenta la scelta |
| **Quattro test mutano `Application.Current.RequestedThemeVariant` senza ripristinarlo.** Non ha ancora morso perché nessun altro test legge un brush dipendente dal tema. La classe implementa già `IDisposable` | 2026-08-14 | XS | `ThemeCycleTests.cs:47-59, 74-86, 138-146, 151-163` contro `:121-134` |
| **`SynonymReader` non fa un-escape di `]]`** — ma la conseguenza raccontata non esiste: i quattro segmenti sono campi morti e l'emittente usa `BaseObjectName` verbatim. **La chiusura più corta è cancellare i campi**, non gestire il `]]` | 2026-07-30 | XS | `Providers.LiveDb/Readers/SynonymReader.cs:52`; `SynonymScriptEmitter.cs:14` |
| **Il parser delle risposte UDP del browser SQL è permissivo:** valida solo `buffer[0] == 0x05` e poi si fida della stringa. Scorporata dalla voce P0 sulle credenziali quando quella è stata chiusa: nulla parte più da sola verso un host suggerito, quindi resta igiene, non esposizione | 2026-07-30 | S | `Persistence/Sql/SqlServerDiscovery.cs:196-199` |
| **Gli invarianti UI non sono asseriti in sé.** `Themes.axaml` è coperto da `AccentBandContrastTests` dal 2026-08-01, ma nulla fallisce se domani qualcuno rimette `Background=Transparent` su `.ghost` o toglie il `MinHeight` 32. Due buchi minori: manca uno stile CheckBox, e `SaveProjectDialog.axaml:31` usa `ghost` per Annulla | 2026-07-30 | S | `Styles/AppStyles.axaml:6-22` e `:91-105`; grep `Tokens.axaml` su `tests/` → zero |

---

## P5 — Del proprietario o non verificabile da soli

| Voce | Reg. | Sforzo | Stato reale |
|---|---|---|---|
| **Nessuno ha mai guardato l'app girare**: dialogo di conferma, banner di rifiuto e pulsante Annulla sono provati solo in headless. Cosa l'headless non copre: il secondo ingresso nel catch di annullamento, la lettura interrotta a metà che il driver riporta come `SqlException -2`. **Stessa sessione dello smoke della voce 12 (P1)** | 2026-08-16 | S | Due connessioni vere, dieci minuti |
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
| Nessun Annulla sull'esecuzione, cap 600 s | 2026-07-30 | Remarks di venti righe sulla costante + tabella «Accettato consapevolmente». `cf81ee9` ha reso annullabile il **compare**, non l'esecuzione. La CLI ha già `--command-timeout 0` |
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
