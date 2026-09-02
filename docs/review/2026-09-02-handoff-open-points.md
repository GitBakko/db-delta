# HANDOFF — 2026-09-02: il gate d'errore di batch, e da dove si riparte

> **STORIA, non stato.** Questo file dice *perché* le cose sono state fatte
> così e *quali trappole sono state pagate*. **L'elenco di ciò che è aperto
> sta SOLO in `docs/BACKLOG.md`**, che è l'unica lista di lavoro del progetto:
> qui non è duplicato, e se un giorno i due divergono ha ragione il backlog.
> Ogni riga di stato qui sotto invecchia il commit dopo: riverificala con
> `git status -sb`, `git log -1` e un `awk` sulle righe di tabella.

**Da leggere per primo in una sessione nuova**, con `docs/BACKLOG.md` accanto.
`2026-08-16-handoff-post-scan.md` resta valido come storia dello scan migliorie;
`docs/parity/redgate-2026-08-31.md` è la matrice di parità, e la sua sezione
**R5 è superata da questo documento** — la misura del 2026-09-01 la corregge su
due punti, vedi «La misura, per intero».

---

## Stato al momento della scrittura

- **`main` = `origin/main` = `8a20e54`**, zero commit locali. I quattro commit
  della sera del 2026-09-01 sono stati pushati tutti insieme dopo che
  `github.com:443` era rimasto irraggiungibile per ore **mentre
  `api.github.com` rispondeva** — `gh` funzionava e `git push` no, e la
  diagnosi giusta era `git ls-remote`, non «la rete è giù».
- **CI verde su `8a20e54`**, entrambi i job: `33560365612` (ci, 4m19s) e
  `33560366060` (docs, 1m2s).
- **1144 test verdi** con Docker acceso (1009 senza), `dotnet format
  --verify-no-changes` esce 0, `dotnet docfx docfx/docfx.json` esce 0 con un
  solo warning **preesistente** su `version-history.md`, che è generato.
- **4 voci aperte** — P3 1 · P4 2 · P5 1 — più 22 in «Deciso». Ricontate con
  `awk`, non a mente: 26 righe di tabella meno le 22 decise fa 4.

## Cosa è successo in questa sessione

| Commit | Effetto |
|---|---|
| `5fc5acf` | Il gate d'errore di batch **rimisurato** e archiviato in «Deciso — NON riaprire»; il `<remarks>` di `DeploymentScriptWriter` e «Why no DROP is guarded» in `docfx/articles/cli.md` portano ora la misura. Nello stesso commit i **tre riferimenti marci** che i commit precedenti avevano creato |
| `8a20e54` | Due voci d'igiene **aperte con evidenza misurata**, non dedotte: `NoTransactions` disfatto da `apply`, e `RolledBack` mai misurato nella modalità `script` |

I tre riferimenti marci meritano una riga, perché sono l'esempio della regola:
`ScriptGenerator` era citato a **1527** righe nel backlog *e* in `CLAUDE.md`
mentre era a **1539** — a farlo crescere era stato `19fd51f`, cioè il commit che
scriveva la regola «non crescerlo in silenzio»; il run CI citato era vecchio di
tre run; e l'aritmetica che spiegava come contare le voci («29 meno 17 fa 12»)
non risolveva più. In più il paragrafo «per origine» elencava una P1 già chiusa
e **non nominava affatto la voce ancora aperta**.

---

## La misura, per intero

Il metodo è quello che in questo repo ha smentito sei voci su sei: **un harness
che chiama le classi vere con dati veri**, non una deduzione. Lo script di prova
è stato costruito dal **vero** `DeploymentScriptWriter` ed eseguito dal **vero**
`SqlExecutor`, su `mssql/server:2022-latest` **16.0.4265.3** (la stessa build
della parità).

### Cosa fa davvero il gate

`WriteBatch` scrive il corpo, poi `GO`, poi `IF @@ERROR <> 0 SET NOEXEC ON;`,
poi `GO`. Misurato:

