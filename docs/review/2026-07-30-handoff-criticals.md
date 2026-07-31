# HANDOFF — i 3 critical che droppano la produzione, poi S2 / S3 / S11

> **CHIUSO IL 2026-07-31.** Tutti e sei i finding di questo documento sono stati
> corretti in sette commit `2c7776d..e249d24`, ognuno con il suo probe di
> mutazione eseguito. Suite: **594 verdi** su 10 progetti (Compat esclusa, gira
> solo di notte), `dotnet format --verify-no-changes` esce 0, build senza avvisi.
> Il documento resta come **specifica di ciò che è stato corretto e perché** —
> la sezione «Cosa NON fare», le trappole di processo e l'appendice del
> 2026-07-31 valgono ancora. **Resta da fare prima di rc5: lo smoke live
> 243 → 242.**
>
> | # | Commit | Come è stato chiuso |
> |---|--------|---------------------|
> | C3 | `2c7776d` | Preflight in `LoadAsync`. Scoperto scrivendo il test: serve **anche** `SELECT` su `sys.sql_expression_dependencies`, concesso di default al solo `db_owner` — la remediation come l'avevo scritta all'inizio non produceva una connessione funzionante. Nota onesta: senza preflight un login cieco riceveva già `CatalogQueryFailed` dal `DependencyReader`, quindi la finestra davvero aperta era più stretta di quanto scritto sotto. |
> | C2 | `41bc5dd` + `112f516` | Comparer derivato dalla collation del target, nel motore **e** nell'emitter (la metà colonne è perdita di dati quanto quella tabelle). Trappola confermata: il comparer CI fa lanciare i `ToDictionary` su un database davvero CS che contiene entrambe le grafie → il motore rifiuta invece di scartarne una. |
> | C1 | `06343fa` | Staleness **derivata** dalla coppia di endpoint memorizzata, non un flag da ricordare su cinque uscite. Copre anche `ConnectionPickerSlot` e il caricamento progetto senza codice dedicato. Gate su **entrambi** i comandi. |
> | S2 | `8167187` | Due feeder (uscente + entrante) sul set di colonne toccate, ognuno con un test che lo isola. Il re-add ha riusato il meccanismo del rebuild invece di aggiungere `forcedFkRecreates`. |
> | S3 | `4a9ea0c` | **Una riga**: tolto lo skip dell'holder che viene droppato lui stesso. Msg 3726 non dipende più dall'ordine, quindi nemmeno da un ordinamento topologico. Gli archi target-side servono ora solo per la catena schemabound (Msg 3729), e sono passati. |
> | S11 | `e249d24` | `Sql.Q` ovunque, in entrambe le sintassi. La architecture test ha trovato un sito che il grep d'inventario aveva mancato; la regex di `ModuleHeader` e `Unquote` avevano il bug speculare in lettura. |

**Da leggere per primo in una sessione nuova.** Tutto quello che serve è qui o
nei due documenti citati; non serve ricostruire nulla.

- **HEAD:** `6aac709` su `main`, origin **sincronizzato**, working tree pulito.
- **Test:** 553 verdi, 3 skip di design (compat matrix, scheduled-only).
- **Gate formato:** `dotnet format DbDelta.sln --verify-no-changes` esce 0.
- **Chiuso finora:** i 4 blocker + S1 del handoff precedente
  (`docs/review/2026-07-30-handoff-blockers.md`, tutti verificati), i 6 test
  morti, e il batch a effort S: S4, S5, S6, S8, S9, S10, S13, S16.
- **Ri-verificato il 2026-07-31** su `d5e8c63`: tutti e sei i finding di questo
  documento sono ancora **aperti**, nessuno ha copertura di test, e i file:line
  citati sono ancora esatti. La verifica ha però aggiunto materiale — vedi
  l'**appendice** in fondo prima di iniziare un finding.

## Regola d'ingaggio

**Prima i 3 critical qui sotto, poi S2 e S3, poi S11.** Il motivo dell'ordine
non è la severità nominale: i tre critical sono gli unici difetti rimasti che
**distruggono dati di produzione senza che l'utente abbia chiesto niente**, e
due dei tre stanno fuori dal generatore di script (view-model e reader), quindi
non sono coperti da nessuno dei test che abbiamo indurito finora.

**NON iniziare W3-1 / W3-2 (undo) prima di S2 e S3**: sono i suoi due
prerequisiti duri. Un `down.sql` con l'ordine di DROP sbagliato è peggio di
nessun `down.sql`.

---

## Il protocollo che ha funzionato — usalo

Due regole, entrambe pagate a caro prezzo:

1. **Per ogni simbolo che tocchi, apri OGNI chiamante col `grep`.** Non a
   memoria, non rileggendo il proprio ragionamento. B1 (perdita silenziosa di un
   trigger di produzione) è esistito perché un commento diceva «questo pass legge
   il risultato non filtrato» — vero per la CLI, falso per la GUI, e nessuno ha
   aperto `DeployScriptBuilder` per controllare.

2. **Per ogni test nuovo, rispondi a "passerebbe anche senza il fix?" eseguendolo
   davvero.** Rimuovi il fix, ricompila, lancia, guarda il rosso, ripristina.
   Nella suite c'erano 6 test che non potevano fallire; sono stati trovati così,
   non leggendoli.

   **Trappola:** se il probe non compila, `dotnet test --no-build` gira
   sull'assembly PRECEDENTE e stampa **verde**. È successo tre volte in una
   sessione (IDE0051 membro privato inutilizzato, IDE0060 parametro inutilizzato,
   IDE0046). Controlla sempre `Errori: 0` del build del probe **prima** di
   credere al risultato. Il modo più sicuro di ripristinare il pre-fix di un file
   già committato è `git show HEAD:<path> > <path>`.

---

## C1 — critical — una ricomparazione fallita lascia i DROP eseguibili contro il NUOVO target

**File:** `src/DbDelta.App.Avalonia/ViewModels/AppStateViewModel.cs:193`
(`CompareAsync`), gate in
`src/DbDelta.App.Avalonia/ViewModels/MainWindowViewModel.cs:618` (`CanDeploy`) e
`:667` (`CanExecuteOnTarget`).

`CompareAsync` esce per errore su cinque rami distinti (`:214`, `:223`, `:233`,
`:240`, `:270`) scrivendo `LastError` e lasciando **`LastComparison` e
`LastComparisonRaw` intatti**. La griglia continua a mostrare le righe del
confronto precedente, con le spunte dell'utente ancora attive, e i due gate
guardano solo `Rows.Any(r => r.IsSelected)` e la stringa di connessione target —
nessuno dei due sa da quali endpoint vengono le righe.

**Danno concreto:** confronti DEV→STAGING, spunti 12 righe di cui 5 sono `DROP`,
poi ripunti la destinazione su PROD e premi Aggiorna. La sorgente è giù → il
compare fallisce → banner rosso, **griglia invariata**, `Esegui` abilitato.
Premi Esegui: i 5 `DROP` partono su PROD, calcolati contro un confronto che non
ha nulla a che vedere con quel server.

**Fix.** Legare i risultati agli endpoint che li hanno prodotti. Uno stato
`ResultsAreStale` (o meglio: memorizzare la coppia di endpoint del confronto e
confrontarla con quella corrente) che:
- diventa `true` su ogni uscita per errore di `CompareAsync` e su ogni cambio di
  `SourceConnectionString` / `TargetConnectionString` / `CurrentProject`;
- gatea **entrambi** i comandi (`CanDeploy` e `CanExecuteOnTarget`);
- si vede nella UI (le righe non devono sembrare fresche).

Attenzione: `partial void On…Changed` di CommunityToolkit è il posto naturale
per l'invalidazione, e ricordati `NotifyCanExecuteChanged()` — ci sono già 6 call
site che lo fanno (`MainWindowViewModel.cs:53, 58, 141, 497`).

- **effort M**
- **Test:** headless. `AppStateViewModel` con un risultato, poi cambio della
  connessione target → `vm.ExecuteOnTargetCommand.CanExecute(null)` deve essere
  `false`. Passa senza il fix? No: oggi resta `true`.

---

## C2 — critical — l'accoppiamento è case-sensitive e droppa la tabella di produzione

**File:** `src/DbDelta.Core/ObjectModel/Table.cs:28`
(`public readonly record struct ObjectIdentity(string SchemaName, string ObjectName, string Kind)`)
+ ogni `ToDictionary(x => x.Identity)` in
`src/DbDelta.Core/Diff/ComparisonEngine.cs` (`:66`, `:89`, e uno per kind).

`ObjectIdentity` è un record struct di stringhe: l'uguaglianza generata usa
`EqualityComparer<string>.Default`, cioè **ordinale**. SQL Server decide la
case-sensitivity dalla **collation del database**, che quasi sempre è CI.

**Danno concreto:** il target ha `dbo.CLIENTI`, la sorgente `dbo.Clienti` (stesso
oggetto per SQL Server). Il motore produce `OnlyInA` + `OnlyInB` → lo script
emette `DROP TABLE [dbo].[CLIENTI]` e `CREATE TABLE [dbo].[Clienti]`. **La
tabella di produzione viene droppata con i suoi dati e ricreata vuota**, e il
tool riporta successo.

