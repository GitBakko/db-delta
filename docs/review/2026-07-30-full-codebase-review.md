# DbDelta — Review completa del codebase (2026-07-30)

**HEAD:** `19131d8` · **versione:** v1.0.0-rc4 · **test:** 455 · **LOC:** ~17k src / ~10k test

**Metodo.** 8 reviewer paralleli (una dimensione ciascuno, lettura reale dei file — nessuna
speculazione), 8 verificatori **adversariali** istruiti a *confutare* ogni finding leggendo il
codice citato e i test, 1 progettista dell'architettura undo, 1 critico di completezza.
102 finding grezzi → **96 sopravvissuti** alla confutazione, **6 confutati**.
Dopo dedup fra dimensioni (20 duplicati, vedi Appendice B): **~84 difetti unici** + 62 migliorie.
Ogni finding qui riportato ha uno scenario di fallimento concreto e un `file:riga`.

I 4 fix a effort S più urgenti li ho poi **riverificati io stesso** aprendo i file: tutti e 4
confermati (uno con un aggravante che i reviewer non avevano visto — vedi W0-1).

---

## 1. Verdetto

DbDelta è **architetturalmente sano** e per certi aspetti migliore di quanto la mole di finding
suggerisca: il modello a oggetti è pulito, i reader sono query batch (non N+1), l'envelope dello
script generato è parità-Redgate e la sua atomicità *durante* il deploy è provata empiricamente.
La copertura di test è ampia (455).

Ma **non è pronto per essere puntato su un database di produzione**, per tre motivi distinti:

1. **Il deploy generato fallisce, o peggio riesce a metà del suo scopo, su casi banali.**
   Un `CREATE TABLE` con un DEFAULT nominato emette T-SQL sintatticamente invalido — e c'è un
   **golden test che lo asserisce come corretto**. La migrazione più comune del mondo (aggiungi
   una colonna `NOT NULL` con default a una tabella popolata) fallisce con Msg 4901. Un deploy
   verso uno schema che non esiste sul target fallisce alla prima istruzione perché
   `CREATE SCHEMA` non viene mai emesso. Lo script della GUI non ha **nessun** ordinamento
   topologico (`DeployScriptBuilder` non passa gli archi di dipendenza).
2. **In alcuni casi dice "Identici" quando non lo sono, e in altri distrugge dati senza
   avvisare.** Un indice columnstore assente su un lato → "Identici". Un trigger `INSTEAD OF`
   su vista → invisibile. Un login a privilegio minimo sulla sorgente → 47 tabelle di produzione
   diventano `DROP TABLE`. Un rebuild per IDENTITY droppa la tabella e **non ricrea indici né
   trigger**, riportando "successo".
3. **Il requisito RESILIENTE non ha implementazione.** Nessun down-script, nessuno snapshot,
   nessun backup, nessun journal. `grep -rni "backup" src/` → zero.

Nessuno di questi è un problema di *design*. Sono tutti buchi puntuali in un impianto corretto,
e la maggior parte dei fix ad alto impatto è effort **S**. Il piano sotto è ordinato perché
l'ondata W0 (un giorno di lavoro) chiude 8 difetti di cui 3 critical.

### Nota di equità: ciò che è già giusto

Il critico e i verificatori hanno *smontato* diverse ipotesi di rischio. Vanno messe a verbale
perché evitano lavoro inutile:

- **L'atomicità del deploy esiste ed è corretta.** `DeploymentScriptWriter` emette
  `SET XACT_ABORT ON` + `SERIALIZABLE` + `BEGIN TRANSACTION`, con gate
  `IF @@ERROR <> 0 SET NOEXEC ON` per operazione e verdetto finale con `ROLLBACK` condizionale.
  I due chiamanti reali passano `useOwnTransaction: false`, quindi non c'è doppia transazione.
  Se il batch 7 di 20 fallisce, il DB **non** resta a metà. Provato da
  `DeployErrorHandlingTests` + `SqlExecutorTests`. **Non "migliorare" questo passando a
  `useOwnTransaction: true`**: darebbe `@@TRANCOUNT = 2` e il `COMMIT` dello script diventerebbe
  un decremento, con il client che poi committa lavoro che lo script credeva bloccato dal gate.
- **XXE nel project store XML: non vulnerabile.** `XDocument.Parse` forza
  `DtdProcessing = Prohibit`; il path legacy V1 usa un reader in-memory su contenuto già parsato.
- **Report HTML: nessun XSS, nessun leak.** Tutto passa da `WebUtility.HtmlEncode`; nessun corpo
  oggetto, nessuna connection string.
- **Gestione culture: pulita.** Le 3 sole `ToUpper/ToLower` in `src/` sono tutte Invariant.
- **MSI: solido.** `perMachine`, `ProgramFiles64Folder` (quindi la voce PATH non è un vettore di
  DLL planting), `MajorUpgrade` con downgrade guard, smoke install/uninstall in CI.
- **Nessuna concorrenza fra le due scansioni DB** da rivedere: sono strettamente sequenziali.

---

## 2. I tre requisiti, stato reale

| | Requisito | Stato | Cosa manca davvero |
|---|---|---|---|
| 1 | **SICURO** | 🟡 parziale | Password in chiaro nell'header dello script salvato. TDS non cifrato e certificato non validato per default. Nessun escape di `]` negli identificatori in ~40 punti di emissione. Nessun avviso prima di DDL distruttivo. Picker server popolato da UDP broadcast non autenticato. |
| 2 | **AFFIDABILE** | 🟡 parziale | ~12 famiglie di falsi negativi (attributi letti a metà o non letti). ~10 bug che rendono lo script non eseguibile. Nessun test round-trip apply→ricompara oltre 3 dei 13 kind. `ComparisonOptions`: 14 flag su 20 non vengono mai letti. |
| 3 | **RESILIENTE** | 🔴 **assente oltre il singolo deploy** | Atomicità *dentro* un run: ✅ esiste e funziona. Undo *dopo* il commit: ❌ nulla. Nessun down-script, nessun backup, nessun journal, nessuna cancellazione, timeout fisso a 60 s che rende un rebuild grosso non deployabile. |

