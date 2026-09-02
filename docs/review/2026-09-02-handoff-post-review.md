# HANDOFF — 2026-09-02 (sera): tre voci chiuse, e la review che ha corretto il correttore

> **STORIA, non stato.** Questo file dice *perché* le cose sono state fatte così
> e *quali trappole sono state pagate*. **Ciò che è aperto sta SOLO in
> `docs/BACKLOG.md`**, l'unica lista di lavoro: qui non è duplicato, e se un
> giorno i due divergono ha ragione il backlog. Ogni riga di stato qui sotto
> invecchia il commit dopo: riverificala con `git status -sb`, `git log -1` e un
> `awk` sulle righe di tabella.

**Da leggere per primo in una sessione nuova**, con `docs/BACKLOG.md` accanto.
Sostituisce `2026-09-02-handoff-open-points.md` del mattino, che resta valido
**solo** come misura del gate d'errore di batch: le sue tre voci «da dove si
riparte» sono chiuse, e il documento lo dichiara in testa.

---

## Stato al momento della scrittura

- **`main` = `origin/main` = `11f1caa`**, pushato il 2026-09-02. `git ls-remote`
  ha risposto subito: la trappola di rete del 2026-09-01 non si è ripetuta.
- **1155 test verdi** con Docker acceso (**1017** senza), `dotnet format
  --verify-no-changes` esce 0, `dotnet docfx docfx/docfx.json` esce 0 con il
  solo warning **preesistente** su `version-history.md`, che è generato.
- **2 voci aperte** — P1 0 · P2 0 · P3 0 · P4 0 · P5 **2** — più 22 in «Deciso».
  `awk`: 24 righe di tabella meno le 22 decise fa 2.
- **Nessuna delle due è un difetto e nessuna è lavoro eseguibile da soli**: sono
  entrambe del proprietario. È la prima volta che il backlog arriva qui.

## Cosa è successo

| Commit | Effetto |
|---|---|
| `61754b8` | ③ chiusa: `RolledBack` in modalità `script`, misurato per la prima volta |
| `18ab52c` | ② chiusa: marker `-- dbdelta:transaction=none`, decisione del proprietario |
| `bfd87f7` | ① chiusa: `DeployPreflight` estratto, `ScriptGenerator` 1539 → 1352 |
| `11f1caa` | La review pre-push: 18 finding, 16 confermati, e **un difetto vero** |

---

## La lezione, ed è la sola cosa da portarsi dietro

Il filo delle sessioni precedenti era «**la voce di backlog ha ragione sul
difetto e torto sulla causa**» — sette volte su sette, poi nove su nove.
Il 2026-09-02 quel filo si è allungato di un anello che nessuno aveva previsto:

> **Anche la correzione è un'affermazione senza prove.**

La voce ③ diceva «`RolledBack` torna **sempre** `false` in modalità `script`».
Falso. Ma la **prima correzione**, scritta dopo sei misure vere su container,
diceva «a severità 16 `XACT_ABORT` aborta, a 11 no» — e sbagliava in due modi
che solo una passata avversariale indipendente ha trovato:

1. Il repo stesso, in **tre** posti, metteva la soglia a **14**, non a 16.
2. Due file dicevano già «la ragione è la severità, **non** `XACT_ABORT`»: la
   correzione contraddiceva il repo mentre credeva di allinearlo.

Rimisurando con un **controllo vero** (lo stesso caso col flag spento):

| Fallimento | sev | `@@TRANCOUNT` dopo | `XACT_ABORT` ON vs OFF |
|---|---|---|---|
| Msg 3701 «l'oggetto non c'è» | 11 | **1** | identico |
| Msg 15225 `sp_rename` | 11 | **1** | identico |
| Msg 4145 errore di **compilazione** | 15 | **1** | identico |
| Msg 3701 «permesso negato» | **14** | **0** | identico |
| Msg 2714 / 1767 / 8134 | 16 | **0** | identico |

Tre conclusioni, e nessuna delle due versioni precedenti le aveva:

- **Il numero di Msg non è il discriminante**: `3701` sta su **entrambi** i lati.
  È l'unico dato che isola la severità dalla forma dello script.
- **`XACT_ABORT` non è la leva**: dieci casi, acceso e spento, esito identico.
- **La severità è un buon predittore, non il meccanismo**: un errore di
  compilazione a 15 lascia la transazione aperta perché il batch non parte.

Quel che il flag dice davvero è **se la transazione era ancora aperta quando
l'esecutore l'ha chiesto**. E `true` **non salva il bersaglio, lo certifica**:
chiudere la connessione lo rollerebbe indietro comunque.

**Se riapri questo, devi battere la misura, non ridiscuterla.**

## Il difetto vero che la review ha trovato, ed era di un'ora prima

`SqlExecutor.ScriptDeclaresNoTransaction` faceva `Contains` su **tutto il file**
mentre il suo `<remarks>` prometteva «solo uno script che lo dichiara». Quei 27
caratteri arrivano dentro un commento copiato da una `CREATE PROCEDURE` o dentro
un letterale, e quella risposta **toglie e basta** la transazione del client:

```csharp
useOwnTransaction = !selfManaged && !declaredNoTransaction && !noTx;
```

