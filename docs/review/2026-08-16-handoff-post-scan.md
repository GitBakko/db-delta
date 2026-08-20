# HANDOFF — dopo lo scan migliorie: dove siamo e da dove si riparte

> **STORIA, non stato — rileggere il 2026-08-20.** Le tre voci di «Da dove si
> riparte» sono tutte chiuse: il report HTML (`91cafb2`), l'exit code di
> `dbdelta script` e lo smoke dal vivo del 2026-08-18. Questo file resta per
> *perché* le cose sono state fatte così e quali trappole sono state pagate.
> **Lo stato aperto sta solo in `docs/BACKLOG.md`**, riscritto e riverificato
> voce per voce il 2026-08-18: l'ultima riga di questo documento diceva di non
> fidarsene, ed è la sola riga che era vera il 16 e non lo è più.

**Gli SHA citati qui sono anteriori alla riscrittura della storia del
2026-08-18 e non risolvono più.** Cerca per messaggio di commit.


**Da leggere per primo in una sessione nuova**, insieme a
`2026-08-14-improvement-scan.md`, che è la **diagnosi**, non lo stato: lo stato vive solo in `docs/BACKLOG.md`.
`2026-08-08-handoff-to-v1.md` resta valido per la storia del rilascio (la
trappola della `ProductVersion`, la verifica dell'upgrade, le tre reti da non
rompere).

## Stato

- **L'ondata 11b è su `origin/main`**, spinta il 2026-08-16 (`0e95089..db40740`).
  Una riga di handoff sullo stato del push invecchia il commit dopo: chiedilo a
  `git status -sb`, non a questo file.
- **Le voci 12 e 2 sono su `origin/main`**, spinte il 2026-08-17
  (`3e0676e..f833df0`): `9b81ca0` `142fcb7` per la 12, `8cfbc91` `cf81ee9` per
  la 2, più i docs.
- **v1.0.2 pubblicata** (2026-08-13). Nessuna release nuova in questa ondata:
  tutto quanto segue è post-1.0.2 e non ancora rilasciato.
- **830 test verdi** in locale su dieci progetti (i 3 della matrice compat
  restano skipped senza `DBDELTA_COMPAT=1`). Erano 804 prima della 12, 822 prima
  della 2.
- **CI verde su `db40740`**, job `ci` e `docs`, con `Verify formatting` e i
  Testcontainers Linux dentro. La matrice compat non gira sui push: è notturna,
  e l'ultima misura è il run `31925658819`. Da `f91ee6e` un badge verde vuol
  dire qualcosa — vedi sotto.
- Restano di proprietà del proprietario: **code signing** (bloccato sul
  certificato) e **annuncio pubblico**. L'**undo** resta rinviato.

## Cosa è cambiato in questa ondata

Sette voci su quattordici dello scan.

| Voce | Commit | Effetto |
|---|---|---|
| 1 — la CI non faceva da gate | `f91ee6e` | il badge verde ora significa qualcosa |
| 4 — diff pane | `d210b1a` | non può più mostrare l'SQL di un oggetto sotto il nome di un altro |
| 3 — dialogo di conferma | `0cde9a9` | mostra lo script e nomina ciò che verrà eliminato |
| 11a — censimento | `6c2e2e9` | «nessuna differenza» dichiara il proprio perimetro |
| 11b — rifiuto | `04886b1` `b250d4f` `3c32b71` | un rebuild non può più distruggere in silenzio un indice che non sa riscrivere |
| 12 — vincoli auto-nominati | `9b81ca0` `142fcb7` | una tabella con un DEFAULT inline non è più Different per sempre, e nessun hash viaggia fra due server |
| 2 — annullamento + timeout | `8cfbc91` `cf81ee9` | un compare contro un server lento non si finisce più a colpi di Task Manager |

### 1 — La CI, misurata sul runner reale

Non è una modifica dedotta: il run notturno `31925658819` su `f91ee6e`
(`event: schedule`) la esercita tutta.

- **La matrice compat ha girato per la PRIMA volta**: `Passed: 3, Skipped: 0`
  in 59 s contro SQL Server 2017/2019/2022. Prima: `Skipped: 3` in **77 ms**,
  senza tirare una sola immagine. La sonda cercava TCP `localhost:2375` e la
  named pipe di Windows, mai il socket unix di Linux.