---

## 3. Lista di modifiche, ordinata per impatto ed effort

Impatto = quanto danno evita nel mondo reale. Effort: **S** ≤ ~50 righe in 1-2 file · **M** più
file + test · **L** sottosistema nuovo.

### W0 — Un giorno di lavoro, 8 difetti (3 critical). Massimo impatto per effort del progetto.

| # | Difetto | File | Eff | Impatto |
|---|---|---|---|---|
| **W0-1** | **`CREATE TABLE` emette `CONSTRAINT [x] DEFAULT … FOR [col]` a livello tabella = errore di sintassi.** `DEFAULT` non esiste nella grammatica `table_constraint`: è valido solo inline sulla colonna o via `ALTER TABLE ADD CONSTRAINT`. **Aggravante che ho verificato io: è fissato da un golden**, `TableWithConstraintsGoldenTests.Create_table_with_check_and_default_constraints.verified.txt:6` — la suite asserisce l'output rotto. Qualunque tabella nuova con un default nominato non si crea. | `TableScriptEmitter.cs:66-70` | **S** | 🔴 critical |
| **W0-2** | **`ALTER TABLE ADD` di colonna `NOT NULL` con default nominato omette il DEFAULT** → Msg 4901 su qualunque tabella popolata. `FormatColumn(c, hasNamedDefault: true)` sopprime il default inline (`:481`) e il `ADD CONSTRAINT` arriva come istruzione separata più avanti. È la migrazione più comune che esista. | `TableScriptEmitter.cs:255-259` + `:481` | **S** | 🔴 critical |
| **W0-3** | **Password in chiaro nell'header dello script.** `DeployScriptBuilder.Build` riceve le connection string grezze e le scrive in `-- Source :` / `-- Target :`. La stessa funzione, 45 righe sotto, le redige correttamente per il dialog (`ConnectionStringRedactor.Redact`) — è una dimenticanza dimostrabile, non una scelta. Lo script salvato viene mandato per mail e committato. | `MainWindowViewModel.cs:598, 632` | **S** | 🔴 critical |
| **W0-4** | **`AppState.LastError` è bindato solo in una view che non viene mai istanziata** → ogni errore di comparazione/progetto è invisibile. L'utente vede sparire l'overlay busy e i conteggi *precedenti* ancora a schermo, e li legge come attuali. Precondizione di W1-1. | `MainWindow.axaml:452` | **S** | 🟠 high |
| **W0-5** | **`connections.json` fa morire l'app all'avvio senza finestra né log.** `catch (JsonException)` è l'unico catch: `File.ReadAllBytesAsync` lancia `IOException`/`UnauthorizedAccessException` (file tenuto aperto da OneDrive, profilo roaming read-only) e l'eccezione esce da un `async void` handler (`App.axaml.cs:42`) senza handler globale. L'app lampeggia e sparisce, a ogni avvio, per sempre. *(Trovato dal critico contro una non-segnalazione esplicita di un reviewer, che aveva scritto "cattura IOException, non lo riporto". Non la cattura.)* | `JsonConnectionStore.cs:88-105` | **S** | 🟠 high |
| **W0-6** | **I permessi selezionati non vengono mai deployati.** `IgnorePermissions` è in `ComparisonOptions.Default`, quindi il motore mostra le righe Permission ma `DeployScriptBuilder` le scarta. L'utente seleziona 3 GRANT, riceve "aggiornamento riuscito", la differenza resta. Fix: azzerare il flag quando la selezione contiene un `Kind == "Permission"` — la selezione *è* l'intento. | `DeployScriptBuilder.cs` | **S** | 🟠 high |
| **W0-7** | **Trigger `INSTEAD OF` su vista invisibili.** `INNER JOIN sys.tables` scarta ogni trigger con parent vista. Il pattern standard per rendere aggiornabile una vista multi-tabella non viene mai comparato né deployato. Fix: `sys.objects` con `type IN ('U','V')`. Una riga. | `ModuleReader.cs` (TriggerQuery) | **S** | 🟠 high |
| **W0-8** | **`GRANT <perm> ON DATABASE TO [x]` non è T-SQL valido** — e uno unit test asserisce la forma invalida. | `PermissionScriptEmitter.cs` | **S** | 🟡 medium |

> Nota trasversale su W0-1 e W0-8: **due difetti su otto sono *fissati da test* che asseriscono
> output non eseguibile.** È l'argomento definitivo per la migliora F-1 (gate ScriptDom).

### W1 — Deploy che fallisce a metà (effort M). Blocca GA.