1. **`@@ERROR` sopravvive al `GO`.** Il gate in batch separato **non** è un
   no-op: se il fallimento è l'ultima istruzione del batch precedente, legge il
   numero e scatta. Era la prima ipotesi da escludere, ed è falsa.
2. **È cieco appena segue un'istruzione riuscita**, nello stesso batch. Anche
   una `PRINT` azzera `@@ERROR`.
3. **È cieco su `EXEC` in ogni posizione, ultima compresa.** `sp_rename` fallito
   come ultima istruzione del batch lascia `@@ERROR` a **0**. Il fallimento
   viaggia nel **return code** (`rc = 1`; `rc = 0` quando riesce). Questo la
   voce non lo diceva.

### Perché non morde: è la severità, non `XACT_ABORT`

| Severità | Misurati | Il batch |
|---|---|---|
| **11** | Msg 3701 su table/view/procedure/function/index/sequence/synonym/trigger **assenti**; Msg 15225 e 15335 da `sp_rename` | prosegue, gate cieco, **committa** |
| **14** | Msg 3701 quando significa **«permesso negato»** | aborta, rolla indietro |
| **16** | 2714, 3726, 3727/3728, 4902, 8106, 218 (`DROP TYPE`), 15151 (`DROP SCHEMA`), 207 (`sp_refreshsqlmodule` che non lega più) | aborta, rolla indietro |

Quindi al livello 11 arriva **una cosa sola**: «l'oggetto che stavo per droppare
non c'è già più» — cioè la ri-esecuzione che la politica delle DROP nude
dichiara **già** non supportata. Il caso che avrebbe fatto danno davvero — il
DROP rifiutato per permessi, che lascia l'oggetto in piedi — è **livello 14 e
aborta**. È il motivo per cui la voce è finita in «Deciso» e non in codice.

**La mitigazione che la voce affermava era sbagliata**: diceva «il sopravvissuto
è seguito da un `ALTER TABLE … ADD CONSTRAINT` che dà Msg 4902». Non è la
ragione generale — la ragione è la classe di errore, non la forma dello script.

### L'asimmetria fra i due esecutori, e va tenuta a mente

Lo **stesso file**, sullo stesso server:

- via **`dbdelta apply` / GUI**: `Success=False`, `RolledBack=True`, bersaglio
  **intatto**. `Microsoft.Data.SqlClient` alza `SqlException` anche a severità
  11, quindi `SqlExecutor` si ferma a quel batch e il `COMMIT` non parte mai.
- via **SSMS / `sqlcmd`**: errore inghiottito, script committato, e stampa
  **`The database update succeeded`**.

Il verdetto `PRINT 'The database update failed'` **non lo legge nessuno**:
`SqlBatchResult.Success` dipende solo da «nessun batch ha lanciato», e
`SqlBatchResult.Errors` esiste ma serve solo a decorare una pill.

### Cosa NON è stato raggiunto, e va detto

**Nessuna forma in cui un errore inghiottito committi uno stato sbagliato.** Il
rebuild è protetto da un muro di livello 16 davanti a `sp_rename`: `CREATE TABLE
_tmp` duplicata è 2714, `DROP TABLE` con FK entrante è 3726, e `INSERT … FROM
[X]` con X sparita **non compila** (208, che uccide il batch prima che parta). Il
candidato peggiore, il refresh dei moduli, alza 207 e aborta. Se qualcuno
riapre la questione, deve battere questo, non ridiscuterlo.

---

## Trappole pagate misurando (costano un'ora se le riscopri)

- **`sqlcmd` NON è lo strumento per misurare un fallimento di batch.** Dopo il
  primo errore sotto `XACT_ABORT ON`, ODBC 18 stampa `SqlState 24000, Invalid
  cursor state` per **ogni batch successivo** e **perde tutte le `PRINT`**, che
  sono la misura. Sembra che i batch dopo non siano girati: mente. Misura con
  `Microsoft.Data.SqlClient` e un handler `InfoMessage`.