- **Job Windows, `Persistence.IntegrationTests`: `Passed: 4, Skipped: 3`.**
  Prima erano 3 rossi sotto un job verde, perché sei `dotnet test` in un solo
  `run: |` lasciano a pwsh solo l'exit code dell'**ultimo**.
- **Job Linux, stesso progetto: `Passed: 7`.** È la metà che prima non girava
  da nessuna parte: la semantica di rollback di `SqlExecutor` non era asserita
  in nessun job. Il progetto ora sta in **entrambi** e ne esercita una metà
  disgiunta per lato — DPAPI ha già la guardia `IsWindows`, i test
  Testcontainers possono avere un container Linux solo sul job Linux.
- `Shared.UnitTests` (4) e `Property.Tests` (12) comparivano in **nessun** job.

**La lezione, generalizzabile:** una sonda che *indovina* se una dipendenza
esterna c'è mente in entrambe le direzioni. Fai partire la cosa e lascia
rispondere lei, portandone il messaggio dentro lo skip — così un run verde dice
comunque perché non ha asserito niente.

### 11a — Il censimento è la dichiarazione, NON la correzione

`UnexaminedCensus` (Core) + `UnexaminedReader` (provider) contano in un round
trip per endpoint ciò che i reader non coprono, e il caveat viaggia su
`ComparisonResult` fino a quattro superfici: banda ambra sopra la griglia,
banner sotto i totali del report HTML, campi strutturati + frase nel report
JSON, riga finale nell'output testuale della CLI. Niente viene emesso quando
non c'è niente da dichiarare.

Il merge fra i due lati prende il **massimo**, non la somma: lo stesso indice
esiste di norma su entrambi gli endpoint.

### 11b — Il rifiuto, e dove sta

`IndexReader` non filtra più su `i.type IN (1, 2)`: **ogni tipo di indice entra
nel modello**, con `sys.indexes.type_desc` su `TableIndex.TypeDesc` (ultimo
parametro del record, così le costruzioni posizionali esistenti compilano
ancora). L'unica forma ancora esclusa è l'heap, che non ha nome e non è un
indice. `TableIndex.IsRowstore` è l'unica domanda che le superfici di emissione
pongono.

Il rifiuto è `UnscriptableIndexException`, alzato da **due** guardie:

- `IndexScriptEmitter` — in `EmitCreate` e in `EmitRebuildForCompression`.
  Coprono ogni percorso che dovrebbe scrivere l'indice: tabella nuova, delta
  degli indici, ricreazione forzata dopo un DROP bloccante, e il passo che
  ripristina gli indici di una tabella ricostruita.
- `TableScriptEmitter.EmitRebuild` — accanto al `DROP TABLE` che protegge.
  Ridondante finché quel passo gira; il punto è che smette di esserlo il giorno
  in cui qualcuno lo cambia. È l'unica guardia che il probe di mutazione fa
  cadere da sola.

`EmitDrop` è **esente di proposito**: `DROP INDEX` è valido per ogni tipo, e
rifiutare di droppare ciò che la sorgente non ha più bloccherebbe una
convergenza che il target sa completare.

Tre cose che l'analisi dello scan non aveva previsto:

- **Il corpo del diff viewer non può lanciare.** `GenerateFullTableBody` rende
  un `--` di commento con nome, tipo e colonne chiave. Un pannello che mostra
  due testi identici su una riga che la griglia chiama Different è lo stesso bug
  del vuoto della voce 4, visto dall'altro capo.
- **`EmitRebuildForCompression` andava chiusa insieme a `EmitCreate`**: per un
  columnstore `data_compression_desc` vale `COLUMNSTORE`, che un REBUILD
  rowstore non accetta.
- **Il messaggio d'errore è codice emittente per `IdentifierQuotingTests`.**
  `$"[{schema}].[{table}]"` dentro il testo dell'eccezione fa fallire il test di
  architettura: usa `Sql.Q`, anche in un messaggio.

Il **censimento resta**, ristretto: gli indici non rowstore ora sono confrontati
per nome e colonne, ma le loro opzioni specifiche non si leggono e un `CREATE`
non si emette mai. L'etichetta è passata a «opzioni di indici non rowstore».

**Non fatta, e deliberatamente: emettere un columnstore.** È il rifiuto che
ferma la perdita. Scrivere il `CREATE` è una voce a sé e nessuno l'ha chiesta.