**Fix.** La collation la leggiamo già: `Database.DefaultCollation`
(`src/DbDelta.Core/ObjectModel/Database.cs:65`), popolata da
`LiveDbSource.cs:32` / `:99` via `ReadDefaultCollationAsync` (`:132`). Serve un
`IEqualityComparer<ObjectIdentity>` scelto in base alla collation (nome che
contiene `_CI_` → OrdinalIgnoreCase) e passato a **ogni** dizionario/HashSet di
identità del motore. Decidere cosa fare quando le due collation divergono:
raccomandazione, usare la collation del **target** (è lui che subisce il DDL) e
segnalarlo.

Nota: le colonne hanno lo stesso problema dentro `TableScriptEmitter`
(`existingColsByName` / `newColsByName` sono `StringComparer.Ordinal`).

- **effort M**
- **Test:** due `Database` con `DefaultCollation = "SQL_Latin1_General_CP1_CI_AS"`
  e la stessa tabella con case diverso → **una** riga `Identical`, zero `OnlyIn*`.
  Passa senza il fix? No: oggi sono due righe.

---

## C3 — critical — nessun preflight di visibilità: gli oggetti invisibili diventano DROP

**File:** `src/DbDelta.Providers.LiveDb/LiveDbSource.cs:25` (`LoadAsync`).

`LoadAsync` legge il catalogo e restituisce quello che vede. `sys.tables` &
soci sono filtrate dalla **metadata visibility**: un principal vede solo gli
oggetti su cui ha un permesso.

**Danno concreto:** il requisito SICURO *incoraggia* un login a privilegio
minimo. Se la connessione alla sorgente ha SELECT su 3 tabelle di 50 e quella al
target è sysadmin, il motore vede 3 oggetti da una parte e 50 dall'altra → **47
`DROP TABLE`** su produzione, presentati come differenze legittime.

**Fix.** Preflight in `LoadAsync` che fallisce **forte** (`Result.Failure`, non un
warning): asserire `HAS_PERMS_BY_NAME(NULL, NULL, 'VIEW ANY DEFINITION')` o
equivalente, e/o confrontare `COUNT(*)` su `sys.objects` con quello ottenuto da
una vista non filtrata. Il messaggio deve dire *quale* endpoint e *cosa*
concedere. Nessun grep serve qui: `LoadAsync` ha un solo comportamento e i
chiamanti (CLI `CompareCommand`/`ScriptCommand`, `AppStateViewModel.CompareAsync`)
gestiscono già `Result` fallito.

- **effort M**
- **Test:** integrazione Testcontainers — crea un login con SELECT su una sola
  tabella, `LoadAsync` deve fallire. Docker c'è e la suite di integrazione gira
  (19 test verdi in ~27 s).

---

## S2 — nessuna FK viene droppata per liberare una colonna ritipizzata

**File:** `src/DbDelta.Core/ScriptGen/ScriptGenerator.cs:134-190` (i tre feeder
del pass di drop), `TableScriptEmitter.cs:224` (sezione 1 fa `continue` sulle
FK), `TableScriptEmitter.cs:~385` (`DependsOnColumn` non ha un ramo ForeignKey).

I tre feeder del pass "droppa tutte le FK all'inizio" sono: (a) FK che una
tabella Different ha perso o rimodellato, (b) FK che puntano a una tabella
droppata, (c) FK che puntano a una tabella ricostruita. **Nessuno è indicizzato
su `ColumnsDroppedOrAltered`.** Quindi il classico allargamento `int → bigint`
di una PK referenziata muore: Msg 3725 sul `DROP CONSTRAINT [PK_…]`, oppure
Msg 5074 sull'`ALTER COLUMN` della tabella figlia.

È **esattamente la migrazione per cui `275660a` è stato scritto** — quel commit
ha risolto la metà indici e ha lasciato la metà FK.

**Fix.** Un quarto feeder simmetrico a `blockingIndexDrops`: per ogni tabella
Different, le FK (proprie e in entrata) che toccano una colonna in
`touchedColumns` vanno droppate all'inizio. Serve anche il gemello di
`forcedIndexRecreates` — chiamalo `forcedFkRecreates`, chiave
`(schema, table, name)` come le altre — perché `EmitFkAdds` salta le FK
invariate: senza il forzamento la FK sparisce dalla produzione in silenzio.
Il pattern è già scritto due volte nel file, copialo.

- **effort M**

## S3 — l'ordine "reverse-topologico" del pass di DROP è vacuo

**File:** `src/DbDelta.Core/ScriptGen/ScriptGenerator.cs:284`
(`createOrder.Reverse()`), `src/DbDelta.Core/Dependency/DependencyResolver.cs:62`.