- **`--no-build` mente se il probe non compila:** stampa il verde dell'assembly
  **vecchio**. Due volte in questa sessione ho letto un risultato stantio.
  Controlla sempre il conteggio degli errori di build **prima** dell'output.
- **`"` non è una virgoletta in C#:** dà `CS1056`. Serve una costante.
- **Un'ancora `perl`/`python` con `\n` contro un file CRLF non aggancia.**
- **`docker exec` sotto Git Bash vuole `MSYS_NO_PATHCONV=1`**, o i path
  `/tmp/...` vengono riscritti.
- **`DENY ALTER ON OBJECT::` non impedisce una `DROP TABLE`**: il permesso che
  conta è ALTER sullo **schema** (o CONTROL sull'oggetto). Il primo tentativo di
  probe sui permessi misurava il nulla.
- **Un batch che non compila non esegue nemmeno le istruzioni prima
  dell'errore:** un `CREATE VIEW` non in prima posizione (Msg 111) ha fatto
  fallire in silenzio l'intero setup di un probe, e lo stato finale sembrava
  dire il contrario di quello che diceva.
- `docker ps` **non** dice se il daemon è vivo: `docker version` deve mostrare
  `Server:`.

### La ricetta del probe, se serve rimisurare

```bash
export MSYS_NO_PATHCONV=1
docker run -d --name dbdelta-probe -e ACCEPT_EULA=Y \
  -e "MSSQL_SA_PASSWORD=<scegline una, NON committarla>" \
  -e MSSQL_PID=Developer -p 14833:1433 mcr.microsoft.com/mssql/server:2022-latest
```

Poi un console project **nello scratchpad** (mai in repo) con
`ProjectReference` a `src/DbDelta.Persistence/DbDelta.Persistence.csproj`: dà
accesso a `DeploymentScriptWriter`, `SqlExecutor` e `ScriptManagesItsOwnTransaction`,
cioè si misura il prodotto e non un'imitazione. Servono due modalità di
esecuzione, perché le due che contano si comportano in modo opposto: «come
`SqlExecutor`» (si ferma alla prima eccezione) e «come SSMS» (prosegue al batch
successivo dopo un errore).

---

## Da dove si riparte

**Il *cosa* sta in `docs/BACKLOG.md`.** Qui c'è solo il *come*, il vincolo e il
primo passo di ciascuno. L'ordine consigliato è ③ → ② → ①.

### ③ P4 — `RolledBack` mai misurato in modalità `script` · XS · è un test

Nella sola modalità che uno script generato usa, `RolledBack` torna **`false`
col bersaglio provatamente intatto**. Corretto (è l'under-claim voluto di
`TryRollbackAsync`), ma **niente lo fissa**: le due asserzioni su server vero
coprono `client` e `none`.

- **Dove:** `DeployErrorHandlingTests` — è già il file dei fallimenti, sta su
  `LiveDbCollection` (nessun container in più), ha già `FreshDbAsync` e
  `ObjectExistsAsync`, ed è a 187 righe, lontano dal tetto di 500.
- **Il test deve asserire due cose insieme:** lo stato del bersaglio **e**
  `RolledBack`. Asserirne una sola fissa metà del contratto — ed è esattamente
  il motivo per cui il buco è sopravvissuto.
- **Attenzione alla geometria:** il test esistente
  `Failing_step_aborts_rolls_back_and_reports_failure` scrive **una istruzione
  per batch**, la sola forma in cui il gate non può perdere niente. Un test
  nuovo che copia quella forma non prova nulla di nuovo.

### ② P4 — `NoTransactions` disfatto da `apply` · S · prima una decisione

Misurato: uno script generato con `useTransaction: false` non porta né il marker
né `BEGIN TRANSACTION`, quindi `ScriptManagesItsOwnTransaction` torna false e
`ApplyCommand` calcola `useOwnTransaction = true` — il JSON dice `"transaction":
"client"`. Il flag di generazione è annullato se l'operatore non passa **anche**
`--no-transaction`.

- **La domanda è di prodotto, non di codice:** le due opzioni omonime devono
  parlarsi (per esempio un marker che dichiari «niente transazione, per
  scelta»), oppure resta com'è e si documenta come si è fatto per le DROP nude?
  Il JSON già dice `client`, quindi non mente a nessuno.
- **Primo passo: la risposta del proprietario.** Il codice viene dopo ed è
  piccolo in entrambi i casi.
- Nella stessa modalità il verdetto emette ancora `IF @@TRANCOUNT > 0 ROLLBACK
  TRANSACTION`, che sotto la transazione del client rollerebbe indietro **quella
  del client**: registrato come forma da guardare e **non** come difetto aperto,
  perché SqlClient si ferma molto prima del verdetto e in SSMS la transazione
  client non esiste. Non riaprirlo senza una misura che lo raggiunga.

### ① P3 — estrarre `DeployPreflight` · S · non è un difetto

`ScriptGenerator` è a **1539** righe (rimisurare, non citare questo numero).

- **Il vincolo è la voce stessa:** `RefuseRebuildsBlockedBySchemabinding`,
  `RefuseTypeDropsBlockedByABinder` e `TargetSideTypeUsers` stanno lì perché è
  **l'unico punto che tiene insieme** la decisione di rebuild, gli archi del
  target e le coppie da cui esce il set droppato. **Spostarle non deve
  significare rifarle su meno input**: è esattamente il difetto che hanno appena
  chiuso.
- **Primo passo:** scrivere la firma che riceve quei tre input e restituisce i
  blocchi, sulla forma di `BackfillPreflight`, che è già fuori. Se la firma non
  li prende tutti e tre, fermarsi.
- **Rete sotto:** 19 unit, 3 su container, 2 acceptance, 13 sonde di mutazione.

### ④ P5 — annuncio pubblico · del proprietario

**Escluso per scelta, non bloccato.** Il draft è fermo a 1.0.1 mentre la release
è 1.0.2. Non proporlo come lavoro: resta in lista solo perché è un'azione
ancora possibile.

---

## Cosa NON rilitigare

Sono decisioni prese, con il criterio scritto accanto. Riaprirle costa tempo e
non cambia nulla.

- **Il gate d'errore di batch è chiuso**, con la misura qui sopra. È in «Deciso
  — NON riaprire».
- **I rifiuti diagnostici NON sono `Unscriptable*`.** Il criterio di quella
  famiglia è: *l'alternativa era un'istruzione valida che significa in silenzio
  un'altra cosa*. Un fallimento **rumoroso** che rolla indietro non qualifica —
  `SchemaboundRebuildException` e `BoundTypeDropException` stanno fuori, exit 30
  via `ErrorCode.UnsupportedSchemaChange`.
- **Tutte le DROP sono nude**, e ne segue che uno script generato **non va
  rieseguito**: dopo un fallimento si ri-confronta e si rigenera.
- Archiviate: contratti JSON della CLI (non unificarli, romperebbero la 1.0.2
  pubblicata), trimming/94 MB, Sezione D, code signing.

## La regola che ha retto tutta la sessione

**Misura prima di scrivere, e misura anche il motivo per cui scarti una strada.**
In questa sessione la voce di backlog aveva ragione sul difetto e torto sulla
causa **di nuovo**, come le sei precedenti: il gate non è debole per il motivo
che dichiarava, e la mitigazione che si attribuiva non era quella vera. Le due
voci nuove sono state misurate **prima** di essere scritte, e tutte e due sono
uscite diverse dalla frase che le proponeva — una delle due l'avevo riassunta a
voce sbagliata io, dicendo «`RolledBack` non è mai asserito da un server vero»
quando `ApplyCommandTests` lo asserisce eccome, su container, con lo stato del
bersaglio: il buco vero era **una modalità su tre**.