Nessun termine che un operatore possa mettere a `true` — `--no-transaction`
toglie anche lui. Ora aggancia la **prima riga**. Il gemello
(`ScriptManagesItsOwnTransaction`) può permettersi di sovra-rilevare e lo mette
per iscritto; questo no, ed è la ragione dell'asimmetria.

---

## Trappole pagate, oltre a quelle dell'handoff del mattino

- **Una sonda che lascia stato dietro di sé risponde a un'altra domanda al giro
  dopo.** Le tabelle marker rimaste da una run precedente hanno fatto fallire il
  `CREATE` di setup con `2714` **prima** dell'istruzione in esame: la transazione
  era già andata e la sonda ha «misurato» l'opposto. Due volte. Ripulisci **per
  caso**, non una volta per run, e stampa se il setup è riuscito.
- **Le escape non sopravvivono a Python dentro un heredoc**: `\n` scritti in una
  stringa Python sono diventati newline reali dentro letterali C#, rompendo il
  file due volte. Per inserire codice, scrivi il blocco con
  `cat > file <<'BLOCK'` e splicalo **leggendolo dal file** — mai costruirlo in
  una stringa Python. E un heredoc con dentro un handoff lungo può rompere anche
  bash: per i documenti usa lo strumento di scrittura diretto.
- **Non tutti i file di questo repo hanno le stesse terminazioni di riga**: i
  `.cs` sono CRLF, i `.md` sono LF. Un'ancora costruita con `\n` non aggancia un
  file CRLF. Rileva con `b"\r\n" in raw` e converti l'ancora, oppure lavora per
  righe.
- `docker exec` sotto Git Bash vuole `MSYS_NO_PATHCONV=1`; `docker version` deve
  mostrare `Server:` (`docker ps` stampa l'intestazione anche a daemon morto).
- `--no-build` mente se il probe non compila: controlla gli errori **prima**
  dell'output.

---

## Da dove si riparte

**Il *cosa* sta in `docs/BACKLOG.md`.** Entrambe le voci aperte sono **del
proprietario** e nessuna è un difetto: non proporle come lavoro, chiedile.

### `NoTransactions` non è richiedibile da nessun front end · P5 · S

Aperta dalla review. `ScriptCommand` dichiara quattro `Option<>` e la sua unica
mutazione è `opts &= ~IgnorePermissions`; ogni altro call site passa
`ComparisonOptions.Default`. Il flag esiste, `ScriptGenerator` lo legge, il
writer emette il marker e `apply` lo onora — ma **l'unico modo di ottenere uno
script `=none` dai binari pubblicati è scrivere la riga a mano**.

- **Non è un difetto**: niente si comporta male, e `docfx/articles/cli.md` ora lo
  dice invece di promettere un verbo che non esiste.
- **Due strade, entrambe toccano superficie pubblica**: esporla con una
  `Option<bool>` su `script` (additiva, non rompe la 1.0.2 pubblicata), oppure
  cancellarla come è già stato fatto con `ProjectOptions`, morto per esattamente
  la stessa ragione — quella cancellazione è già in «Deciso».
- Finché non è decisa il ramo è tenuto vivo da **una sola** unit di wiring,
  `DeploymentScriptWriterTests.NoTransactions_reaches_the_writer_and_comes_out_as_the_marker`,
  scritta apposta perché non muoia in silenzio.

### Annuncio pubblico · P5 · escluso per scelta

Il draft è fermo a 1.0.1 mentre la release è 1.0.2. **Non proporlo come lavoro**:
resta in lista solo perché è un'azione ancora possibile.

---

## Cosa NON rilitigare

- **Il gate d'errore di batch** e la **legge sulla severità**: chiusi, misurati
  due volte, la seconda con un controllo. In «Deciso — NON riaprire».
- **Tutte le DROP sono nude**, e ne segue che uno script generato **non va
  rieseguito**: dopo un fallimento si ri-confronta e si rigenera.
- **I rifiuti diagnostici NON sono `Unscriptable*`**: il criterio di quella
  famiglia è «l'alternativa era un'istruzione valida che significa in silenzio
  un'altra cosa». Un fallimento rumoroso che rolla indietro non qualifica.
- **`DeployPreflight` prende cinque input e non ne ricalcola nessuno.** La forma
  `Scan(result, selection)` di `BackfillPreflight` è stata valutata e
  **scartata**: ricalcolerebbe `rebuildTargets`, cioè il difetto che quelle
  guardie chiudono.
- Archiviate: contratti JSON della CLI, trimming/94 MB, Sezione D, code signing.

## Puntatori lasciati marci apposta

`docs/review/2026-07-30-*` cita righe che non risolvono più
(`SqlExecutor.cs:189-201`, `ApplyCommand.cs:67`, `:114`, `:102`). Erano **già**
marci prima del 2026-09-02 e quei file sono archeologia datata, non riferimenti
vivi: sistemarne uno in un mare di rotti è teatro. I riferimenti **vivi** sono
stati riancorati ai nomi dei metodi (`docs/parity/redgate-2026-08-31.md`,
`docs/review/2026-08-14-improvement-scan.md`).