Ogni chiamante passa archi **source-side** e un oggetto `OnlyInB` (rimosso dalla
sorgente) non compare in nessuno di essi. Quindi il DROP pass non ordina un bel
niente: cade sul `KindRank` invertito (Function prima di View prima di Table).
In più `DependencyResolver` scarta gli archi ForeignKey e `DependencyReader` non
ne emette: due tabelle target-only legate da FK vengono droppate in ordine
alfabetico, cioè a testa o croce rispetto a Msg 3726.

`AppStateViewModel.cs:87` conserva già `TargetDependencies` e il commento dice
che non è usata da nessuno: è l'input che manca.

**Fix.** Passare gli archi target-side al pass di DROP (firma di `Generate`, poi
i 3 chiamanti reali: CLI `ScriptCommand`, `DeployScriptBuilder`, i test) e non
scartare gli archi FK nel resolver. **Da fare prima dell'undo**: un ordine di
DROP sbagliato in avanti diventa un ordine di rollback sbagliato.

- **effort M**

## S11 — nessun quoting di identificatori, in nessun emitter

**File:** tutti gli emitter (`grep -rn 'QuoteName\|EscapeIdentifier' src/` non
restituisce niente).

Ogni emitter scrive `$"[{schema}].[{name}]"` senza raddoppiare le `]`. Un nome
con una parentesi quadra chiusa rompe lo script; nel caso peggiore è iniezione
attraverso valori di catalogo, che il requisito SICURO vieta esplicitamente.
Sistemico: aggiustare un solo emitter non compra niente.

**Fix.** Un `static string Q(string identifier)` (raddoppia `]`, avvolge in `[…]`)
in `DbDelta.Core.ScriptGen`, e **tutti** i `[{…}]` instradati lì. Poi un
architecture test o un test di analisi del sorgente che fallisce se ricompare un
`[{` interpolato a mano — senza il test la terza copia tornerà.

- **effort M**

---

## Cosa NON fare

- I 4 finding confutati in fondo a
  `docs/review/2026-07-30-self-diff-adversarial-review.md`.
- **Non** passare `useOwnTransaction: true` per gli script generati: darebbe
  `@@TRANCOUNT = 2` e il `COMMIT` dello script diventerebbe un decremento.
- **Non** togliere il fallback sintattico di `ScriptManagesItsOwnTransaction`
  lasciando solo il marker di provenienza: gli script generati **prima** di
  `6aac709` non hanno il marker e finirebbero avvolti in una transazione client.
  Vedi il messaggio di quel commit.
- XXE nel project store XML, XSS nel report HTML, gestione culture e MSI sono
  stati verificati **puliti**: non ri-auditarli.

## Ancora aperto, noto, non urgente

- `ScriptManagesItsOwnTransaction` sovra-rileva su script **estranei** (un
  `BEGIN TRANSACTION` a inizio riga dentro un commento a blocco o in un corpo di
  procedura). Asserito come limite noto in `SqlExecutorTests`. Serve un parser.