**Misurata dal vivo, non dedotta** — A/B sulla stessa coppia reale
(`PcrmV2Pl_test` → `PcrmV2Pl_Badii` su `.243`), stessa CLI compilata prima
(`0e95089`) e dopo:

| | pre | post |
|---|---|---|
| oggetti | 841 | 841 |
| Identical / Different / OnlyInA / OnlyInB | 786 / 13 / 24 / 18 | identico |
| status spostati | — | **0** |
| script generato | — | **byte-identico** (`cmp`) |

Il censimento di quella coppia dice `52 proprietà estese` e **nessun indice non
rowstore**: il reader allargato è un no-op *provato* lì, e il percorso di
rifiuto **non** è stato esercitato su quei server. Resta coperto
dall'acceptance, che gira lo stesso scenario attraverso il confine di processo
contro un SQL Server 2022 vero.

Un effetto collaterale confermato su dati veri nella stessa corsa: **`dbdelta
script` esce 0 con 13 differenze pendenti**. È la voce 5, non una regressione di
questa ondata.

### 12 — I vincoli auto-nominati: appaiati per FORMA, creati senza nome

Il suffisso di `DF__Ordini__Stato__3B75D760` viene dall'`object_id` di quel
vincolo, quindi lo stesso schema deployato due volte produce due nomi che
dissentono **per costruzione**. Appaiati per nome, ogni tabella con un DEFAULT
inline o un CHECK/PK senza nome era Different per sempre, e lo script droppava
l'hash del target per aggiungere quello della sorgente — su chiavi primarie di
produzione.

Il modello (`9b81ca0`) porta `Constraint.IsSystemNamed`, letto da
`sys.key_constraints`/`sys.check_constraints`/`sys.default_constraints`. È una
proprietà `init`, non un membro posizionale: ogni costruzione posizionale
esistente compila ancora e resta `false`, cioè il comportamento vecchio.

La decisione (`142fcb7`) sta tutta in `Core/Diff/ConstraintPairing.cs`, una
classe che risponde a **una** domanda — *quale vincolo di là È questo vincolo di
qua* — prima e separatamente da *è cambiato*. Auto-nominato → si appaia per
forma: colonne per PK/UQ, espressione normalizzata per CHECK, colonna per
DEFAULT. Tutto il resto → per nome.

**Le strutture erano QUATTRO, non tre.** Alle tre che questo file aveva elencato
si aggiunge quella che nessuno aveva contato: le tre query parallele di
`LiveDbObjectBodyResolver`, da cui il diff viewer costruisce i corpi. Un corpo
che stampa ancora l'hash mostra una differenza su una riga che la griglia ora
chiama Identical — l'esatto bug della voce 4, visto dall'altro capo. Il test
`The_diff_viewer_body_is_identical_on_both_sides` è lì per quello.

Quattro cose che l'analisi dello scan non aveva previsto:

- **La chiave di appaiamento è IDENTITÀ, deliberatamente più stretta della
  shape-equality che i chiamanti applicano dopo.** Una PK sulle stesse colonne
  passata a NONCLUSTERED è la stessa PK, *cambiata*. Se la forma completa
  entrasse nella chiave, ogni modifica diventerebbe un non-appaiamento: i
  chiamanti la gestiscono (drop + add), ma nessuno riuscirebbe più a dire «lo
  stesso vincolo, cambiato».
- **I due lati devono concordare su COME si appaiano, prima di poterlo fare.**
  Una sorgente con un DEFAULT inline contro un target con `CONSTRAINT DF_Stato
  DEFAULT` sono due vincoli davvero diversi: il target porta un nome che la
  sorgente non ha mai chiesto. Lo script lo droppa e lascia che il server conii
  il proprio.
- **Consume-once nel motore.** `ConstraintsEqual` toglie dalla lista il vincolo
  appaiato. Senza, due CHECK sorgente con la stessa espressione reclamano
  entrambi l'unico del target, il conteggio torna, e la tabella esce Identical
  con un vincolo mai esaminato.
- **`droppedForColumnDependency` va chiavato sul nome della SORGENTE.** La
  sezione 5 di `EmitAlter` cerca lì il ripristino di un vincolo droppato solo
  per liberare una colonna alterata, e per un auto-nominato i due nomi
  differiscono: con la chiave del target il ripristino non scatterebbe mai e il
  vincolo resterebbe perso. I DROP, invece, usano sempre il nome vero del
  target — è l'unico posto dove quell'hash è giusto.

