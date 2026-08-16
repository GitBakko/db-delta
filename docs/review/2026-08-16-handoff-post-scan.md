# HANDOFF — dopo lo scan migliorie: dove siamo e da dove si riparte

**Da leggere per primo in una sessione nuova**, insieme a
`2026-08-14-improvement-scan.md`, che è la roadmap operativa.
`2026-08-08-handoff-to-v1.md` resta valido per la storia del rilascio (la
trappola della `ProductVersion`, la verifica dell'upgrade, le tre reti da non
rompere).

## Stato

- **HEAD `d9759c1`** su `main`, **origin sincronizzato**, working tree pulito.
- **v1.0.2 pubblicata** (2026-08-13). Nessuna release nuova in questa ondata:
  tutto quanto segue è post-1.0.2 e non ancora rilasciato.
- **784 test verdi** su dieci progetti in locale.
- **CI verde su tutti e tre i job**, e per la prima volta la CI vuol dire
  qualcosa — vedi sotto.
- Restano di proprietà del proprietario: **code signing** (bloccato sul
  certificato) e **annuncio pubblico**. L'**undo** resta rinviato.

## Cosa è cambiato in questa ondata

Quattro voci su quattordici dello scan, un commit ciascuna.

| Voce | Commit | Effetto |
|---|---|---|
| 1 — la CI non faceva da gate | `f91ee6e` | il badge verde ora significa qualcosa |
| 4 — diff pane | `d210b1a` | non può più mostrare l'SQL di un oggetto sotto il nome di un altro |
| 3 — dialogo di conferma | `0cde9a9` | mostra lo script e nomina ciò che verrà eliminato |
| 11a — censimento | `6c2e2e9` | «nessuna differenza» dichiara il proprio perimetro |

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

## Da dove si riparte: la voce 11b

**È la voce più importante che resta, ed è distruzione silenziosa di dati.**

`TableScriptEmitter.cs:668-670` — il rebuild chiama `EmitCreate` per la tabella
`_tmp`, che emette solo `table.Indexes`; poi `:691` fa `DROP TABLE`
dell'originale. Il columnstore non è mai stato nel modello
(`IndexReader.cs:42` filtra `AND i.type IN (1, 2)`), quindi **nessuno lo
ricrea** — sotto banner verde di successo.

Il censimento ha reso il punto cieco *visibile*, non innocuo.

**Lo scenario è già riprodotto da un test live**, non va inventato:
`tests/DbDelta.Providers.LiveDb.IntegrationTests/UnexaminedCensusTests.cs`,
`A_columnstore_only_difference_compares_Identical_and_the_census_says_why`.

Il passo minimo che ferma la distruzione **non** è emettere i columnstore:

1. Allargare il filtro di `IndexReader` a tutti i tipi e portare `i.type_desc`
   su `TableIndex` (oggi il modello non ha **nessun** campo per il tipo di
   indice).
2. Emettere `CREATE` per i tipi 1/2 come oggi; esporre il resto come
   Different-ma-non-scriptabile.
3. **Far RIFIUTARE `EmitRebuild`**, con errore chiaro, su una tabella che porta
   un indice che non sa riemettere — **prima** che una riga di SQL tocchi il
   server.

L'emissione completa del columnstore può seguire dopo. È il rifiuto che ferma
la perdita.

## Reti da non rompere (l'elenco cresce)

Alle tre di `2026-08-08-handoff-to-v1.md` — `DeployedModuleConvergesTests`,
`AccentBandContrastTests`, `CompressionRoundTripTests` — si aggiungono:

4. **`UnexaminedCensusTests` (live)** — asserisce che due database la cui unica
   differenza è un columnstore risultano `Identical`. **Se un giorno quel test
   fallisce perché lo status non è più Identical, non "correggerlo": vuol dire
   che qualcuno ha chiuso il punto cieco**, ed è una buona notizia da
   riscrivere, non un rosso da spegnere.
5. **`A_huge_script_does_not_grow_the_window_past_the_screen`** — misura il
   PANNELLO, non la finestra. Vedi la trappola qui sotto.

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

## Cosa NON è coperto, dichiarato

- **L'output testuale della CLI per il censimento** non ha un'asserzione
  diretta: `TextFormatter` è `internal` e le acceptance girano l'eseguibile. Il
  `Summary` che stampa è testato a unità; il codice attorno sono quattro righe
  dietro un `IsEmpty`.
- **Il dialogo di conferma non è stato visto dal vivo** — servono due
  connessioni SQL vere e una selezione. L'headless prova che il XAML
  renderizza, che i contrasti reggono nei due temi e che il pannello dello
  script resta limitato; l'estetica no.

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