- Spuntare la riga di uno schema senza il suo contenuto → Msg 3729. Rumoroso.
  È una domanda di prodotto ("droppare lo schema implica droppare ciò che
  contiene?"), non un bug.
- Il resto della lista S: S7, S12, S14, S15 (tutti effort S), poi F-2
  (round-trip per kind: oggi copre 3 kind su 13).

## Trappole di processo già pagate

- **Il tool Bash è Git Bash, non PowerShell.** Le here-string `@'…'@` finiscono
  dentro il messaggio di commit. Usare `git commit -F - <<'EOF'`. Trailer
  `Co-Authored-By`.
- **Un hook sporca la root del repo a ogni edit** con una lambda C#: `=>` in
  shell non quotata è una redirezione, quindi `l => l.Trim()` crea il file
  `l.Trim()`. In questa sessione ne ha creati 5 (`0`, `,+`, `rebuild`,
  `dropFk\``, `x.Identity.Kind`, `p.Identity.Kind`). Sono vuoti. **Non usare
  `git add -A`**: due sono finiti in un commit e sono stati tolti con `--amend`.
  Aggiungere i path espliciti, o `rm -f` prima. Vale la pena sistemare il
  quoting dell'hook.
- **CI gatea duro** su `dotnet format --verify-no-changes` + `TreatWarningsAsErrors`.
  Analizzatori che mordono: **IDE0046** (ternario al posto di if/return),
  **IDE0061** (corpo a blocco per le funzioni locali), **IDE0062** (funzione
  locale statica), **IDE0051/IDE0060** (membro/parametro inutilizzato — mordono
  soprattutto durante i probe), **IDE0028**, **IDE0047**, **CA1062**.
- Il tool Write emette LF: `dotnet format` prima di ogni commit. `--` è illegale
  nei commenti XML. Se documenti UN parametro con `<param>`, devi documentarli
  **tutti** (CS1573) — per una nota su un solo parametro usa un commento `//`.
- **Verify** crea un `.verified.txt` vuoto da 3 byte al primo run: `mv -f` del
  received sopra. I due golden che passano da `Generate` si muovono a ogni
  cambio del preamble.
- `DbDelta.ScriptGen.GoldenTests` non ha `xunit.assert`: per far fallire un test
  lì, lanciare un'eccezione.
- Push automatico a fine lavoro verificato; **tag e release solo con l'ok del
  proprietario**. Terminologia UI: sempre **"Carica"**, mai "Apri".
- Endpoint live per smoke/parity: `192.168.3.243` e `.242`; password sa chiesta
  ogni volta, **mai** salvata.

## Definizione di "fatto"

```bash
dotnet build DbDelta.sln -c Debug              # 0 errori, 0 avvisi
dotnet test DbDelta.sln                        # 553 + i nuovi, 0 rossi
dotnet format DbDelta.sln --verify-no-changes  # esce 0
```

Più, per ogni fix: **aver aperto ogni chiamante del simbolo toccato** (`grep`,
non memoria), e per ogni test nuovo aver **eseguito** il probe di mutazione
verificando prima che il probe compili.

## Prima di taggare rc5

I 6 commit dei blocker e gli 8 del batch S **cambiano cosa emette la GUI**
(FK di tabelle non selezionate, trigger ricreati, schemi promossi, marker di
transazione). Nessun golden copre la forma del path GUI — il pass di revert
della review lo ha dimostrato. **Smoke live 243 → 242 prima del tag.**

---

# APPENDICE — ri-verifica del 2026-07-31 su `d5e8c63`

Sei scanner indipendenti + sei contestatori adversariali, uno per finding, con
il mandato opposto (allo scanner: «trova il fix»; al contestatore: «trova il
buco»). **Verdetto unanime: sei finding su sei ancora aperti, zero test.** Sotto
solo ciò che il corpo del documento non dice già.

## C1 — cosa si è aggiunto

- **Un secondo punto d'ingresso, oggi latente.** Il setter di
  `ConnectionPickerSlot.ConnectionString`
  (`src/DbDelta.App.Avalonia/ViewModels/ConnectionPickerSlot.cs:17-23`, guidato
  da `OnSelectedEntryChanged` `:35-47`) ripunta `AppState.TargetConnectionString`
  **senza tentare alcun confronto**: righe e spunte stantie sopravvivono senza
  nemmeno bisogno di un compare fallito. Non è raggiungibile oggi
  (`ConnectionPickerView.axaml` non è istanziata da nessuna finestra) ma il fix
  deve coprirlo, non solo i tre writer vivi (`App.axaml.cs:88-108`,
  `MainWindowViewModel.cs:296-306` apertura progetto, `:539-547` modifica).
- **Il notify è già asimmetrico.** Su cambio del target
  (`MainWindowViewModel.cs:51-54`) viene notificato **solo**
  `ExecuteOnTargetCommand`; `DeployCommand` no. Gatendo un comando solo si lascia
  vivo l'altro.
- **Il dialog di conferma non è una mitigazione.** `MainWindowViewModel.cs:650-651`
  passa a `ConfirmExecuteViewModel` la redazione delle connessioni **correnti** —
  cioè PROD, esattamente ciò su cui l'utente ha appena ripuntato — mai gli
  endpoint da cui il diff è stato calcolato. Strutturalmente non può accorgersene.
  E `DeployAsync` (salvataggio script, gatato dal solo `CanDeploy`) non mostra
  alcun dialog.
- **Nessuna difesa a valle.** Grep su tutto `src/DbDelta.Core/` per
  `DB_NAME|@@SERVERNAME|SERVERPROPERTY|RAISERROR`: zero. Lo script emesso non
  porta nessuna asserzione d'identità del target, e
  `SqlExecutor.ExecuteAsync` (`src/DbDelta.Persistence/Sql/SqlExecutor.cs:74`)
  non prende un parametro "database atteso".

## C2 — una trappola che il fix ingenuo fa esplodere

**L'uguaglianza ordinale di oggi è ciò che impedisce a `ToDictionary(x => x.Identity)`
di lanciare.** Nel momento in cui il comparer diventa case-insensitive, un
database con collation **davvero CS** che contiene sia `dbo.Clienti` sia
`dbo.CLIENTI` fa lanciare `ArgumentException` (chiave duplicata) a **tutti** i
~12 `ToDictionary` del motore, invece di confrontare. Quindi: derivare il
comparer **strettamente** dalla collation (rilassare solo su `_CI_`) e/o passare a
una forma `TryAdd`/raggruppamento. Non basta scambiare il comparer.

Altri fatti verificati:

- `IEqualityComparer` **non compare da nessuna parte** nel repo (né `src/` né
  `tests/`). `ObjectIdentity` non è `partial`: un override di `Equals`/`GetHashCode`
  in un altro file non è solo assente, è impossibile senza toccare
  `Table.cs:28`.
- `Database.DefaultCollation` è **letta e buttata**: 5 occorrenze in `src/`, la
  dichiarazione (`Database.cs:65`) e tre in `LiveDbSource.cs` (`:32`, `:99`,
  `:132`). Zero letture in `Diff/` o `ScriptGen/`. Il commento di documentazione
  a `Database.cs:59-64`, che dice che il generatore la usa per emettere le
  clausole `COLLATE`, **è falso**, e
  `tests/DbDelta.Core.UnitTests/ScriptGen/ColumnCollationTests.cs:40-41` imposta
  la property senza che l'emitter la legga — passerebbe anche cancellandola.
- **Un test verde fissa il difetto:**
  `tests/DbDelta.Core.UnitTests/ObjectModel/TableTests.cs:10-18`
  `Identity_combines_schema_and_name_case_sensitively_by_default` asserisce
  `t1.Identity.Should().NotBe(t3.Identity)` per `dbo.Customer` vs `dbo.customer`.
  Va riscritto **insieme** al fix, non dopo.
- Siti di accoppiamento da toccare, ai numeri di riga correnti: `ComparisonEngine.cs`
  `:66` (schemi), `:89` (utenti), `:117` (ruoli), `:192`, `:226`, `:252`, `:283`,
  `:322` (tabelle), `:341` (moduli, serve viste/proc/funzioni), `:612` (trigger),
  più i permessi su chiavi stringa ordinali a `:164-170`. Colonne/vincoli/indici
  sono ordinali a `:306`, `:425`, `:501`, `:557` e in
  `TableScriptEmitter.cs:209-212`.
- Nessun path parzialmente sistemato: i quattro costruttori del confronto
  (`AppStateViewModel.cs:245`, `CompareCommand.cs:66`, `ReportCommand.cs:79`,
  `ScriptCommand.cs:80`) sono identici.

## C3 — il cablaggio esiste già, manca solo la chiamata

- `ErrorCode.InsufficientPermissions` (`src/DbDelta.Core/Abstractions/Result.cs:11`)
  è già mappato a **exit code 11** (`src/DbDelta.Cli/CliErrorMapper.cs:23-24`,
  `src/DbDelta.Cli/ExitCodes.cs:11`) e **non è costruito da nessuna parte** in
  `src/`. Membro morto in attesa del preflight.
- `LiveDbSource` ha già `DisplayName` (`:20`/`:23`) e i chiamanti passano già
  `"source"`/`"target"`: il messaggio d'errore può nominare l'endpoint senza
  lavoro extra.
- **Perché nessun catch può salvare:** i tre catch (`LiveDbSource.cs:103-122`)
  intercettano `SqlException`. Il filtro di metadata visibility **non è un
  errore** — `SELECT ... FROM sys.tables` riesce e restituisce meno righe. Il
  difetto è strutturalmente invisibile alla gestione errori esistente.
- Una `PreflightAsync` privata attesa fra `LiveDbSource.cs:29` e `:32` gatea
  tutti e 13 i reader e tutti e 4 i chiamanti con un unico guard.
- Attenzione: `docs/03_core_modules.md:175` e `:301` **descrivono il preflight
  come se esistesse** (`fn_my_permissions`/`HAS_PERMS_BY_NAME`), e
  `docs/01_architecture.md:270`/`:1438` enunciano il requisito di permessi.
  Documentazione di un'intenzione mai implementata — vanno allineati col fix.
- Nessuna rete a valle: `ComparisonEngine` classifica `(null, _) => OnlyInB`
  meccanicamente (`:101`, `:129`, `:178`, `:204`, `:238`, `:264`, `:295`, `:391`),
  senza euristica di volume. `ConfirmExecuteViewModel.OnlyInTargetCount`
  **mostra** il conteggio gonfiato come legittimo, e la CLI `script` non ha
  nemmeno quello.

## S2 — riprodotto empiricamente

Probe eseguito contro il `ScriptGenerator` reale: padre `dbo.Testa` con `Id`
`int → bigint` e `PK_Testa` clustered, figlio `dbo.Righe` con `TestaId`
`int → bigint` e `FK_Righe_Testa` **byte-identica sui due lati**, entrambe le
coppie `Different`, nessuna tabella droppata né ricostruita. Lo script emesso
**non contiene alcun batch «Dropping foreign keys»**: va dal preambolo diritto a
`ALTER TABLE [dbo].[Righe] ALTER COLUMN [TestaId] [bigint] NOT NULL;` (Msg 5074)
e poi `ALTER TABLE [dbo].[Testa] DROP CONSTRAINT [PK_Testa];` (Msg 3725).

Il perché, ai numeri correnti:

- `AddFkDrop` (`ScriptGenerator.cs:136-150`) ha **esattamente due** call site,
  `:163` e `:188`.
- Feeder (a) `:152-166` raccoglie solo se `!stillThere || !ForeignKeyShapeEqual`
  (`:161`) → una FK di forma invariata non entra mai.
- Feeder (b)+(c) `:168-192` sono dentro
  `if (droppedTables.Count > 0 || rebuildTargets.Count > 0)` → con nessuna
  tabella droppata né ricostruita **il blocco non gira affatto**, perché
  `RequiresFullRebuild` (`TableScriptEmitter.cs:438-453`) scatta **solo** sui
  cambi di identity: un `int → bigint` semplice non produce mai un rebuild.
- Il pass sulle colonne toccate è `:227-245` (`touched` a `:235`) e itera solo
  `sideB.Indexes` (`:237`). `sideB.Constraints.OfType<ForeignKey>()` mai.
- `forcedFkRecreates` non esiste: grep di `forcedFk|blockingFk|FkRecreate` su
  tutto il repo colpisce solo i `docs/review/*.md`.
- **Il gemello serve davvero:** `EmitFkAdds` (`:976-994`) ha
  `if (existsOnTarget && !shapeChanged) { continue; }` a `:990` senza override —
  confronta con `:357`, che invece infila `forcedIndexRecreates` in
  `EmitIndexDelta`. Senza il forzamento il fix è **peggio** del bug: la FK viene
  droppata e mai ri-aggiunta.
- Nessun altro path può droppare quella FK: i tre soli `DROP CONSTRAINT` in
  `src/` sono `ScriptGenerator.cs:271`, `TableScriptEmitter.cs:237` (sezione 1,
  che però esclude le FK a `:226`) e `TableScriptEmitter.cs:494` (dentro
  `EmitRebuild`, che richiede `RequiresFullRebuild`).
- Nessun test è in grado di diventare rosso: `ColumnDependencyOrderingTests.cs`
  ha 8 fact e l'unica occorrenza di `ForeignKey|FK` è un commento a `:125`;
  `ForeignKeyDropOrderingTests.cs` ha 5 fact, tutti su tabella referenziata o
  forma della FK.
- **Da correggere col codice:** il commento XML di classe a
  `ScriptGenerator.cs:17-21` dichiara «EVERY foreign-key drop» ed elenca proprio
  i tre feeder esistenti. La clausola d'apertura è falsa.

## S3 — la stima ottimista è confutata, non ridimensionata

Il corpo del documento e un primo passaggio di analisi ipotizzavano che il pass
di drop FK anticipato (`ScriptGenerator.cs:265-274`, da `ec6eb83`) **mascherasse
già** il caso Msg 3726 delle due tabelle target-only. **Falso, provato eseguendo
il generatore**: padre `dbo.Zone`, figlio `dbo.Alpha`, `FK_Alpha_Zone`, entrambe
`OnlyInB`. Lo script mette `DROP TABLE [dbo].[Zone];` **prima** di
`DROP TABLE [dbo].[Alpha];` e **non emette alcun** `DROP CONSTRAINT
[FK_Alpha_Zone]`, perché `ScriptGenerator.cs:174` salta deliberatamente un
holder che sta a sua volta per essere droppato. Msg 3726 al deploy. La
sopravvivenza dipende interamente dall'ordine alfabetico inverso.

- Il test esistente **fissa il comportamento vulnerabile**:
  `ForeignKeyDropOrderingTests.cs:88`
  `A_dropped_table_does_not_get_a_pointless_drop_for_its_own_fk` usa
  Currency/Invoice, che ordinano in modo fortunato (figlio `Invoice` > padre
  `Currency`, quindi il reverse droppa prima il figlio) e asserisce solo la
  **presenza** dei due `DROP TABLE`, mai il loro ordine relativo.
- **Sfilare il filtro nel resolver non basta:** nessun arco `ForeignKey` viene
  mai **costruito**. `new DependencyEdge` compare una volta sola in tutto `src/`,
  `DependencyReader.cs:60-63`, cablato su `EdgeKind.ModuleReference`;
  `sys.foreign_keys` è letta solo da `ConstraintReader.cs:47` e
  `LiveDbObjectBodyResolver.cs:428`, che popolano `Table.Constraints`. Serve
  anche il lavoro sul reader.
- `DependencyResolverTests.cs:60-70` `Foreign_key_edges_are_ignored` fissa il
  comportamento vecchio: va riscritto col fix.
- **Avvertenza sul resolver:** rimuovere il filtro FK in blocco fa lanciare
  `DependencyCycleException` sulle tabelle auto-referenziate — il pass di CREATE
  si affida al fatto che le FK siano differite. Lo sfilamento va limitato alla
  chiamata d'ordinamento del DROP.
- `TargetDependencies` è ancora write-only: dichiarata a
  `AppStateViewModel.cs:87`, assegnata a `:255`, letta da nessuno.

## S11 — conteggio reale e un sito in più

- **52** occorrenze di `[{` su **19** file in `src/`, di cui ~**42** finiscono in
  DDL emesso (le altre sono etichette: `PhaseLabel` `:530-531`, le label di
  `WriteBatch` `:331/:350/:360/:428/:448/:458/:903`, `Permission.cs:28`).
- **In più**, una decina di righe di emissione a `StringBuilder`
  (`Append("[")`, `Append("].[")`), concentrate in `TableScriptEmitter` e
  `ForeignKeyScriptEmitter` (p.es. `TableScriptEmitter.cs:31`, `:68`): stesso
  difetto in sintassi diversa, che **un grep di `[{` non vede**. Il fix va
  cercato con entrambi i pattern.
- L'unico helper è `TableScriptEmitter.Bracket` (`:119`,
  `$"[{identifier}]"`): avvolge senza raddoppiare, è `private` e ha 3 chiamanti
  (`:70`, `:591`, `:592`). Non è un fix nemmeno lì.