| # | Difetto | File | Eff | Impatto |
|---|---|---|---|---|
| **W1-1** | **Una ricomparazione fallita lascia le righe precedenti selezionate ed eseguibili contro il NUOVO target.** Compari DEV→STAGING, spunti 12 righe (5 sono `DROP`), ripunti il target su PROD, la sorgente è giù → `CompareAsync` esce, la griglia resta, `Esegui` resta abilitato. I DROP partono su PROD. Fix: legare i risultati agli endpoint da cui provengono (flag `ResultsAreStale` che gatea `CanExecuteOnTarget`). | `AppStateViewModel.cs:184` | **M** | 🔴 critical |
| **W1-2** | **Lo script della GUI non ha ordinamento topologico.** `DeployScriptBuilder.Build` non ha un parametro `dependencies`, quindi `ScriptGenerator` fa `dependencies ??= []` e ordina per `KindRank`: una vista nuova che chiama una funzione nuova emette la vista prima → Msg 208. Verificato io stesso: `Generate(syntheticResult, selection: null, options: …)`, nessun arco. **Prerequisito duro dello stack undo** (un down-script mal ordinato è peggio di nessun down-script). | `DeployScriptBuilder.cs:53` | **M** | 🟠 high |
| **W1-3** | **Schemi mai comparati, `CREATE SCHEMA` mai emesso.** Letti e buttati. Qualunque oggetto in uno schema assente sul target → Msg 2760 alla prima istruzione. *(Segnalato da 4 dimensioni indipendenti.)* | `ComparisonEngine.cs` | **M** | 🟠 high |
| **W1-4** | **`DROP TABLE` emesso prima di droppare le FK entranti** → il DROP fallisce sempre (Msg 3726). Le FK vengono droppate, ma alla fine dello script. | `ScriptGenerator.cs:146` | **M** | 🟠 high |
| **W1-5** | **`ALTER COLUMN` senza droppare prima indice/PK/CHECK dipendenti** → Msg 5074. Allargare un `int` a `bigint` su una colonna indicizzata (caso da manuale) fallisce. | `TableScriptEmitter.cs:244` | **M** | 🟠 high |
| **W1-6** | **L'orchestrazione delle FK entranti del rebuild vede solo gli oggetti nella selezione**, che nella GUI è ciò che l'utente ha spuntato. Spunti solo `Invoice`, `InvoiceLine` è Identica → nessun drop FK → Msg 3726 sul `DROP TABLE` del rebuild. | `ScriptGenerator.cs:84` | **M** | 🟠 high |
| **W1-7** | **Sequenze `decimal(38,0)` abortiscono l'intera comparazione.** `CAST(... AS bigint)` → Msg 8115 → `CatalogQueryFailed` → il compare di *entrambi* i DB muore con un errore di overflow aritmetico incomprensibile. | `SequenceReader.cs` | **M** | 🟠 high |
| **W1-8** | **Il rebuild per IDENTITY mette un default auto-nominato sulla temp table, poi ri-aggiunge quello nominato** → "Column already has a DEFAULT bound to it". | `TableScriptEmitter.cs:348` | **S** | 🟠 high |
| **W1-9** | **Nessun handler globale di eccezioni.** Qualunque throw su un path `async void` uccide il processo in silenzio: nessun dialog, nessun log, nessun crash file. Con W0-5 e W1-10 sono tre morti silenziose distinte. | `Program.cs:9` | **M** | 🟠 high |
| **W1-10** | **Due righe Permission possono produrre la stessa `ObjectIdentity`** (`Identity` scarta `ClassDesc`, `DiffKey` no) → `RebuildRows.ToDictionary` lancia `ArgumentException` → morte del processo a metà comparazione. Raggiungibile: sotto metadata visibility un permesso OBJECT su oggetto invisibile produce `Identity` byte-identica a un GRANT database-scope. *(Il critico ha stabilito la raggiungibilità che il reviewer aveva solo ipotizzato.)* | `Permission.cs:26` + `MainWindowViewModel.cs:477` | **S** | 🟡 medium |
| **W1-11** | **`dbdelta apply` non ha transazione.** `useOwnTransaction: false` hardcoded su uno script arbitrario dell'utente: senza envelope proprio, resta a metà. È **l'unico vero buco di half-migration del prodotto**. Lo XML doc afferma il contrario di quello che fa il codice, e il test che dovrebbe coprirlo non esercita l'atomicità. | `ApplyCommand.cs:67` | **S** | 🟡 medium |
| **W1-12** | **`SplitOnGo` non è consapevole di stringhe e commenti**: un `GO` dentro un blocco `/* */` o un literal spezza il batch. | `SqlExecutor.cs` | **M** | 🟡 medium |

### W2 — Dice "Identici" e non lo sono (falsi negativi). Il danno peggiore per un tool di diff.