Le **FK restano appaiate per nome**, di proposito: il loro lato di emissione è
chiavato sul nome in tre strutture di `ScriptGenerator` (`orchestratedFks`,
`fkDropKeys`, il delta FK), e cambiare la regola qui da solo lascerebbe motore e
emittente in disaccordo sulla stessa chiave.

**Provato:** 10 test nuovi in `SystemNamedConstraintTests` (CREATE, ALTER,
rebuild, catena completa motore+emittente, corpo del diff viewer), 7 aggiunti a
`ConstraintDiffTests`, più le asserzioni su `is_system_named` in
`ConstraintReaderTests` (live). Due probe di mutazione, entrambe cadute:
`PairsByShape` che ritorna sempre `false`, e `NameClause` che emette sempre il
nome.

**Non fatto, e dichiarato:** `IgnoreConstraintNames` resta scollegato — è un
flag morto, e tocca alla voce 9 decidere se quei flag si implementano o si
cancellano. E lo **smoke dal vivo su `.243`/`.242` non è stato fatto**: questa è
la voce dove servirebbe di più, perché il bug vive negli `object_id` veri, e i
database lì portano DEFAULT inline. L'unica prova su metadati veri è il giro
Testcontainers (33 LiveDb + 20 acceptance + 7 persistence, verdi con Docker su).

### 2 — Annullare il compare, e non morire a 30 s

Due metà indipendenti, due commit.

**Le letture (`8cfbc91`).** Tutte stavano sul default ADO.NET di 30 s, incluse
quelle che non sono piccole: la lettura delle colonne unisce quattro viste di
catalogo per l'intero database, quella degli indici porta una sottoquery per
riga su `sys.partitions`. Il percorso di deploy aveva 600 s da sempre; quello
di lettura, che scandisce tutto, era rimasto al default.

`SqlConnection.CommandTimeout` **è di sola lettura**: la stringa di connessione
è l'unica leva. Questo è anche il motivo per cui una sola modifica in
`ConnectionFactory.OpenAsync` copre tutti i comandi di lettura — un `SqlCommand`
creato da quella connessione eredita il valore. Coperti entrambi i chiamanti,
`LiveDbSource` e `LiveDbObjectBodyResolver`.

Due dettagli che non si indovinano:

- **`ShouldSerialize("Command Timeout")`, non `ContainsKey`.** Il secondo
  risponde «è una keyword nota?», che è sempre vero: con `ContainsKey` il
  timeout non verrebbe iniettato mai. Il probe lo prova.
- **`Command Timeout=0` vuol dire illimitato e va lasciato stare.** Una guardia
  del tipo «se vale 30, alzalo» mangerebbe sia lo 0 sia il 30 scritto apposta.
  Chi l'ha scritto se lo tiene, e la stringa torna indietro **identica**.

`SqlException -2` è insieme «non raggiungo il server» e «la query è scaduta», e
il rimedio suggerito nominava solo il firewall. Ora li nomina entrambi e dice
quanto la lettura ha già aspettato.

**L'annullamento (`cf81ee9`).** `CompareAsync` filava già il token dentro le due
letture: mancava un token che qualcuno potesse cancellare.
`IncludeCancelCommand` genera `CompareCancelCommand`, e l'overlay ci lega un
**Annulla** neutro (`Classes="ghost"`, che in questo repo È il neutro pieno; il
`MinHeight` 32 arriva dallo stile base, niente colori inline).

Tre cose che l'analisi non aveva previsto:

- **Tre dei «cinque call site» erano già a posto.** `ExecuteAsync(object?)`
  ignora il parametro, quindi il `CancellationToken.None` scritto in tre punti
  era un *parametro di comando* buttato via: quelle chiamate giravano già sul
  CTS del comando. I call site veri erano **due**, quelli che chiamavano il
  metodo direttamente — «Aggiorna» e il refresh dopo un'esecuzione riuscita — ed
  è esattamente dove il pulsante sarebbe stato morto.
- **`ExecuteAsync` non consulta `CanExecute`**, quindi lo scavalcamento
  deliberato documentato su `RefreshAsync` sopravvive. La guardia `IsBusy`
  invece **resta**: `ExecuteAsync` cancella la corsa in volo prima di
  ricominciare, quindi senza guardia un secondo «Aggiorna» aborta il primo
  invece di essere ignorato.