- Zero escaping in `src/`: i soli `]]` sono una classe di caratteri regex
  (`ModuleHeader.cs:36`) e un indice di array (`KindCatalog.cs:76`).
- Nessuna validazione al confine: grep di
  `IsValidIdentifier|ValidateName|Sanitiz|Contains(']')|QUOTENAME` su `src/` non
  restituisce nulla. I nomi arrivano grezzi dal catalogo.
- **Sito nuovo, lato lettura:** `ModuleHeader.cs:129-130`
  `Unquote` toglie le parentesi esterne **senza** ricomprimere `]]`, quindi un
  `[Ev]]il]` correttamente quotato diventa `Ev]]il`: il confronto di staleness a
  `:82` sbaglia e `AlignNameToCatalog:90` innesta poi `$"[{schema}].[{name}]"`
  non escapato dentro un corpo di modulo vivo.
- **I property test non possono catturarlo:**
  `tests/DbDelta.Property.Tests/Arbitraries/SchemaArbitraries.cs` genera nomi solo
  da `_schemas = ["dbo","stage","audit"]` (`:17`) e dai template `T_{suffix}`
  (`:53`), `C_{i:D3}` (`:62`), `v_`/`usp_`/`fn_`/`seq_`. Nessun generatore può
  emettere un `]`.