| # | Difetto | File | Eff | Impatto |
|---|---|---|---|---|
| **W2-1** | **Il rebuild per IDENTITY droppa la tabella e non ricrea indici né trigger.** `DROP TABLE` porta via tutto; `EmitIndexDelta` non emette nulla per gli indici identici sui due lati e il trigger identico non viene mai ricreato. Il deploy dice "riuscito", il re-compare dice "Identici", e in produzione l'indice e il trigger di audit non ci sono più. Se invece l'indice *era* diverso, il deploy fallisce. *(3 dimensioni, stesso difetto.)* | `TableScriptEmitter.cs:312` + `ScriptGenerator.cs:61` | **M** | 🔴 critical |
| **W2-2** | **L'accoppiamento oggetti/colonne è case-SENSITIVE (ordinal).** Con collation case-insensitive (il default), `dbo.CLIENTI` sul target e `dbo.Clienti` sulla sorgente diventano `OnlyInA` + `OnlyInB` → il deploy **droppa la tabella di produzione** e ne crea una vuota. SQL Server decide la case-sensitivity dalla collation del database, che il reader **già legge** (`DefaultCollation`). | `ObjectModel/Table.cs`, `ComparisonEngine` | **M** | 🔴 critical |
| **W2-3** | **Nessun preflight di metadata visibility: gli oggetti invisibili diventano `DROP`.** Login read-only con SELECT su 3 tabelle su 50 (l'assetto che il requisito SICURO *incoraggia*): `sys.tables` ne restituisce 3 per la sorgente e 50 per il target → 47 tabelle di produzione classificate `OnlyInB` → 47 `DROP TABLE`. Fix: asserire `HAS_PERMS_BY_NAME(…,'VIEW DEFINITION')` in `LoadAsync` e fallire *forte*. | `LiveDbSource.cs` | **M** | 🔴 critical |
| **W2-4** | **Indici non-rowstore invisibili.** Il filtro tiene solo i B-tree su tabella base: columnstore, XML, spatial, hash e indici su viste indicizzate spariscono da entrambi i lati → "Identici". Un clustered columnstore mancante su una fact table non viene mai segnalato. *(4 dimensioni.)* | `IndexReader.cs` | **L** | 🟠 high |
| **W2-5** | **Varianti speciali di tabella lette come tabelle semplici**: temporal/system-versioned, memory-optimized, external, FileTable. Il versioning attivo/spento non viene mai riportato. | `TableReader.cs` | **L** | 🟠 high |
| **W2-6** | **Vincoli auto-nominati → quasi ogni tabella reale risulta "Different" per sempre.** `is_system_named` non viene mai letto e i vincoli si accoppiano per nome: `DF__Ordini__Stato__3B75D760` vs `DF__Ordini__Stato__1A14E395` (il suffisso deriva dall'object id) non matchano mai. Churn `DROP`/`ADD CONSTRAINT` inutile e nessuna opzione per spegnerlo. *(2 dimensioni.)* | `ConstraintReader.cs` | **M** | 🟠 high |
| **W2-7** | **Dynamic Data Masking non letto**: una colonna PII mascherata compara uguale a una non mascherata, e un rebuild rimuove la maschera in silenzio. | `TableReader.cs` | **M** | 🟠 high |
| **W2-8** | **`BodyNormalizer` collassa gli spazi *dentro* i literal e attraverso i commenti di riga** → due corpi genuinamente diversi comparano Identici. Regressione introdotta dalla feature `ExpressionsEqual` di rc4. | `BodyNormalizer.cs` | **M** | 🟡 medium |
| **W2-9** | **`ALTER COLUMN` che restringe scala/precisione senza guardia**, e il preamble disattiva l'unica impostazione che lo abortirebbe (`NUMERIC_ROUNDABORT OFF`): `decimal(19,4)` → `decimal(19,2)` arrotonda ogni valore, senza errore né avviso. | `TableScriptEmitter.cs:229` | **M** | 🟠 high |
| **W2-10** | **Direzione ASC/DESC delle chiavi PK/UNIQUE non letta** → una PK `(Data DESC, Id)` compara Identica a `(Data ASC, Id)` e viene ricreata ASC, invertendo il piano di accesso del paging. | `ConstraintReader.cs` | **M** | 🟡 medium |
| **W2-11** | **Ordine delle colonne `INCLUDE` confrontato in modo ordine-sensibile** e letto senza `ORDER BY` deterministico → indici semanticamente identici risultano Different e vengono ricostruiti su tabelle di produzione. | `IndexReader.cs` + `ComparisonEngine.cs` | **S** | 🟡 medium |
| **W2-12** | **Collation a tappeto non sopprimibile**: due DB installati con default diversi (`Latin1_General_CI_AS` vs `SQL_Latin1_General_CP1_CI_AS`, caso estremamente comune) flaggano ogni tabella con stringhe ed emettono `ALTER COLUMN COLLATE` che fallisce sulle colonne indicizzate. `IgnoreCollation` esiste ma non viene letto. | `ComparisonEngine.cs` | **M** | 🟡 medium |
| **W2-13** | **`is_not_trusted` (WITH NOCHECK) non modellato** per FK e CHECK → un vincolo non validato compara uguale a uno validato. | `ConstraintReader.cs` | **S** | 🟡 low |
| **W2-14** | **`UserReader` fa join su `sys.server_principals`**, filtrata da metadata visibility: con un login a privilegio minimo `LoginName` è NULL per tutti → **ogni utente risulta Different** e il deploy emette `CREATE USER` per utenti già corretti. Non è un problema Azure: succede on-prem. | `UserReader.cs:25` | **S** | 🟡 medium |
| **W2-15** | **Moduli CLR etichettati come "criptati" e quindi Different per sempre**; `WITH ENCRYPTION` e "corpo non leggibile per permessi" sono conflati. | `ModuleReader.cs` | **S** | 🟡 medium |

### W3 — RESILIENZA: lo stack undo (il requisito #1 dell'owner)

Ordine di implementazione. Dettaglio completo in §6.

| # | Cosa | Eff | Perché in questo ordine |
|---|---|---|---|
| **W3-0** | Chiudere i 3 buchi dell'atomicità: rollback esplicito e *osservabile* (`bool RolledBack` su `SqlBatchResult`), `commandTimeoutSeconds` parametrico (0 = illimitato — oggi 60 s fissi rendono **non deployabile** un rebuild da 30M righe), transazione per `dbdelta apply` su script esterni (W1-11). | **S** | Il resto poggia qui. |
| **W3-1** | **Tassonomia degli avvisi di deploy** + gate. Un classificatore su `selectedPairs`: `DropTable`, `DropColumn`, `NarrowColumn`, `TableRebuild`, `AddNotNullNoDefault`, `SequenceRestart`, `EncryptedModule`, `DropPrincipal`, ognuno con `Reversible: bool`. Consumato da: banda di avviso nel dialog + checkbox di presa d'atto, gate del backup, `--abort-on-warnings` nella CLI (già specificato in `docs/01_architecture.md:1154`, mai implementato). | **M** | **Prima del down-script.** Dire "questo cancella 12.400.000 righe e non è annullabile" vale più che consegnare un `down.sql` che quelle righe non le riporta comunque. |
| **W3-2** | **`down.sql` per inversione di coppia.** `DifferencePair` è `(Identity, Status, SideA, SideB)` e `SideB` **è** l'oggetto target pre-deploy catturato; `ScriptGenerator.Generate` è puro. Quindi il down-script è l'up-script con i lati scambiati: ~20 righe, **zero nuovi emitter, zero modifiche al modello**. Scrivere `up.sql` + `down.sql` + `meta.json` in una cartella per-run *prima* di eseguire. | **M** | Prerequisiti duri: W1-2 (archi di dipendenza) e W2-1 (il rebuild che droppa indici — altrimenti il down-script *causa* la perdita che dovrebbe riparare). |
| **W3-3** | **Journal dei deploy.** `JsonDeployJournal` clonato da `JsonRecentProjectsStore` (ha già `schemaVersion`, `WriteAtomicAsync`, degrado su file corrotto). Toolbar "Cronologia deploy" → *Apri cartella* / *Vedi up.sql* / *Annulla questo deploy*. | **S+S** | Oggi non esiste **nessuna** traccia di cosa è stato applicato, quando, su quale server. |
| **W3-4** | **Backup `COPY_ONLY` prima dei deploy distruttivi.** L'unico meccanismo che annulla i **dati**. Checkbox pre-spuntata e non deselezionabile quando la lista rischi contiene una voce irreversibile. Vincoli da progettare: serve `db_backupoperator`, il file lo scrive il **service account** (mai un path picker client-side: leggere `SERVERPROPERTY('InstanceDefaultBackupPath')`), non può stare dentro la transazione del deploy, **non esiste su Azure SQL DB**. `RESTORE` fuori scope: DbDelta registra il path, il DBA agisce. | **M** | |
| **W3-5** | Cancellazione reale per operazione (oggi ogni await passa `CancellationToken.None`; il pulsante Annulla è decorativo) + snapshot del modello target pre-deploy per rigenerare un down-script *drift-aware*. | **M**/**L** | |

**Cosa NON si può annullare con DDL, e va detto all'utente *prima* del click:** righe di tabella
droppate · valori di colonna droppati · un tipo restretto (stringhe troncate, decimali arrotondati
in silenzio) · colonne portate via dal rebuild · il valore corrente di una sequenza · tutto ciò che
i reader non modellano (columnstore, masking, temporal) e che quindi non può essere ri-emesso.

### W4 — Parity / prodotto (vedi §5 per il ragionamento)

| # | Cosa | Eff | Impatto |
|---|---|---|---|
| **W4-1** | Superficie reale delle **opzioni di comparazione** — oggi 14 flag su 20 non vengono mai letti, `ProjectOptions` del `.dbd` è ignorato, e la CLI in `script` calcola le opzioni e poi passa `Default`. Redgate ne ha ~60 e sono la differenza fra una demo e un tool usabile sul proprio DB. | **M** | 🔴 fondamentale |
| **W4-2** | **Snapshot di schema** (`.snp`-equivalente) come sorgente *e* target: sblocca baseline pre-deploy, drift detection schedulata e il down-script drift-aware (W3-5) in un colpo. | **M** | 🔴 fondamentale |
| **W4-3** | **Filtri oggetto**: `--include`/`--exclude <Kind>:<regex>`, toggle per tipo, file di filtro salvabili. Il plumbing esiste (`Generate` accetta già una `selection`), manca solo il predicato. | **M** | 🟠 alto |
| **W4-4** | **CLI utilizzabile in CI**: `dbdelta script` ritorna 0 anche dopo aver scritto 400 righe di DDL. Servono exit code di drift, `--assert-identical`, `--project`. | **S** | 🟠 alto |
| **W4-5** | **Credenziali fuori da argv** (`--target-env`, stdin, nome connessione salvata): oggi la password di produzione sta nella command line, leggibile da qualunque utente locale. | **M** | 🟠 alto |
| **W4-6** | **Proprietà fisiche degli indici** (fill factor, compressione, filegroup, `is_disabled`) e **extended properties** (`MS_Description`): entrambe famiglie di falsi "Identici". | **M** | 🟠 alto |
| **W4-7** | **Tagging ambiente reale**: oggi `ToEndpoint()` hardcoda `EnvironmentTag: "Dev"` per **entrambi** gli endpoint. L'affordance di sicurezza è decorativa. | **M** | 🟠 alto |
| **W4-8** | Evidenziazione sintassi + diff intra-riga nel pannello: è la superficie su cui gli utenti giudicano un compare tool, oggi è testo monospace piatto. | **M** | 🟠 alto |
| **W4-9** | Scripts folder come sorgente/target (database-as-code). Spedire prima `--scripts-out` (solo scrittura) che da solo sblocca "esporta schema in Git". | **L** | 🟠 alto |
| **W4-10** | Kind tier-3 (full-text, partition, filegroup, CLR, XML schema collection, DDL trigger…). **Prima di implementarne uno**: una query di conteggio per `sys.objects.type` che dichiara "N oggetti non comparati" — copre tutti i kind mancanti in un colpo e trasforma un falso negativo in un avviso onesto. | **L** | 🟡 medio |

### W5 — Debito e qualità

`stackalloc` illimitato in `ProjectsFolder` (nome progetto incollato da 500 KB → StackOverflow
non catchabile) · 3ª copia della regex password rotta, questa **scrive su disco** in chiaro a ogni
comparazione riuscita (`ConnectionStoreViewModel.cs:174`, latente oggi) · JSON degli errori CLI
concatenato a mano (messaggi SQL Server multi-riga e path Windows lo rendono non parsabile) ·
`LiveDbObjectBodyResolver` duplica 690 righe di reader · `ScriptDom` referenziato e mai usato (peso
nell'MSI) · `RebuildRows` O(n²) · pulsante "Nuovo progetto" è uno stub che non fa nulla · 4 file UI
irraggiungibili (`ConnectionPickerView`, `EnvironmentBadge`, …) · `System.CommandLine` beta pinnato
in un prodotto GA · MRU store senza l'hardening 0600 che il gemello ha.

---

## 4. Open points già salvati (memoria + `docs/BACKLOG.md`)

### Bloccati su input esterno dell'owner
- [ ] **Code signing** — certificato Authenticode, poi firma di MSI + `.exe` in `release.yml`.
      **Bloccato: serve un certificato.** Ultimo gate reale per v1.0 FINAL.
- [ ] **Annuncio alpha pubblico** — README (link MSI + sito DocFX), release notes, annuncio.
- [ ] *(nuovo, dal critico)* **Integrità della distribuzione**: la release attacca l'`.msi` nudo,
      senza `.sha256` né attestazione di provenienza. Chi scarica un tool che esegue DDL in
      produzione non ha **alcun** modo di verificare cosa ha preso. Il checksum è 3 righe di
      workflow ed è la metà gratuita della storia della firma.

### Release
- [x] `v1.0.0-rc1` … `rc4` rilasciate (rc4 = `19131d8`, 2026-06-05, smoke-testata live).
- [ ] **`v1.0.0` FINAL** — gated su code signing + annuncio. La pipeline pubblica già i tag senza
      `-` come non-prerelease. Nota WiX: rc e final hanno la stessa `ProductVersion` numerica
      `1.0.0` → `MajorUpgrade` non aggiorna, va disinstallato prima.

### Hardening in corso
- [ ] **Più scenari di parity** — fixture a 17 scenari. Gap noti: ordinamento DROP reverse-topo con
      oggetti schemabound, indici filtrati/columnstore, check constraint cross-tabella, extended
      properties. CLI Redgate license-blocked (exit 35) → GUI per il lato Redgate.
      Live: `192.168.3.243` (`DbDeltaParity_Source`/`_Target`), password sa chiesta a sessione.
- [ ] **Cosmetici deliberatamente NON allineati**: `CREATE OR ALTER` (tenuto per idempotenza —
      opzione "C" deselezionata dall'owner), naming `[X_tmp]` del rebuild, spaziatura virgola in
      `IDENTITY(1,1)`, blocco `xp_logevent` finale di Redgate.
- [ ] Ottimizzazione dimensione MSI (~94 MB; runtime condiviso ~dimezzerebbe). YAGNI.
- [x] Bug header `DeployScriptBuilder` (`TrimGeneratorHeader`) — chiuso 2026-05-28.
- [x] **RIFIUTATO**: "rimuovere i fallthrough irraggiungibili degli switch" — IDE0010/IDE0072
      sotto `TreatWarningsAsErrors` *richiedono* ogni membro enum nominato. Sono imposti
      dall'analizzatore, non rumore.

### v2 parking-lot (fuori scope v1 per spec §6.3) — brainstorm ancora pendente
- [ ] Provider Scripts-Folder / Snapshot / Source-Control (LibGit2Sharp). → **questa review
      promuove Snapshot a W4-2 (fondamentale)**: serve allo stack undo, non solo alla parity.
- [ ] Migration script (override DDL scritti dall'utente).
- [ ] Kind tier-3: CLR Assembly, Full-text, XML schema, Service Broker, partition function/scheme,
      filegroup. → **W4-10**, con il backstop di onestà prima dell'implementazione.
- [ ] Estensione SSMS / VS; OpenTelemetry opt-in; canale auto-update; CLI Linux + macOS.

### Vincoli di processo già in memoria (validi)
- CI gatea **duro** su `dotnet format --verify-no-changes` + `TreatWarningsAsErrors`.
- xunit.v3 di questo repo **non** ha implicit usings (`using Xunit;` esplicito).
- Il tool Write emette LF → sempre `dotnet format` apply+verify prima di committare `.cs`.
- Verify crea un `.verified.txt` vuoto da 3 byte al primo run → `Move-Item -Force` del received.
- `--` è illegale dentro i commenti XML.
- Push automatico a fine lavoro verificato; tag/release solo con ok dell'owner.
- Terminologia UI: sempre **"Carica"**, mai "Apri". Inviolabile.

### Non in memoria e da aggiungere
1. **CI non esegue 2 progetti di test su 11.** `ci.yml` enumera i progetti a mano →
   `DbDelta.Property.Tests` e `DbDelta.Shared.UnitTests` **non sono mai girati in CI**. La rete di
   sicurezza sui falsi positivi non è un gate. Qualunque progetto aggiunto in futuro è escluso in
   silenzio. Fix: `dotnet test DbDelta.sln` con filtro per categoria.
2. **Il round-trip apply→ricompara copre 3 kind su 13.** 8 kind non vengono **mai** deployati verso
   un server reale in nessun test. Un kind non testato è un kind che potrebbe non deployare affatto.
3. **7 kind non hanno golden test**: Sequence, Synonym, UserDefinedType, TableType, User, Role,
   Permission.
4. **Nessuna misura di coverage** in CI.

---

## 5. Le migliorie che considero fondamentali per il prodotto

Non ordinate per effort ma per *quanto cambiano cosa DbDelta è*.

### F-1. Gate di parsing ScriptDom su ogni script emesso — **effort S, il miglior rapporto del progetto**

`Microsoft.SqlServer.TransactSql.ScriptDom` **è già** una `PackageReference` di `DbDelta.Core` e la
sua DLL **è già** nell'output dei golden test. Nessuno la usa. Un helper `AssertParses(string tsql)`
con `TSql160Parser` chiamato da un converter Verify avrebbe intercettato **W0-1 e W0-8 al primo
run** — invece i golden li hanno *fissati come corretti*.

Un tool che genera DDL e non parsa mai il proprio output ha un buco strutturale, non un bug. Questa
è la singola modifica che cambia la classe di errori possibili.

### F-2. Round-trip per kind, senza filtri — **effort M, è la rete di sicurezza che manca**

Un `[Theory]` con uno snippet SQL di seed per kind: seed sorgente → `LoadAsync` su entrambi →
`Generate` → `SqlExecutor.ExecuteAsync` → ricarica target → assert `Differences.All(Identical)`.
Gira già per PR nel job linux (Testcontainers è configurato).

È l'unica cosa che *dimostra* che un kind deploya davvero. Chiude in un colpo W1-3, W1-4, W1-5,
W2-1, W0-8 e ogni loro futura regressione. Oggi la suite dà 455 assert verdi su output che in tre
casi accertati **non è eseguibile**.

### F-3. Rendere l'incompletezza *rumorosa* — **effort M, requisito AFFIDABILE**

Ogni finding W2 è la stessa modalità di fallimento: una `WHERE` o una colonna non letta trasforma
un drift reale in "Identici", e l'utente non ha modo di saperlo. I reader **sanno già** cosa stanno
scartando.

`Database` guadagna `IReadOnlyList<SourceWarning> Warnings`; ogni reader conta le righe filtrate e
appende `("Index", "dbo.FactSales", "1 indice columnstore non comparato")`. La UI mostra
**"comparazione parziale"** invece di verde. Un compare tool ha una sola promessa da mantenere —
che "Identici" significhi identici. Se non può, deve dirlo.

Questo copre anche tutti i kind tier-3 mancanti *senza implementarne nessuno*: una query di
conteggio per `sys.objects.type` che dichiara "N oggetti di tipo X non comparati" è onesta oggi e
resta valida per sempre.

### F-4. Un solo `SqlIdentifier.Quote` / `SqlLiteral.Escape`, imposto da un architecture test — **effort M, requisito SICURO**

~40 punti concatenano `"[" + valore_catalogo + "]"` senza raddoppiare `]`, e 1 punto (`sp_rename`
nel rebuild) concatena `"'" + nome + "'"` senza raddoppiare `'`. Non è la stessa vulnerabilità
scoperta due volte: è **la stessa astrazione assente vista da due angoli**.

Due funzioni da una riga, la conversione degli emitter, poi una regola in
`DbDelta.Architecture.Tests` che scansiona gli emitter per la concatenazione grezza di `[`. Il
progetto ha già un test project di architettura: la regola è il vero deliverable, perché impedisce
al 41° sito di nascere. Nessun test nel repo contiene un `]` in un identificatore (`grep "]]" tests/`
è vuoto) — quindi la property test con identificatori ostili è il complemento naturale.

Regola DRY del `CLAUDE.md` applicata dove costa davvero.

### F-5. Un solo classificatore di rischio, tre consumatori — **effort M, requisiti SICURO + RESILIENTE**

Tre feature diverse (banda di avviso nel dialog, gate del backup, `--abort-on-warnings` della CLI)
hanno bisogno della **stessa** risposta: quali coppie selezionate distruggono dati? Calcolabile
dalle coppie stesse, senza ri-parsare l'SQL emesso.

Tenerlo **stupido e sovra-inclusivo**: un falso "questo può perdere dati" costa una checkbox, un
falso negativo costa una tabella.

Arricchimento opzionale, economico, di impatto altissimo: un
`SELECT SUM(rows) FROM sys.partitions WHERE index_id IN (0,1) AND object_id IN (…)` read-only prima
del dialog trasforma *"droppa `dbo.Archive_2019`"* in *"droppa `dbo.Archive_2019` — 12.400.000
righe"*. **Quel numero è ciò che fa fermare un operatore.**

### F-6. Comparatore di identificatori guidato dalla collation — **effort M, chiude un critical**

SQL Server decide la case-sensitivity degli identificatori dalla **collation del database**, che il
reader **già legge** (`Database.DefaultCollation` via `DATABASEPROPERTYEX`). Un solo
`StringComparer` risolto da quel valore in `ComparisonEngine.Compare`, propagato all'identity
comparer e a ogni dizionario di nomi. Chiude W2-2 alla radice invece che caso per caso, e le due
FsCheck property da aggiungere (cloni con case permutata, identità duplicate) sono le stesse che
oggi la suite **strutturalmente non può** trovare, perché `SchemaArbitraries` dedupa per
`(Schema,Name)` esatto e non permuta mai la case.

### F-7. Superficie reale delle opzioni — **effort M, è la soglia di adozione**

`ComparisonOptions.Default` è hardcoded in ogni chiamante. 14 flag su 20 non vengono mai letti.
Il `.dbd` persiste `ProjectOptions` che il motore ignora. `OwnerMappings`/`TableMappings` sono
modellate, persistite, round-trippate da test — e **mai applicate** (una tabella mappata viene
riportata per il DROP).

Nel breve: se un progetto caricato ha mapping non vuoti, **bloccare** la comparazione con "mapping
non ancora supportati" invece di ignorarli in silenzio. Ignorare una configurazione che l'utente ha
scritto e salvato è la forma peggiore di inaffidabilità, perché l'utente crede di aver detto una
cosa e il tool ne fa un'altra.

Nel medio: un pannello opzioni (le checkbox 32 px che il design system già impone) legato a un
singolo `ComparisonOptions` che vive sul progetto e passa a ogni `Compare`/`Generate`.

### F-8. Snapshot di schema come primitiva, non come feature v2 — **effort M**

Un `JsonSchemaSource : ISchemaSource` con envelope versionato sblocca **quattro** cose con un
lavoro: baseline pre-deploy (→ undo drift-aware, W3-5), drift detection schedulata contro una
baseline, comparazione offline senza toccare produzione, e il provider `.snp` già in backlog.
Il costo reale sono i discriminatori `[JsonPolymorphic]` sulla gerarchia `Constraint` — lavoro che
serve comunque.

È in backlog come "v2 parking-lot". Va promosso: è infrastruttura per il requisito RESILIENTE, non
una feature di parity.

---

## 6. Roadmap undo — sintesi operativa

Il documento di design completo (meccanica delle transazioni, invertibilità per kind, vincoli del
backup, tassonomia degli avvisi, il test che prova ogni stadio) è in
`docs/review/2026-07-30-undo-architecture.md`.

I due punti che contano per decidere:

**1. Il down-script è quasi gratis, e questo cambia la priorità.** `SideB` di ogni
`DifferencePair` **è** l'oggetto target catturato prima del deploy, e `ScriptGenerator.Generate` è
puro. Quindi:

```csharp
public static string BuildInverse(
    IReadOnlyList<DifferencePair> selectedPairs, string src, string tgt,
    DateTime nowUtc, IReadOnlyList<DependencyEdge>? targetDependencies = null)
    => Build([.. selectedPairs.Select(Invert)], tgt, src, nowUtc, targetDependencies);

private static DifferencePair Invert(DifferencePair p) => p with
{
    Status = p.Status switch
    {
        DifferenceStatus.OnlyInA => DifferenceStatus.OnlyInB,
        DifferenceStatus.OnlyInB => DifferenceStatus.OnlyInA,
        _ => p.Status,
    },
    SideA = p.SideB,
    SideB = p.SideA,
};
```

Nessun emitter nuovo, nessuna modifica al modello. La struttura DROP-pass/CREATE-pass si inverte
per costruzione. Per i 4 kind di modulo (View/Procedure/Function/Trigger) l'inversione è
**perfetta**: ri-applica il corpo pre-deploy catturato, che è esattamente ciò che serve quando un
deploy ha sovrascritto un hotfix fatto a mano in produzione.

Non invertibili e da dichiarare tali: sequenze (il reader cattura `start_value`, mai
`current_value` → un `RESTART WITH` riavvolge un contatore vivo e causa collisioni di PK), colonne
droppate con `NOT NULL` senza default (l'`ADD` inverso *fallisce*), tipi allargati (l'inverso
restringe → troncamento silenzioso), moduli criptati, e il rebuild finché W2-1 non è chiuso.

**2. La copy deve essere onesta.** Una riga accanto al pulsante, mai negoziabile:

> *"Ripristina la struttura, non i dati: righe eliminate da DROP TABLE/COLUMN e valori troncati non
> tornano."*

Se l'utente legge "annullamento" come "backup", il down-script diventa più pericoloso della sua
assenza — perché produce fiducia che non merita.

**Sequenza:** W3-0 (S) → avvisi (M) → down-script (M) → journal (S) → backup (M) → snapshot (L).

Gli avvisi vanno **prima** del down-script deliberatamente: prevenire l'errore vale più che offrire
una riparazione parziale. I due insieme sono il requisito completo — l'avviso previene lo sbaglio,
il down-script ripara il 90% recuperabile, il backup copre il resto.

---

## Appendice A — Affermazioni confutate (non lavorarci)

6 finding su 102 sono stati uccisi dalla verifica adversariale, più 1 scenario corretto:

| Affermazione | Perché è falsa |
|---|---|
| Il rollback di `SqlExecutor` usa il token già cancellato | Confutata sul codice reale. |
| La regex del redactor tronca al `;` ed è copia-incollata | Il difetto esiste ma la conseguenza affermata no; la **terza** copia (che scrive su disco) è invece reale ed è in W5. |
| L'autofill credenziali sostituisce silenziosamente il login | Confutata *in quella forma*; il difetto reale è che non pulisce la password al cambio server → **innalzato a high** dal critico, perché l'input arriva da UDP non autenticato. |
| `ALTER SEQUENCE … RESTART WITH` resetta il contatore di produzione | Confutata sul path di emissione — ma resta vera come **limite di invertibilità** (§6). |
| Il reader UDTT perde vincoli/default/identity | Confutata come bug di *lettura*; resta vera come incompletezza di *emissione* (in W-lista come medium). |
| `DiffViewerViewModel` muta stato bindato fuori dal thread UI | Confutata. |
| *"L'utente spunta 'seleziona tutto'"* in uno scenario | **Non esiste alcuna affordance di seleziona-tutto** nel prodotto. Il finding resta valido, il moltiplicatore dello scenario no. |

## Appendice B — Mappa dei duplicati (dedup prima di triagiare)

~20 finding su 96 sono lo stesso difetto visto da dimensioni diverse. Triagiare senza dedup
distorce le priorità e nasconde i critical veri.

| Difetto | Occorrenze |
|---|---|
| Schemi mai comparati / nessun `CREATE SCHEMA` | ×4 |
| Indici non-rowstore invisibili | ×4 |
| `ComparisonOptions` / `ProjectOptions` morte | ×4 |
| Il rebuild droppa indici/trigger/FK | ×3 |
| Password nell'header dello script | ×2 |
| Timeout 60 s | ×2 |
| L'app perde gli archi di dipendenza | ×2 |
| Churn dei vincoli auto-nominati | ×2 |
| Masking invisibile | ×2 |
| `LineDiffer` quadratico | ×2 |
| `apply` senza transazione | ×2 |
| `script` scarta le opzioni calcolate | ×2 |
| Ordine `INCLUDE` ordine-sensibile | ×2 |
| Overflow bigint delle sequenze | ×2 |
| Migliora "genera rollback script" | ×4 quasi identiche |

## Appendice C — Aree rimaste non coperte

- `src/DbDelta.App.Avalonia/Styles/*.axaml` (Tokens, Themes, Templates, AppStyles): 4 file, zero
  copertura. Sono il punto di applicazione degli invarianti `CLAUDE.md` (32 px, no naked buttons) e
  nessun reviewer li ha verificati.
- Comportamento in memoria a 10k oggetti: mai misurato. Il path caldo non testato è
  `OnSearchTextChanged` → `_rowsView.Refresh()` a ogni battuta.
- `SynonymReader.SplitBaseObjectName`: parser di bracket scritto a mano senza un-escape di `]].
  Innocuo per l'emissione (usa `baseRaw`), sbagliato nella visualizzazione a segmenti. Condivide la
  radice con F-4.