- **Annullare non alza per forza un'eccezione.** Una lettura interrotta a metà
  torna dal driver come `SqlException -2`, che `LiveDbSource` trasforma in un
  *Result* `CannotConnect`: il solo `catch (OperationCanceledException)` avrebbe
  lasciato la banda rossa a incolpare la rete di ciò che l'utente aveva appena
  chiesto. Serve `ct.ThrowIfCancellationRequested()` dopo ogni lettura.

**Annulla accorcia la lettura, non il confronto**: `engine.Compare` è sincrono e
gira sul thread UI. Sta scritto nel doc-comment, perché è la prima domanda di
chi guarda la barra ferma.

## Da dove si riparte

Nell'ordine in cui le rimetterei in fila — la scelta resta del proprietario, e
la roadmap ha l'evidenza `file:riga` per ciascuna.

1. **Voce 6 — il report HTML che la GUI non sa invocare.** Ore, zero codice di
   motore nuovo: `LastComparisonRaw` **è** l'input che il generatore prende.
2. **Voce 5 — `dbdelta script` esce 0 con differenze pendenti.** Non è dedotta:
   è stata vista sui dati veri dell'A/B della 11b, 13 differenze e exit 0.
3. **Guardare l'app girare.** Non è una voce dello scan, ed è il debito più
   vecchio di questa ondata: il dialogo di conferma (voce 3), il banner di
   rifiuto (11b) e ora il pulsante Annulla sono tutti provati in headless e
   nessuno è mai stato visto da un essere umano. Servono due connessioni vere e
   dieci minuti.

   Prima di aprire una voce che tocca i vincoli, leggi la sezione 12 qui sopra:
   le strutture che li confrontano sono **quattro**, in tre assembly, e la
   lezione ricorrente di questo repo è che chi ne cambia una sola lascia le
   altre a dissentire.

## Reti da non rompere (l'elenco cresce)

Alle tre di `2026-08-08-handoff-to-v1.md` — `DeployedModuleConvergesTests`,
`AccentBandContrastTests`, `CompressionRoundTripTests` — si aggiungono:

4. **`UnexaminedCensusTests` (live)** — asseriva `Identical` su una differenza
   di solo columnstore, e la 11b ha girato l'asserzione in `Different`: era
   proprio la buona notizia che la rete diceva di riscrivere, non un rosso da
   spegnere. Ora la rete è l'altra metà: **il censimento deve continuare a
   dichiarare le opzioni non esaminate anche adesso che l'indice è visto**. Un
   verdetto che smette di dichiarare il proprio perimetro è una regressione
   anche quando tutto il resto è verde.
5. **`A_huge_script_does_not_grow_the_window_past_the_screen`** — misura il
   PANNELLO, non la finestra. Vedi la trappola qui sotto.
6. **`UnscriptableIndexRefusalTests` (Core)** — sette test, e due di essi sono
   controlli in negativo: `The_same_rebuild_with_a_rowstore_index_still_generates`
   e `Dropping_a_columnstore_the_source_no_longer_has_is_allowed`. **Se il
   rifiuto diventa un blocco generale sui rebuild o sui DROP, quei due
   cadono e sono loro ad avere ragione.**
7. **`Refuses_with_exit_30_when_a_rebuild_would_drop_a_columnstore_index`
   (acceptance)** — l'unico test che attraversa il confine di processo su questa
   catena. Se torna 99, non è il test: è il `catch` di `Program.cs` che qualcuno
   ha scavalcato.
8. **`SystemNamedConstraintTests` (Core)** — dieci test, e due sono controlli in
   negativo: `A_new_table_still_creates_an_explicitly_named_constraint_by_name`
   e `A_named_target_constraint_facing_an_auto_named_source_one_is_replaced`.
   **Se l'appaiamento per forma diventa la regola generale, o se l'emittente
   smette del tutto di scrivere i nomi, cadono quei due e sono loro ad avere
   ragione.** `A_table_differing_only_by_the_hash_produces_no_script` è l'unico
   che attraversa motore **ed** emittente insieme: se cade solo lui, i due lati
   hanno ripreso a dissentire, ed è esattamente il modo in cui questa famiglia
   di bug si riapre.
