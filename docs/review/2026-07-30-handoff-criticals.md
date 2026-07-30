# HANDOFF — i 3 critical che droppano la produzione, poi S2 / S3 / S11

**Da leggere per primo in una sessione nuova.** Tutto quello che serve è qui o
nei due documenti citati; non serve ricostruire nulla.

- **HEAD:** `6aac709` su `main`, origin **sincronizzato**, working tree pulito.
- **Test:** 553 verdi, 3 skip di design (compat matrix, scheduled-only).
- **Gate formato:** `dotnet format DbDelta.sln --verify-no-changes` esce 0.
- **Chiuso finora:** i 4 blocker + S1 del handoff precedente
  (`docs/review/2026-07-30-handoff-blockers.md`, tutti verificati), i 6 test
  morti, e il batch a effort S: S4, S5, S6, S8, S9, S10, S13, S16.

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
