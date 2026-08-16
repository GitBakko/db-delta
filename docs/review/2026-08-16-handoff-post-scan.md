# HANDOFF — dopo lo scan migliorie: dove siamo e da dove si riparte

**Da leggere per primo in una sessione nuova**, insieme a
`2026-08-14-improvement-scan.md`, che è la roadmap operativa.
`2026-08-08-handoff-to-v1.md` resta valido per la storia del rilascio (la
trappola della `ProductVersion`, la verifica dell'upgrade, le tre reti da non
rompere).

## Stato

- **L'ondata 11b è su `origin/main`**, spinta il 2026-08-16 (`0e95089..44e00f8`).
  Una riga di handoff sullo stato del push invecchia il commit dopo: chiedilo a
  `git status -sb`, non a questo file.
- **v1.0.2 pubblicata** (2026-08-13). Nessuna release nuova in questa ondata:
  tutto quanto segue è post-1.0.2 e non ancora rilasciato.
- **804 test verdi** in locale su dieci progetti (i 3 della matrice compat
  restano skipped senza `DBDELTA_COMPAT=1`).
- **CI verde su tutti e tre i job** all'ultima misura, e per la prima volta la
  CI vuol dire qualcosa — vedi sotto. I quattro commit nuovi non l'hanno ancora
  attraversata.
- Restano di proprietà del proprietario: **code signing** (bloccato sul
  certificato) e **annuncio pubblico**. L'**undo** resta rinviato.

## Cosa è cambiato in questa ondata

Cinque voci su quattordici dello scan.

| Voce | Commit | Effetto |
|---|---|---|
| 1 — la CI non faceva da gate | `f91ee6e` | il badge verde ora significa qualcosa |
| 4 — diff pane | `d210b1a` | non può più mostrare l'SQL di un oggetto sotto il nome di un altro |
| 3 — dialogo di conferma | `0cde9a9` | mostra lo script e nomina ciò che verrà eliminato |
| 11a — censimento | `6c2e2e9` | «nessuna differenza» dichiara il proprio perimetro |
| 11b — rifiuto | `04886b1` `b250d4f` `3c32b71` | un rebuild non può più distruggere in silenzio un indice che non sa riscrivere |

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

## Da dove si riparte

Nell'ordine in cui le rimetterei in fila — la scelta resta del proprietario, e
la roadmap ha l'evidenza `file:riga` per ciascuna.

1. **Voce 12 — i vincoli auto-nominati appaiati per nome.** È l'altra metà del
   punto 11 dal lato del falso *positivo*: `DF__Ordini__Stato__3B75D760` non
   combacerà mai con l'hash dell'altro server, quindi ogni tabella con un
   DEFAULT inline è Different per sempre e lo script droppa e ricrea vincoli su
   chiavi primarie di produzione. Giorni.
2. **Voce 2 — il compare non si annulla e muore a 30 s.** Ore, e il token è già
   filato correttamente fin dentro i reader: mancano i cinque call site e un
   pulsante.
3. **Voce 6 — il report HTML che la GUI non sa invocare.** Ore, zero codice di
   motore nuovo: `LastComparisonRaw` **è** l'input che il generatore prende.

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
- `docs/BACKLOG.md` — **è indietro di tre release**, lo dice la voce 14 dello
  scan. Non fidarti del suo blocco di stato.