9. **`CompareCancellationTests` (headless)** — due dei quattro test non guardano
   un comportamento ma un **instradamento**: che «Aggiorna» e il refresh
   post-esecuzione passino da `CompareCommand` invece di chiamare `CompareAsync`
   diretto. È invisibile nell'app finché non serve, e il giorno che qualcuno
   «semplifica» rimettendo la chiamata diretta il pulsante Annulla muore in
   silenzio proprio lì. Se cadono quelli, hanno ragione loro.

## Trappole pagate in questa sessione

- **`dotnet test` su `App.HeadlessTests` si pianta al PRIMO lancio dopo una
  build.** Host vivo con ~0,6 s di CPU su 10 minuti. Ambientale e riproducibile,
  non del codice: uccidi `DbDelta.App.HeadlessTests` e `testhost`, rilancia, il
  secondo tentativo chiude in ~20 s. Costata tre falsi allarmi e per poco lo
  scarto di un test valido.
- **Non usare `--blame-hang`** per indagarla: tronca il run (17 test su 149) e
  restituisce exit 1, che sembra un fallimento vero.
- **Un test di layout headless che asserisce su `Window.Bounds` non prova
  niente.** Con `SizeToContent` la finestra resta piccola anche senza cap. La
  prima versione del test passava con la mutazione applicata. Misurato sul
  pannello, lo stesso probe riporta **59966 px** contro un bound di 260. **Il
  probe di mutazione è ciò che ha distinto il test vero da quello vacuo** — su
  un test nuovo che asserisce un limite, falla sempre.
- **`&&` a fine riga in pwsh** è insieme continuazione e short-circuit, e
  `$LASTEXITCODE` propaga. Verificato prima di scriverlo in `ci.yml`
  (`cmd /c exit 3 && …` → seconda riga non eseguita, exit 3).