- La casa del test di guardia esiste già: `tests/DbDelta.Architecture.Tests/`,
  che oggi contiene il solo `LayeringTests.cs` con 3 test NetArchTest.
- Precedente utile: `HtmlReportGeneratorTests.cs:107`
  `Schema_and_object_names_are_html_encoded_to_avoid_injection` dimostra che il
  progetto ha già capito che i valori di catalogo sono non fidati — e lo ha
  applicato al sink HTML lasciando grezzo quello SQL.

## Riepilogo degli artefatti falsi o morti trovati strada facendo

Da correggere insieme al finding che li tocca, non separatamente:

| Artefatto | Problema |
|---|---|
| `Database.cs:59-64` (commento) | Dice che il generatore usa `DefaultCollation` per le clausole `COLLATE`. Non la legge nessuno. (C2) |
| `ColumnCollationTests.cs:40-41` | Imposta `DefaultCollation`, l'emitter non la legge: passerebbe anche cancellando la property. (C2) |
| `TableTests.cs:10-18` | Test verde che **fissa** l'accoppiamento case-sensitive. (C2) |
| `ErrorCode.InsufficientPermissions` | Mappato a exit 11, costruito da nessuna parte. (C3) |
| `docs/03_core_modules.md:175,:301` | Descrivono il preflight permessi come implementato. (C3) |
| `ScriptGenerator.cs:17-21` (commento) | «EVERY foreign-key drop»: falso, i feeder sono tre e nessuno guarda le colonne. (S2) |
| `ForeignKeyDropOrderingTests.cs:88` | Nomi che ordinano in modo fortunato + asserisce solo la presenza: fissa il comportamento vulnerabile. (S3) |
| `DependencyResolverTests.cs:60-70` | `Foreign_key_edges_are_ignored` fissa il comportamento vecchio. (S3) |
| `AppStateViewModel.cs:87`/`:255` | `TargetDependencies` write-only. (S3) |