- **`<see cref="...">` verso un `partial void` generato da MVVM Toolkit** dà
  `CS0419` (ambiguo con l'overload a due parametri). Usa `<c>...</c>`.
- **Attenzione a `NotContain("nome-classe-css")`** in un test sul report HTML:
  la regola CSS è sempre nello `<style>`. Asserisci sull'elemento.
- **`IdentifierQuotingTests` legge anche i messaggi d'eccezione.** Un
  `$"[{schema}].[{table}]"` nel testo di un `throw` sotto `ScriptGen` fa rosso
  l'assembly di architettura: passa da `Sql.Q` anche lì. Giusto così — un nome
  di catalogo con un `]` rompe l'identificatore ovunque venga stampato.
- **Due guardie per lo stesso caso non si provano con un test solo.** Con la
  guardia di `EmitRebuild` e quella di `IndexScriptEmitter` entrambe attive,
  togliere la prima non fa cadere niente. Serve il test che entra dal solo
  `TableScriptEmitter.Emit(pair)`, senza il generatore attorno: è quello a
  distinguere «la guardia c'è» da «qualcun altro la copre».
- **`--no-build` fa girare l'assembly di prima quando la build fallisce**, e
  stampa un verde o un rosso che non c'entrano niente col codice sullo schermo.
  Ripreso in pieno con un probe scritto come `catch (…) when (false)`: è
  `error CS8359`, la build è morta e il test rosso mostrato era quello del probe
  precedente. Un probe di mutazione deve **prima** stampare `Errori: 0`.
- **Con Docker spento i test DB-backed vanno ROSSI, non skipped**: 33 LiveDb +
  20 acceptance + 3 di persistence, 56 in tutto. È la voce 1 che funziona — le
  sonde che indovinavano sono state cancellate apposta — ma davanti a quel muro
  rosso la prima domanda è `docker ps`, non `git diff`.
- **Nullable e lambda:** sostituire una chiamata inline con un helper può
  rendere superfluo un `!` che prima serviva, e `IDE0370` è un **errore**, non un
  avviso. Il pattern `if (Metodo(...) is not string x) { return; }` restituisce
  un locale non-nullable che sopravvive dentro le closure.

## Cosa NON è coperto, dichiarato

- **L'output testuale della CLI per il censimento** non ha un'asserzione
  diretta: `TextFormatter` è `internal` e le acceptance girano l'eseguibile. Il
  `Summary` che stampa è testato a unità; il codice attorno sono quattro righe
  dietro un `IsEmpty`.
- **Il dialogo di conferma non è stato visto dal vivo** — servono due
  connessioni SQL vere e una selezione. L'headless prova che il XAML
  renderizza, che i contrasti reggono nei due temi e che il pannello dello
  script resta limitato; l'estetica no.
- **Il banner di rifiuto dell'app non è stato visto dal vivo.** L'headless prova
  che `TryBuildDeployScript` ritorna `null` e riempie `AppState.LastError`;
  che quel testo lungo stia bene nella banda, no. Vale anche per il rifiuto sul
  percorso **Esegui**: la catena è la stessa, ma nessuno l'ha guardata girare
  con due server veri.
- **Il rifiuto non è mai stato provato contro `.243`/`.242`**, perché nessuno
  dei database lì porta un indice non rowstore. Servirebbero due database
  usa-e-getta seminati apposta; proposto e **scartato dal proprietario**
  (2026-08-16) perché l'acceptance copre già lo scenario contro un server vero.
- **Indici su viste indicizzate restano invisibili**, non solo non scriptabili:
  `IndexReader` fa `INNER JOIN sys.tables`. Il censimento li conta a parte
  (`INDEX_ON_VIEW`) e nient'altro li nomina.
- **Il rifiuto scatta sul lato sorgente.** Se solo il *target* porta un indice
  non rowstore e la tabella viene ricostruita, l'indice sparisce senza errore —
  ed è corretto, perché la sorgente non ce l'ha e la convergenza lo vuole via.
  Vale la pena saperlo prima di leggerlo come un buco.
- **Le FK auto-nominate hanno ancora il bug della 12.** `ForeignKeysQuery` non
  legge `is_system_named` e `ConstraintPairing` esclude le FK per scelta, quindi
  un `FK__Ordini__Cliente__2B3F6F97` continua a non combaciare mai fra due
  server. È contenuto — una FK inline senza nome è molto più rara di un DEFAULT
  inline — ma è lo stesso churn. Chiuderlo vuol dire rifare le tre strutture di
  `ScriptGenerator` chiavate sul nome, non aggiungere una colonna alla query.
- **La 12 non è mai stata provata contro `.243`/`.242`.** È la voce dove lo
  smoke conterebbe di più, perché il bug vive negli `object_id` veri e quei
  database portano DEFAULT inline. Coperta solo dai Testcontainers.
- **`IgnoreConstraintNames` resta un flag dichiarato e morto.** Non è una svista
  della 12: la voce 9 deve prima decidere se quei flag si implementano o si
  cancellano.
- **Il pulsante Annulla non è mai stato visto dal vivo**, e neanche annullato
  per davvero: il test headless cancella il token *prima* della chiamata, così
  `OpenAsync` restituisce un task cancellato senza toccare la rete. Il secondo
  ingresso nello stesso `catch` — la lettura interrotta a metà, che il driver
  riporta come `SqlException -2` — lo può produrre solo un server vero.
- **`engine.Compare` non è annullabile** ed è sincrono sul thread UI: su un
  catalogo grosso il pulsante è cliccabile ma non succede niente finché il
  confronto non finisce. Va saputo prima di leggerlo come un bug.
- **Il timeout di 300 s non è stato misurato contro un server lento davvero.**
  Il test prova che il valore arriva sulla connessione e che quello dell'utente
  vince; che 300 s bastino per un catalogo enorme è una scommessa ragionata, non
  un dato.

## Dove guardare

- `docs/review/2026-08-14-improvement-scan.md` — **la roadmap**: 14 voci
  classificate per valore/sforzo con evidenza `file:riga`, **e 17 proposte
  scartate col motivo**. Quest'ultima metà vale quanto la prima: evita di
  riscoprire come nuove cose già valutate e respinte, incluse due la cui
  evidenza non reggeva.
- `docs/review/2026-08-08-handoff-to-v1.md` — rilascio, `ProductVersion`,
  upgrade verificato, le prime tre reti.
- `docs/review/2026-07-31-handoff-post-wave.md` — i 33 moduli, il confronto con
  Redgate, i due bug del primo smoke.
- `docs/BACKLOG.md` — **la sola lista di lavoro aperta**. Quando questa riga fu
  scritta era indietro di tre release (voce 14 dello scan) e diceva di non
  fidarsi del suo blocco di stato; il 2026-08-18 il file è stato riscritto e
  riverificato voce per voce, e la voce 14 è chiusa. Il blocco di stato invecchia
  comunque in giorni: ricontrollalo con `git status -sb` e `git log -1`, come
  chiede la sua stessa sezione «Manutenzione».
