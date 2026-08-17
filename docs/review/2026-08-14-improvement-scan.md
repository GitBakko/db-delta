# Scan migliorie + code review — 2026-08-14

**Stato al momento dello scan:** `main` = `979d785`, origin sincronizzato, working
tree pulito, **v1.0.2 pubblicata** (2026-08-13, MSI allegata).

Prodotto da sei analisi indipendenti (UX, gap di prodotto, architettura,
performance, affidabilità, distribuzione/CI) più un critico unico che ha
riaperto i file citati, ucciso gli YAGNI e fuso i duplicati: **67 finding
grezzi → 14 sopravvissuti, 17 scartati con motivo**.

Ogni voce cita `file:riga`. Le righe sono quelle di `979d785` — se un file è
stato toccato dopo, ricontrolla prima di fidarti del numero.

## Cosa NON è in questa lista, e perché

Deciso dal proprietario, non riaprire senza una ragione nuova:

- **Undo/rollback** — rinviato (2026-08-01). Prerequisiti tecnici chiusi, vedi
  `2026-08-08-handoff-to-v1.md`.
- **Code signing** e **annuncio pubblico** — bloccati sul certificato / del
  proprietario.
- **`CREATE OR ALTER`, `[X_tmp]`, spaziatura `IDENTITY(1,1)`, `xp_logevent`
  finale** — divergenze cosmetiche deliberate da Redgate.
- **MSI da ~94 MB**, **compressione per-partizione non modellata** — YAGNI
  consapevoli.

---

# Stato di avanzamento

Aggiornato il 2026-08-17.

| Voce | Stato | Commit |
|---|---|---|
| 1 — CI non fa da gate | **fatta** | `f91ee6e` |
| 4 — Diff pane, SQL sotto il nome sbagliato | **fatta** | `d210b1a` |
| 3 — Dialogo di conferma: script + cosa si elimina | **fatta** | `0cde9a9` |
| 11a — Censimento + banner ambra | **fatta** | `6c2e2e9` |
| 11b — `EmitRebuild` deve RIFIUTARE su indici non riemettibili | **fatta** | `04886b1` · `b250d4f` · `3c32b71` |
| 12 — Vincoli auto-nominati appaiati per nome | **fatta** | `9b81ca0` · `142fcb7` |
| 2 — Compare non annullabile, letture a 30 s | **fatta** | `8cfbc91` · `cf81ee9` |
| 5, 6, 7, 8, 9, 10, 13, 14 | aperte | — |

**La 11 è chiusa in entrambe le metà.** Il reader porta ogni tipo di indice e
`TableIndex.TypeDesc`; `IndexScriptEmitter.EmitCreate` /
`EmitRebuildForCompression` e `TableScriptEmitter.EmitRebuild` alzano
`UnscriptableIndexException` prima che esista una riga di script; la CLI esce 30
e l'app mostra il banner. `DROP INDEX` resta permesso su ogni tipo — è valido, e
rifiutarlo bloccherebbe una convergenza che il target sa completare.

Non fatta, e deliberatamente: **l'emissione** di un columnstore. È il rifiuto
che ferma la perdita; scrivere il `CREATE` è una voce a sé, e nessuno l'ha
ancora chiesta.

**La 12 è chiusa, meno una metà dichiarata.** `is_system_named` viaggia dalle tre
query fino a `Constraint.IsSystemNamed`; `ConstraintPairing` appaia per **forma**
ciò che il server ha nominato da sé e per nome tutto il resto; l'emittente crea i
primi **senza** clausola `CONSTRAINT`, così è il target a coniare il proprio
nome, e continua a droppare col nome vero del target.

Non fatto, e per una ragione: **`IgnoreConstraintNames` resta scollegato**. È un
flag dichiarato e morto, e la voce **9** deve prima decidere se quei flag si
implementano o si cancellano — cablarne uno adesso significherebbe scegliere al
posto suo. Il falso positivo che questa voce esisteva per uccidere non passa
comunque più da lì: i vincoli auto-nominati non sono mai confrontati per nome.

**La 2 è chiusa, in due metà indipendenti.** Le letture del catalogo hanno un
`Command Timeout` di 300 s iniettato una volta sola in `ConnectionFactory` — la
proprietà `SqlConnection.CommandTimeout` è di sola lettura, quindi la stringa di
connessione è l'unica leva, ed è per questo che una riga copre tutti i comandi;
chi il timeout se l'era già scritto se lo tiene, `0` (illimitato) compreso. E il
compare si annulla: `CompareCancelCommand` generato dal toolkit, un **Annulla**
neutro nell'overlay, e i due call site che chiamavano il metodo scavalcando il
comando ora ci passano — altrimenti il pulsante sarebbe morto proprio su
«Aggiorna».

Due limiti dichiarati: `engine.Compare` è sincrono sul thread UI, quindi
Annulla accorcia la lettura e non il confronto; e il pulsante non è mai stato
visto dal vivo, solo in headless.

La voce che resta a costo minore è la **6** (il report HTML che la GUI non sa
invocare): ore, e si vede subito.

---

# Parte 1 — La classifica

| # | Cosa | Sforzo | Valore |
|---|------|--------|--------|
| 1 | CI non fa da gate: 3 test rossi ora, matrice compat mai eseguita, 2 progetti test in nessun job | ore | alto |
| 2 | Compare = modale inescapabile, e timeout 30 s non configurabile sul percorso di lettura | ore | alto |
| 3 | Il dialogo di conferma pre-deploy non mostra né lo script né cosa viene eliminato | ore | alto |
| 4 | Il diff pane può mostrare l'SQL di un oggetto sotto il nome di un altro, in silenzio | ore | alto |
| 5 | `dbdelta script` esce sempre 0, e spedisce due contratti JSON incompatibili | ore | alto |
| 6 | Il report HTML finito e testato è irraggiungibile dalla GUI | ore | alto |
| 7 | Griglia risultati: sort promesso e assente, ricerca cieca alle parole a schermo, rebuild quadratico | ore | medio |
| 8 | ~1.500 righe morte: 6 view, connection manager irraggiungibile, Serilog mai chiamato, 3 interfacce senza consumatori | ore | medio |
| 9 | `ComparisonOptions` da 20 flag a 6, via `ProjectOptions`/mappings/parser v1 legacy | giorni | medio |
| 10 | Cancellare `LiveDbObjectBodyResolver` e disegnare il diff dal grafo già in memoria | giorni | alto |
| 11 | Dice «Identical» su cose che non ha mai guardato — e un rebuild distrugge in silenzio quelle che non vede | giorni | alto |
| 12 | I vincoli auto-nominati sono appaiati per nome, quindi non combaciano mai fra due build | giorni | alto |
| 13 | La modale di primo avvio chiude l'app se annullata, e nessun dialogo risponde a Invio/Esc | ore | medio |
| 14 | Il sito docs non nomina mai la MSI, e il README linka le note su Redgate come «Architecture» | ore | medio |

---

# Parte 2 — Le voci in dettaglio

## 1. La CI non fa da gate

**Sforzo:** ore · **Valore:** alto · *lenti: ops, architettura*

Il badge verde su `main` controlla meno di quanto dichiara. Tre cose distinte,
un solo PR.

**Evidenza — verificata sul run reale `31772006949` (conclusion: `success`):**

```
Failed!  - Failed: 3, Passed: 4 ...  DbDelta.Persistence.IntegrationTests.dll
Passed!  - Failed: 0, Passed: 139 ... DbDelta.App.HeadlessTests.dll   ← ultimo = exit code
Skipped! - Failed: 0, Passed: 0, Skipped: 3 ... DbDelta.Compat.Tests.dll (77 ms)
```

- `.github/workflows/ci.yml:46-53` — sei `dotnet test` dentro un solo
  `run: |`. La shell di default sul runner Windows è pwsh, che non aborta su
  exit code non-zero di un comando nativo: **il passo prende l'exit code
  dell'ultimo**. Cinque progetti su sei non possono far fallire il job.
  I tre rossi sono `DockerImageNotFoundException` (immagine Linux su runner
  Windows).
- `tests/DbDelta.Compat.Tests/CompatMatrixTests.cs:167-181` e
  `tests/DbDelta.Persistence.IntegrationTests/Sql/SqlExecutorTests.cs:135-147`
  — due sonde Docker copiaincollate che controllano TCP `localhost:2375` e
  `\\.\pipe\docker_engine`, **mai il socket unix di Linux**, e risultano vere
  su Windows dove l'immagine Linux non può girare. La matrice notturna
  2017/2019/2022 salta 3/3 in 77 ms senza tirare un'immagine: non ha **mai**
  girato.
- `tests/` contiene 11 progetti, `ci.yml` ne nomina 9: `DbDelta.Property.Tests`
  e `DbDelta.Shared.UnitTests` non compaiono in nessun job.

**Proposta:** (a) unire le sei righe con `&&` così il primo fallimento ferma il
passo; (b) cancellare `IsDockerAvailableAsync` da **entrambi** i file — in
Compat.Tests `DBDELTA_COMPAT=1` è impostato solo dal job notturno dove Docker è
garantito, quindi un container che non parte **deve** andare rosso; in
`SqlExecutorTests` avvolgere `StartAsync` in `try { } catch { _container = null; }`
e lasciar scattare le guardie `if (_connectionString is null) Assert.Skip(...)`
che già esistono; (c) aggiungere i due `dotnet test` mancanti. **Netto −35
righe**, e con le sonde oneste l'intera lista a mano può collassare in un solo
`dotnet test DbDelta.sln`.

## 2. Il compare non si annulla e non sopravvive a un server lento

**Sforzo:** ore · **Valore:** alto · *lenti: UX, performance, affidabilità*

**Evidenza:** `src/DbDelta.App.Avalonia/ViewModels/AppStateViewModel.cs:241`
— `CompareAsync(CancellationToken ct)` fila il token correttamente dentro
`src.LoadAsync(ct)` (:284) e `tgt.LoadAsync(ct)` (:291), ma **tutti e cinque i
call site passano `None`**: `App.axaml.cs:99`, `MainWindowViewModel.cs:476`,
`:743`, `:779`, `:922`. L'overlay `Views/MainWindow.axaml:563-591` ha titolo,
ProgressBar e testo di stato — nessun pulsante.

Sull'altra metà: `src/DbDelta.Providers.LiveDb/ConnectionFactory.cs` sono
5 righe di `new SqlConnection` + `OpenAsync`, e un grep di `CommandTimeout` sotto
`src/DbDelta.Providers.LiveDb` restituisce **zero** occorrenze (solo Persistence
ce l'ha). Tutte le ~23 letture di catalogo stanno sul default ADO.NET di 30 s,
incluse `TableReader.ColumnsQuery` (join su 4 viste per l'intero database) e la
sottoquery per riga di `IndexReader` su `sys.partitions`. Il percorso di deploy
ha ricevuto 600 s con 25 righe di commento; il percorso di lettura che scandisce
l'intero catalogo non ha ricevuto niente.

**Proposta:** due modifiche indipendenti.
(a) `[RelayCommand(IncludeCancelCommand = true)]` su `CompareAsync`, i cinque
call site instradati sul token del comando, un pulsante **Annulla** neutro
nello StackPanel dell'overlay, e un `catch (OperationCanceledException) { }`
sopra il catch esistente a `:321` così annullare non viene riportato come
errore. **Non è** il W3-5 rinviato: il compare è in sola lettura e non tiene
transazioni.
(b) In `ConnectionFactory.OpenAsync`, far girare la stringa attraverso
`SqlConnectionStringBuilder` e impostare `CommandTimeout = 300` quando il
chiamante l'ha lasciato al default — tre righe che coprono tutte e 23 le query,
app e CLI insieme, e un `Command Timeout=` nella stringa dell'utente continua a
vincere.

## 3. Il dialogo di conferma non mostra lo script né dice cosa elimina

**Sforzo:** ore · **Valore:** alto · *lenti: UX, affidabilità, prodotto*

Con l'undo rinviato, questo dialogo **è** l'ultimo cancello — e si legge come
un conteggio neutro.

**Evidenza:** `ViewModels/MainWindowViewModel.cs:871` costruisce
`string script = DeployScriptBuilder.Build(...)`; `:881-893` costruisce
`ConfirmExecuteViewModel` con soli conteggi e riepiloghi di endpoint, e lo
script finisce dritto nella closure `executeAsync`.
`ViewModels/ConfirmExecuteViewModel.cs:18-53` — il costruttore non prende
nessun parametro script; il suo stesso doc-comment a `:45-46` dice «Selected
rows that exist only on the target (will be dropped)» mentre
`Views/ConfirmExecuteDialog.axaml:81-88` lo rende con l'etichetta neutra
« solo destinazione». Le parole DROP / elimina / definitivo non compaiono da
nessuna parte nel dialogo. Dietro quel conteggio:
`TableScriptEmitter.cs:251` (`DROP TABLE`), `:319-320`
(`ALTER TABLE … DROP COLUMN`, che l'intestazione del file stesso segnala come
«DROP COLUMN takes the data»), `:691` (il `DROP TABLE` del rebuild). Per
leggere l'SQL oggi l'utente deve annullare, lanciare **Genera script**,
rispondere una seconda volta al preflight di backfill
(`MainWindowViewModel.cs:1007-1021`), salvare un file e aprire un editor
esterno.

**Proposta:** passare lo `script` già costruito a `ConfirmExecuteViewModel` e
legarlo a un pannello monospazio in sola lettura dietro un Expander «Mostra
script (N righe)», così il dialogo mantiene la sua altezza di default;
`LastRunDialog.axaml:79-81` ha già il pattern copia-negli-appunti da riusare.
Rietichettare `OnlyInTargetCount` come «N da ELIMINARE» nel colore di pericolo
e listare i nomi qualificati — le `DifferencePairs` sono già in mano a
`MainWindowViewModel.cs:884`. Nessun emitter nuovo, nessun classificatore.

## 4. Il diff pane può mostrare un oggetto sotto il nome di un altro

**Sforzo:** ore · **Valore:** alto · *lenti: UX, affidabilità, performance*

**Evidenza:** `ViewModels/DiffViewerViewModel.cs:56-82` — il doc XML dichiara
che `LoadAsync` «clears previous state»; il codice non lo fa.
`ObjectQualifiedName = row.QualifiedName` a `:64` gira **prima** dei due await
del resolver a `:65-68`, `Rows`/`Sections` non vengono mai svuotati, e non c'è
catch — solo `finally { IsLoading = false; }`. L'unico call site è
fire-and-forget: `AppStateViewModel.cs:186`
`_ = DiffViewer.LoadAsync(value, CancellationToken.None);`, quindi
l'eccezione è inosservata. Il resolver lancia liberamente:
`LiveDbObjectBodyResolver.cs:33` apre una `SqlConnection` nuova a ogni
chiamata e `:412` lancia `InvalidOperationException` su un tipo di chiave
inatteso.

Due difetti secondari nelle stesse 25 righe: entrambi gli await usano
`.ConfigureAwait(false)` — **gli unici nei view-model dell'app** — quindi
`Rows`/`Sections`/`IsLoading` pubblicano in cinque binding `ItemsControl` da un
thread del pool; e navigare la griglia con le frecce avvia un caricamento non
annullato per riga, quindi il pannello può fermarsi su una riga che l'utente ha
già lasciato. Lo stub dei test headless restituisce `Task.FromResult`
(`tests/DbDelta.App.HeadlessTests/ViewModels/DiffViewerViewModelTests.cs:126-134`)
e completa in modo sincrono: ecco perché niente di tutto questo viene preso.

**Proposta:** in `LoadAsync`, svuotare
`Rows`/`Sections`/`SourceBody`/`TargetBody`/`ObjectQualifiedName` come primo
statement, avvolgere il corpo in un catch che lascia i pannelli vuoti e scrive
su `AppState.LastError` (il banner esiste già), e cambiare entrambi i
`ConfigureAwait(false)` in `(true)`. Un `CancellationTokenSource` in
`AppStateViewModel.OnSelectedRowChanged` che annulla il caricamento precedente.
~15 righe. Pannelli vuoti più un errore battono un diff vecchio sotto il nome
sbagliato.

## 5. Il contratto della CLI: exit code e JSON

**Sforzo:** ore · **Valore:** alto · *lenti: affidabilità, prodotto, architettura*

**Evidenza:** `src/DbDelta.Cli/Commands/ScriptCommand.cs:102`
`return ExitCodes.SuccessNoDifferences;` è l'**unico** return sul percorso di
successo, con `comparison` in scope a `:79` — al contrario di
`CompareCommand.cs:74-80`, che ramifica sul conteggio delle differenze verso
`SuccessDifferencesFound = 1` (`ExitCodes.cs:9`). Una pipeline che gatea su
`dbdelta script` non può distinguere un ambiente pulito da uno con drift
pendente.

Stesso metodo, `ScriptCommand.cs:73-81`: `opts` viene calcolato da
`--include-permissions`, poi `Compare(..., ComparisonOptions.Default)` viene
chiamato con `Default` lo stesso mentre `Generate(..., options: opts)` riceve
quello vero — innocuo oggi solo perché `ComparisonEngine` non legge mai
`IgnorePermissions`, cosa scritta da nessuna parte.

Doppio contratto: `src/DbDelta.Cli/Output/JsonFormatter.cs:18-26` emette
`{kind, schema, name, status}` da un tipo anonimo e si autodefinisce «Stable,
machine-readable contract»; `src/DbDelta.Shared/Dtos/DifferenceDto.cs:11-17`
emette `{kind, schemaName, objectName, status, lastModifiedSource,
lastModifiedTarget}`. Solo la forma di `report` ha un test
(`tests/DbDelta.Cli.AcceptanceTests/ReportCommandTests.cs:54-59`).

**Proposta:** tre modifiche piccole — ritornare
`comparison.Differences.Any(d => d.Status != DifferenceStatus.Identical) ? SuccessDifferencesFound : SuccessNoDifferences`
da `ScriptCommand`; passare `opts` a `Compare` già che si è lì; cancellare
`JsonFormatter.cs` (~30 righe) e far chiamare a `CompareCommand` lo stesso
`JsonReportGenerator` che `report` usa già, così l'unico contratto superstite è
quello testato.

## 6. Cablare il report HTML già finito

**Sforzo:** ore · **Valore:** alto · *lente: UX*

**Evidenza:** `src/DbDelta.Core/Reports/HtmlReportGenerator.cs:14`
`Generate(ComparisonResult)` ritorna un documento autonomo, coperto da
`tests/DbDelta.Core.UnitTests/Reports/HtmlReportGeneratorTests.cs`. Il suo
unico chiamante di produzione è `src/DbDelta.Cli/Commands/ReportCommand.cs:83`;
un grep di `HtmlReportGenerator` su `src/DbDelta.App.Avalonia` non trova nulla.
L'app ha già in mano l'input esatto: `AppStateViewModel.cs:72`
`LastComparisonRaw` **è** il `ComparisonResult` che il generatore prende. Chi
vuole allegare un diff di schema a una richiesta di modifica deve rifare tutto
il confronto dalla CLI con credenziali appena digitate nella GUI.

**Proposta:** un `ExportReportCommand` su `MainWindowViewModel` che copia la
coda di `DeployAsync` (`MainWindowViewModel.cs:816-839`:
`FilePickerSaveOptions` → `OpenWriteAsync` → `StreamWriter` → `StatusText`) con
filtro `*.html`, più un pulsante neutro accanto a «Genera script» nella barra
azioni dei risultati (`MainWindow.axaml:449-462`). Zero codice di motore nuovo.

## 7. La griglia dei risultati: tre difetti, un file

**Sforzo:** ore · **Valore:** medio · *lenti: UX, performance*

**(a) Header che promettono un sort che non esiste.**
`Views/ResultsGridView.axaml:41` `CanUserSortColumns="True"` più
`CanUserSort="True"` su cinque `DataGridTemplateColumn` (`:188`, `:209`,
`:222`, `:291`, `:306`) — un grep di `SortMemberPath` su tutto il repo dà
**zero** occorrenze, e una template column non ha altro modo di derivare un
percorso di ordinamento. Ogni clic su «Ultima modifica (dest)» non fa niente.

**(b) La ricerca è cieca alle parole che l'utente vede.**
`ViewModels/MainWindowViewModel.cs:601-617` — `SearchPredicate` confronta
`row.Status`, cioè i grezzi `"Different"`/`"OnlyInA"`/`"OnlyInB"`, mentre ogni
superficie visibile mostra `DifferenceRowViewModel.cs:188-195`
`StatusDisplayItalian` («Diversi», «Solo destinazione»), che è anche la chiave
di raggruppamento a `:588`. Digitare quel che dice lo schermo non trova niente.

**(c) Rebuild quadratico.** `RebuildRows` a `:655-670` fa `Rows.Add(row)` per
differenza in un `ObservableCollection` il cui handler `CollectionChanged`
(`:80-85`) spara due `NotifyCanExecuteChanged` più `SelectionSummary`, e
`SelectionSummary` (`:144-156`) valuta `TotalDiffsCount` e `SelectedCount` —
altre due scansioni complete `Rows.Count(...)` — **a ogni singolo Add**. Il
motore emette una coppia per ogni oggetto, Identical inclusi, quindi n è il
numero di oggetti, non di differenze. E `ReapplySavedSelections` a `:489-500`
imposta `row.IsSelected` in un loop nudo che scavalca la guardia `_bulkUpdating`
che sta a `:163-177` **nello stesso file**, e il cui commento dice «letting each
one re-run the counters and both can-execute probes is quadratic and visibly
janky on a real comparison».

**Proposta:** (a) aggiungere
`SortMemberPath="KindOrder"/"LastModifiedSource"/"QualifiedName"/"QualifiedName"/"LastModifiedTarget"`
— tutte e cinque le proprietà esistono già su `DifferenceRowViewModel` — oppure
togliere `CanUserSort` da tutte e cinque così gli header smettono di mentire.
(b) una riga: `|| Contains(row.StatusDisplayItalian, q)`.
(c) alzare il flag `_bulkUpdating` esistente attorno a entrambi i loop e uscire
subito dall'handler `CollectionChanged` mentre è alzato; il blocco di notifica a
`:674-685` rilancia già tutto una volta alla fine. ~6 righe.

## 8. Cancellare le view morte, il connection manager irraggiungibile, Serilog, tre interfacce

**Sforzo:** ore · **Valore:** medio · *lenti: UX, architettura, affidabilità*

Circa 1.500 righe che compilano, vengono spedite e ingannano. Una ha già
causato un bug in produzione — lo dice il commento di MainWindow stesso.

**Evidenza** (grep su `src` e `tests`, referenziati da nessuno tranne se
stessi): `Views/ConnectionPickerView.axaml` (149 righe) + `.axaml.cs`,
`Views/ResultsTreeView.axaml` (131), `Views/EnvironmentBadge.axaml`. Il costo
è a verbale in `MainWindow.axaml:317-321`: «AppState.LastError was previously
bound ONLY in ConnectionPickerView, which is never instantiated — so every
failed compare set the message and nobody ever saw it».

Muoiono con loro i dipendenti esclusivi: `ViewModels/ConnectionPickerSlot.cs`
(139), `StatusFilterOption.cs`, `AppStateViewModel.cs:23-29`
`SourceSlot`/`TargetSlot`, `:137-170` `StatusFilter`/`FilteredDifferences`,
`:335-338` `SwapCommand`; `Views/DiffViewerView.axaml.cs:309-319`
`GetRowBackground` ha zero chiamanti e ha hardcodato l'esadecimale del tema
chiaro.

Il connection manager è irraggiungibile allo stesso modo:
`MainWindowViewModel.cs:403-412` `OpenConnectionManagerAsync` è l'unico sito di
costruzione di `ConnectionManagerDialog`, e `OpenConnectionManagerCommand` non
ha **nessun** binding AXAML. Il che significa anche che il
`TrustServerCertificate=True` hardcodato in `ConnectionEditViewModel.cs:172-177`
(senza chiave `Encrypt`, a differenza delle altre tre copie di
`BuildConnectionString`) è **codice morto, non un buco di sicurezza vivo**.

`src/DbDelta.Cli/Logging/SerilogBootstrap.cs` non ha chiamanti in nessun posto,
eppure Serilog + Sinks.Console + Sinks.File sono referenziati in
`DbDelta.Cli.csproj:13-15` e spediscono tre DLL nella MSI.

`ISchemaSource`, `IProjectStore` e `IScriptEmitter` sono nominati solo dalla
singola classe che li implementa — ogni call site costruisce `LiveDbSource` /
`XmlProjectStore` concretamente, e `IScriptEmitter` è implementato da 2 emitter
su ~15 e consumato da nessuno.

**Proposta:** cancellazione secca — i sei file di view,
`ConnectionPickerSlot.cs`, `StatusFilterOption.cs`, `GetRowBackground`, i membri
elencati di `AppStateViewModel`, `SerilogBootstrap.cs` più le tre
`PackageReference` e le tre `PackageVersion`, e i tre file di interfaccia con le
annotazioni `: IFoo` e `SchemaScriptEmitter.Emit`.

**Una decisione resta dentro questa voce:** le connessioni vengono salvate
automaticamente a ogni compare riuscito (`AppStateViewModel.cs:315-319`) e
alimentano gli «Usati di recente» del picker (`MainWindowViewModel.cs:457`,
`:729`), quindi la lista cresce all'infinito con ogni server digitato male. O
si lega un pulsante della topbar all'`OpenConnectionManagerCommand` che già
esiste, o si cancellano anche i dialoghi del manager e si smette di salvare in
automatico. Poi `dotnet format` e la suite headless.

## 9. Potare `ComparisonOptions`, `ProjectOptions`, i mappings e il parser v1

**Sforzo:** giorni · **Valore:** medio · *lenti: prodotto, architettura*

Il codice **e** il formato `.dbd` pubblicizzano un modello di opzioni ricco e
un remapping di schema. Niente di tutto ciò può influenzare un solo byte di
output. Cancellarlo evita che la prossima sessione provi a «finire il
cablaggio» di una feature mai iniziata.

**Evidenza:** `src/DbDelta.Core/Options/ComparisonOptions.cs:10-37` dichiara 20
flag; l'insieme completo dei lettori è `ComparisonEngine.cs:475`, `:477`,
`:558` e `ScriptGenerator.cs:425`, `:426`, `:677` — **sei**. Verificato per
grep: `IgnoreCollations`, `IgnoreConstraintNames`, `IgnoreTriggers`,
`IgnoreIdentitySeed` e altri nove compaiono **solo** nella dichiarazione
dell'enum. Quattro dei cinque flag in `Default` (riga 36) sono fra i morti. E
tutti e quattro i call site di `Compare` passano `Default` hardcodato
(`AppStateViewModel.cs:299`, `CompareCommand.cs:66`, `ReportCommand.cs:79`,
`ScriptCommand.cs:80`).

Modello parallelo: `src/DbDelta.Core/Abstractions/ProjectOptions.cs:7-23` è
serializzato a `XmlProjectStore.cs:119-128`, riletto a `:230-241`, tenuto a
`ProjectSetupViewModel.cs:187`/`:259` — e legato in **zero** `.axaml`. Idem per
`OwnerMappings`/`TableMappings` (`DbDeltaProject.cs:19-21`): la firma di
`ComparisonEngine.Compare` prende solo `(Database, Database,
ComparisonOptions)`, quindi **nella pipeline non esiste alcuno step di
mapping**. In più `XmlProjectStore` porta un secondo parser intero per uno
schema pre-rilascio — `ParseV1Legacy` (`:333-359`, ritorna `Source: null,
Target: null`, inutilizzabile dall'app), `V1Surrogate` (`:385-400`), i percorsi
di scrittura `LegacyRefs`/`SelectedObjects` — che da solo porta il file da 401
righe a ~240.

**Proposta:** cancellare i quattordici flag non letti e ridurre `Default` a ciò
che sopravvive; cancellare `ProjectOptions.cs`, `OwnerMappingEntry`,
`TableMappingEntry`, i loro membri in `DbDeltaProject`, i blocchi
`XmlProjectStore` e i campi dei view-model; cancellare `ParseV1Legacy` +
`V1Surrogate` e rifiutare `schema<2` con un messaggio chiaro. Tenere il reader
tollerante verso i vecchi elementi così i `.dbd` già spediti continuano a
caricarsi. ~300 righe di `src` e ~120 di test via.

**Se un flag merita di tornare è `IgnoreCollations`**, cablato da capo a piedi:
due server installati ad anni di distanza con default diversi accendono oggi
ogni colonna stringa senza modo di zittirle. Ma va aggiunto **nel commit che lo
cabla**, non prima.

## 10. Cancellare `LiveDbObjectBodyResolver`

**Sforzo:** giorni · **Valore:** alto · *lenti: architettura, performance, prodotto*

Ogni clic su una riga della griglia apre due connessioni SQL ed emette fino a
sedici query di catalogo per recuperare byte già in RAM — e per due delle
tredici tipologie non recupera niente e mostra un pannello vuoto.

**Evidenza:** `src/DbDelta.Providers.LiveDb/ObjectBody/LiveDbObjectBodyResolver.cs`
sono 690 righe che duplicano il livello dei reader — `:33` apre una connessione
nuova per chiamata, `ResolveTableBodyAsync` (`:214-258`) spara object_id,
colonne, quattro query di vincoli e una di indici prima di finire a `:257` con
il puro `TableScriptEmitter.GenerateFullTableBody`. Il commento a `:333-335` lo
ammette: «Since ConstraintReader is internal, we replicate a targeted query
here.»

Il duplicato è **già derivato**: il suo switch sulle tipologie (verificato a
`:35-47`) non ha case per `TableType` né per `Schema` — entrambe in
`KindCatalog.KnownKinds` — quindi cadono su `ResolveModuleBodyAsync`, non
trovano riga in `sys.sql_modules` e aprono il pannello vuoto.

Tutto il necessario è già caricato e pubblico: `LiveDbSource.cs:65-78` attacca
vincoli e indici a ogni `Table`; `DifferencePair` porta `SideA`/`SideB`
(`src/DbDelta.Core/Diff/DifferencePair.cs`); `DifferenceRowViewModel.cs:20`
espone `Pair`; `TableScriptEmitter.cs:209` `GenerateFullTableBody` è
`public static`; `Module.Body` tiene la definizione.

**Proposta:** un `ObjectBodyRenderer.Render(object? side)` puro di ~70 righe in
`Core/ScriptGen` — uno switch sui tipi: `Module` → `.Body`, `Table` →
`GenerateFullTableBody`, `Sequence`/`Synonym`/`UserDefinedType`/`TableTypeUdt`/
`DatabaseUser`/`DatabaseRole`/`Schema` → il loro `EmitCreate` esistente,
`Permission` → `null` (mantenendo quel caso deliberato di sola identità).
`DiffViewerViewModel.LoadAsync` diventa sincrono su `row.Pair.SideA/SideB`; via
il resolver, `IObjectBodyResolver`, `SetResolver`, `IsLoading` e lo
`StubResolver`. **Netto −690 righe**, selezione di riga istantanea, `TableType`
e `Schema` ottengono un corpo per la prima volta, e i rischi async del punto 4
in gran parte evaporano.

> Fare **prima** le guardie economiche del punto 4: una correzione di sicurezza
> non va bloccata dietro un refactor da giorni.

## 11. Dice «Identical» su cose che non ha mai guardato

**Sforzo:** giorni · **Valore:** alto · *lente: prodotto*

Due modalità di errore opposte, entrambe silenziose: un falso «Identical» che
nasconde un columnstore mancante, e un falso «Different» che genera churn di
DROP/ADD su chiavi primarie di produzione (→ punto 12).

**Evidenza:** `src/DbDelta.Providers.LiveDb/Readers/IndexReader.cs:42`
`AND i.type IN (1, 2)` — solo rowstore; `:34` `INNER JOIN sys.tables` esclude
inoltre ogni indice su vista indicizzata.
`src/DbDelta.Core/ObjectModel/TableIndex.cs` non ha **nessun** campo per il tipo
di indice (Name/IsUnique/IsClustered/Filter/Key/Included/DataCompression).

**Il percorso distruttivo, verificato:** `TableScriptEmitter.cs:668-670` — il
rebuild chiama `EmitCreate` per la tabella `_tmp`, che emette solo
`table.Indexes`, poi `:691` fa `DROP TABLE` dell'originale. **Il columnstore non
è mai stato nel modello, quindi niente lo ricrea** — sotto banner verde di
successo.

Lo stesso punto cieco a livello di tipologia: `KindCatalog.cs:13-28` sono
tredici tipologie e `LiveDbSource.cs:50-103` è la lista completa dei reader; un
grep di `temporal_type|is_memory_optimized|extended_properties|is_masked` su
`src/` non trova **niente**, e `ModuleReader.cs:179` `WHERE tr.parent_class = 1`
scarta i trigger DDL a livello di database. Nessun banner, nessun conteggio,
nessun segnale di exit code dice all'utente che il verdetto è parziale.

**Proposta, in due passi, prima l'economico.**
**(a) Il censimento:** una query in più in `LiveDbSource.LoadAsync` che
raggruppa `sys.objects` per `type_desc` sulle tipologie non modellate, più un
conteggio di `sys.indexes` con `type NOT IN (0,1,2)` e di
`sys.extended_properties`; appenderlo a `Database` e renderlo in una riga ambra
sopra la griglia e nell'output di report/CLI — «nessuna differenza fra le 13
tipologie confrontate; 4 assembly CLR, 2 schemi di partizione e 118 proprietà
estese non sono stati esaminati». **Ore**, e converte una famiglia di falsi
negativi silenziosi in un caveat onesto.
**(b)** Allargare il filtro di `IndexReader` a tutti i tipi, portare
`i.type_desc` su `TableIndex`, emettere CREATE per 1/2 come oggi, esporre il
resto come Different-ma-non-scriptabile, e far **rifiutare** a `EmitRebuild` una
tabella che porta un indice che non sa riemettere, **prima** che una riga di SQL
tocchi il server. L'emissione completa del columnstore può seguire dopo: è il
rifiuto che ferma la distruzione silenziosa.

> **Fatta** il 2026-08-16 — `04886b1` (reader + modello), `b250d4f` (il
> rifiuto), `3c32b71` (CLI exit 30, banner dell'app). Due dettagli che l'analisi
> non aveva previsto: il `--` di commento nel corpo del diff viewer, perché un
> pannello che mostra due testi identici su una riga Different è lo stesso bug
> vuoto del punto 4 visto dall'altro capo; e la guardia su
> `EmitRebuildForCompression`, che per un columnstore emetterebbe
> `DATA_COMPRESSION = COLUMNSTORE` — valore che un REBUILD rowstore non accetta.

## 12. I vincoli auto-nominati sono appaiati per nome

**Sforzo:** giorni · **Valore:** alto · *lente: prodotto*

SQL Server deriva il suffisso di un vincolo auto-generato dal suo `object_id`,
quindi `DF__Ordini__Stato__3B75D760` e `DF__Ordini__Stato__1A14E395` non
combaciano mai: ogni tabella con un DEFAULT inline o un CHECK/PK senza nome
risulta Different per sempre e non può mai essere appiattita.

**Evidenza:** `src/DbDelta.Providers.LiveDb/Readers/ConstraintReader.cs` —
verificato: `KeysQuery` (`:13-31`), `ChecksQuery` (`:60-71`) e `DefaultsQuery`
(`:73-85`) proiettano solo `.name`; `is_system_named` non compare in nessuna
delle tre né in alcun modello. `src/DbDelta.Core/Diff/ComparisonEngine.cs:567-597`
— `ConstraintsEqual` fa `bx.ToDictionary(c => c.Name, names)` e ritorna `false`
al primo nome mancante, **prima** che `ConstraintShapeEqual` (`:600+`) giri
mai. Lo script emesso poi droppa l'hash casuale del target e aggiunge quello del
source: `TableScriptEmitter.cs:295-310` e `:399-420`.

`ComparisonOptions.IgnoreConstraintNames` è dichiarato a
`Options/ComparisonOptions.cs:15` e letto da nessuno.
`tests/Fixtures/Parity/README.md:99-102` documenta già il falso positivo
(«Redgate ignores by default, DbDelta currently compares by name»): **è una
feature non implementata, non una delle quattro divergenze cosmetiche
decise**, nessuna delle quali menziona il naming dei vincoli.

**Proposta:** aggiungere `is_system_named` alle tre query e un
`bool IsSystemNamed` a `Constraint`. In `ConstraintsEqual`, separare
l'appaiamento: i vincoli con nome esplicito tengono la chiave per nome, quelli
auto-nominati si appaiano per **forma** (lista colonne + espressione
normalizzata). In `TableScriptEmitter`, non emettere niente quando un vincolo
auto-nominato combacia per forma, e omettere il nome quando uno va creato da
zero, così SQL Server genera il suo. Cablare `IgnoreConstraintNames`, già
dichiarato, sullo stesso percorso. Aggiungere una fixture `DF__` —
`grep -rn "DF__" tests/` oggi è vuoto.

## 13. La modale di primo avvio, e la tastiera

**Sforzo:** ore · **Valore:** medio · *lenti: UX, onboarding*

Chi apre DbDelta per vedere cos'è, e esce da un dialogo che pretende due
connessioni SQL Server, vede l'applicazione **sparire**.

**Evidenza:** `src/DbDelta.App.Avalonia/App.axaml.cs:48-75` — l'handler
`MainWindow.Opened` mostra `ProjectSetupDialog` modale e su `result is null`
esegue `desktop.Shutdown(); return;`. La shell che uccide è uno stato
supportato: il commento dello stesso file a `:52-54` dice «the main shell must
start empty until the user confirms a project», `MainWindow.axaml:364-389`
disegna il pannello di benvenuto che dice all'utente di creare o caricare un
progetto, e il comando **Nuovo** della topbar esiste ed è legato
(`MainWindowViewModel.cs:696` / `MainWindow.axaml:95`).

Sulla tastiera: un grep di `IsDefault|IsCancel|KeyGesture|KeyBinding` su tutto
il progetto Avalonia trova **una** occorrenza non pertinente
(`ProjectEndpointPanelViewModel.cs:424`). `ProjectSetupDialog.axaml:312-317`,
`ConfirmExecuteDialog.axaml:137-153`, `LoadProjectDialog.axaml:92-97` e
`LastRunDialog.axaml:78-85` sono tutti handler `Click` nudi: nessun dialogo del
prodotto risponde a Invio o Esc.

**Proposta:** cancellare il ramo di shutdown di due righe e fare solo `return`
— la shell resta sul benvenuto e **Nuovo** riapre il dialogo. Poi
`IsDefault="True"` su ogni OK/Carica/Chiudi e `IsCancel="True"` su ogni
Annulla: Avalonia collega Invio/Esc da quelle due proprietà senza code-behind.
Opzionale, un piccolo `<Window.KeyBindings>` su MainWindow per
F5/Ctrl+S/Ctrl+O/Ctrl+N — tutti e quattro i comandi esistono già
(`MainWindowViewModel.cs:770`, `:358`, `:415`, `:696`).

## 14. Le docs

**Sforzo:** ore · **Valore:** medio · *lenti: ops, affidabilità*

**Evidenza:** `docfx/articles/getting-started.md:1-40` — verificato: apre con
«Prerequisites: the .NET 10 SDK», poi `git clone`, poi invoca la CLI come
`dotnet src/DbDelta.Cli/bin/Release/net10.0/dbdelta.dll`; **la MSI non compare
da nessuna parte**, mentre `README.md:16-33` ha il testo di installazione buono
(MSI, app nel menu Start, `dbdelta` nel PATH, nota su SmartScreen). Ogni
visitatore che arriva dal link docs viene mandato a clonare e compilare un
prodotto che spedisce un installer con un clic.

`README.md:116` linka `docs/01_architecture.md` · `02` · `03` · `04` come
«Architecture · Data Models · Core Modules · CLI / API» — ma
`docs/01_architecture.md:3` dichiara di descrivere «the internal architecture of
Redgate SQL Compare as reverse-engineered from public documentation», e in
`docs/04_api_endpoints.md` la stringa `dbdelta` ricorre **0** volte contro 52 di
`sqlcompare`.

`CONTRIBUTING.md:46-54` documenta una regola di sync per
`src/DbDelta.App/wwwroot/assets/` — un progetto Blazor Hybrid che non esiste
più — e `:40` sostiene che «Renovate-bot opens PRs weekly» mentre `.github/`
contiene solo `workflows`. `README.md:121` dice ancora che CONTRIBUTING è
«coming soon» mentre lo linka. E `docs/BACKLOG.md:3-11`, il file che CLAUDE.md
dice a ogni sessione nuova di leggere per primo, annuncia ancora rc2 come
release corrente e lista «v1.0.1 FINAL» come aperta: **tre release indietro**.

**Proposta:** quasi tutta cancellazione. Il blocco di installazione del README
in cima a `getting-started.md` con il link `/releases/latest`, e la
compilazione da sorgente declassata; esempi riscritti come `dbdelta …`. Via i
quattro link di ricerca da `README.md:116`, puntare agli articoli docfx; spostare
`docs/00`–`06` sotto `docs/research/` con una riga di intestazione che dice che
descrivono Redgate. Via la Design System Sync Rule e la frase su Renovate da
CONTRIBUTING, e «(coming soon)» dal README. Sostituire il blocco di stato del
BACKLOG con quello attuale.

Già che si è in `docfx/articles/cli.md:37-50`: la sezione `apply` sostiene che
lo script gira «GO-split inside a single transaction», il che è **falso** per
uno script generato da DbDelta (`ApplyCommand.cs:102-106`
`useOwnTransaction = !selfManaged && !noTx`), e la sua tabella di opzioni omette
`--command-timeout` e `--no-transaction`, cioè le due che contano di più.

---

# Parte 3 — Scartati, con motivo

Tenuti qui perché una sessione futura non li riscopra come nuovi.

| Proposta | Perché è caduta |
|---|---|
| «rollback done» nel dialogo di esito | **L'evidenza non regge**: il `false` conservativo è deliberato e documentato due volte come fix di un bug precedente. `SqlExecutor.cs:216-229`: «One branch used to serve two opposite meanings with no discriminant… Returning true for both printed `rolledBack: true` over a half-migrated target.» `LastRunViewModel.cs:60-63` difende la dicitura: «'Non è stato possibile confermare' is not pedantry.» |
| Estrarre il pannello endpoint duplicato nel setup | **Evidenza sovrastimata**: `ProjectSetupDialog.axaml:85-104` e `:181-200` non sono identici, e i pezzi rischiosi sono **già** estratti — entrambe le metà usano gli stessi `c:ServerPicker` e `c:PasswordBox`. Resta boilerplate di layout. |
| Separare il ciclo di vita del progetto da `MainWindowViewModel` | YAGNI: giorni per spostare 300 righe fra file con zero effetto visibile, su un file da 1022 righe grande ma coerente e coperto. La regola delle 500 righe è una linea guida, non un difetto. |
| Collassare gli otto costruttori di corpo per tipologia in `ScriptGenerator` | YAGNI, e il backlog ha già respinto un cambio vicino per un motivo concreto (`docs/BACKLOG.md:135-141`: IDE0010/IDE0072 sotto `TreatWarningsAsErrors` **impongono** gli switch esaustivi). ~130 righe, zero impatto utente, e sporca il file il cui golden porta il contratto di parità con Redgate. |
| Deduplicare le opzioni dei quattro comandi CLI | YAGNI: ~70 righe di duplicazione senza impatto utente. L'unico difetto vero che citava (`ScriptCommand` che passa `Default` a `Compare`) sopravvive dentro il punto 5. |
| Fondere `DbDelta.Shared` in `DbDelta.Core` | Marginale: 95 righe spostate fra assembly, e il beneficio dichiarato (un progetto in meno che la CI può dimenticare) si ottiene più a buon mercato sistemando `ci.yml`, che è il punto 1. |
| Confrontare le extended properties | Territorio §D del backlog, e il flag `TreatExtendedPropertiesAsObjects` su cui si appoggia viene cancellato dal punto 9. La metà che serve all'utente — **dirgli** che non sono state esaminate — è dentro il censimento del punto 11 a una frazione del costo. |
| Leggere la direzione delle colonne chiave su PK e UNIQUE | Valore basso per il raggio d'azione: cambiare `Columns` da `IReadOnlyList<string>` a `IReadOnlyList<IndexColumn>` tocca motore, due emitter e i golden, per una chiave primaria DESC — forma reale ma rara. Da riprendere se un utente la segnala. |
| Filtri `--include`/`--exclude` per la CLI | YAGNI: nessuna traccia di domanda nel repo, nelle issue o nel backlog, e inventa una grammatica di opzioni nuova. |
| Retry sui fault transitori nel percorso di lettura | Speculativo: nessun fallimento riportato, nessun test, nessuna lamentela. Aggiungere un provider di retry a ogni lettura di catalogo per difendersi da un ipotetico sfarfallio VPN è esattamente l'impalcatura che si scrive una volta e si debugga per sempre. |
| Notificare all'app che esiste una versione nuova | Esplicitamente parcheggiato: `docs/BACKLOG.md:105` mette il canale di auto-update nel parking lot v2. |
| Cancellare la reference inutilizzata a WiX UI | Banale e autoannullante: le due opzioni della proposta sono «cancella una riga» o «aggiungi una riga», senza modo di sapere quale il proprietario voglia. |
| Non rieseguire i job per-PR nella schedule notturna | **Controproducente adesso**: è proprio il run notturno (`31772006949`) ad aver fatto emergere i tre test nascosti del punto 1. Da valutare solo **dopo** il fix dell'exit code, e anche allora fa risparmiare il colore di un badge, non un utente. |
| Cancellare `ReadTableObjectIdsAsync` / il round trip extra su `sys.tables` | Reale ma trascurabile: una scansione di catalogo per lato per confronto, ~25 righe (`LiveDbSource.cs:61-63` vs `TableReader.cs:16`). Da assorbire nella prossima modifica che tocca `TableReader`. |
| Renderizzare un corpo per le righe `TableType` | Assorbito: è un sintomo dello switch mancante del resolver, e il punto 10 cancella il resolver, il che sistema `TableType` e `Schema` insieme. |
| Unificare le quattro `BuildConnectionString` (la copia del Connection Manager disabilita la validazione del certificato) | **Non è un problema di sicurezza: la copia derivata è irraggiungibile.** `ConnectionEditViewModel` si entra solo da `ConnectionManagerDialog` (costruito unicamente a `MainWindowViewModel.cs:410`) e dal morto `ConnectionPickerSlot`, e `OpenConnectionManagerCommand` non ha binding in nessun `.axaml`. Assorbito nel punto 8 come cancellazione, che è la correzione giusta. |
| Virtualizzare i pannelli diff e limitare la tabella LCS di `LineDiffer` | Fuori dalla lista **solo perché il punto 10 ne cambia la premessa**: oggi un modulo enorme viene scaricato dalla rete prima ancora di essere disegnato. L'evidenza è reale e confermata: `DiffViewerView.axaml:154`/`:195`/`:232` usano `ItemsPanel` `StackPanel` non virtualizzanti; `LineDiffer.cs:86` alloca un `int[m+1,n+1]` grezzo, ~100 MB per un corpo da 5.000 righe e `OutOfMemoryException` oltre le ~23.000, mentre l'input più grande in `LineDifferTests` è di 3 righe. **Da riproporre come voce «modulo grande» dopo la cancellazione del resolver**, e la metà economica vale già da sola: tagliare prefisso e suffisso comuni prima di costruire la tabella LCS sono ~12 righe e tolgono l'allocazione nel caso realistico di quattro righe cambiate in una procedura da 6.000. |

---

# Parte 4 — I pattern sotto le singole voci

**1. La cerimonia non cablata è la classe di difetto dominante**, non la
dimensione né la complessità. Quattordici flag su venti in `ComparisonOptions`
dichiarati e letti da nessuno; tre `ProjectOptions` su cinque che fanno
andata-e-ritorno nel file `.dbd` e non raggiungono alcun motore;
`OwnerMappings`/`TableMappings` persistiti e versionati mentre
`ComparisonEngine.Compare` non ha nemmeno un parametro di mapping; tre
interfacce con una implementazione e zero consumatori; tre UserControl e un
intero albero di dialoghi del connection manager irraggiungibili; un generatore
di report HTML testato che la GUI non può invocare; e un `CancellationToken`
filato correttamente attraverso quindici reader e poi ricevuto come `None` in
tutti e cinque i call site. Circa **1.500 righe si cancellano di netto, e
quattro di quelle cancellazioni sistemano anche un bug visibile all'utente**.

**2. Teatro della verifica.** Le tre cose che certificano la qualità
sovrastimano tutte quel che controllano: il passo CI su Windows inghiotte
l'exit code di cinque progetti su sei (tre stanno fallendo adesso), la matrice
compat notturna cerca un demone Docker in due posti dove su un runner Linux non
sarà mai e riporta successo in 77 ms senza una sola asserzione, e due progetti
di test su undici non sono nominati in nessun job. Il meccanismo che nasconde
tutti e tre è la **stessa** sonda da venti righe, copiaincollata in due file.

**3. L'ingegneria di sicurezza si ferma al bordo del codice.** Il percorso di
deploy in sé è genuinamente attento — envelope `XACT_ABORT`, redazione della
stringa di connessione, un feeder di drop FK con un commento che spiega perché
deliberatamente droppa in eccesso, un flag di rollback che sotto-dichiara
apposta. Ogni superficie **attorno** a quel percorso non ha ricevuto niente di
tutto ciò: il dialogo di conferma non mostra lo script che già tiene in mano ed
etichetta i DROP come «solo destinazione»; i banner d'errore sono `TextBlock`
non selezionabili; il diff pane inghiotte le eccezioni in un `Task` scartato.
**Dove i commenti sono lunghi il codice è giusto: i difetti si addensano
esattamente dove nessuno ha scritto un commento.**

**4. Il verdetto è più stretto di quanto la parola «Identical» implichi, e
niente lo dice all'utente.** Indici di tipo 3–7 (columnstore, XML, spaziali,
hash), ogni indice su vista indicizzata, i vincoli auto-nominati, le extended
properties, le tabelle temporali/mascherate/memory-optimized/esterne e i trigger
DDL a livello di database passano tutti come invisibili o
permanentemente-Different. Una query di censimento e un banner ambra costano ore
e convertono l'intera famiglia in un caveat onesto.

**5. Le vittorie più economiche sono modifiche di una riga a codice che già sa
la risposta.** `_bulkUpdating` esiste in `MainWindowViewModel` con un commento
che nomina l'esatto rischio quadratico, e il loop di restore 300 righe più in là
lo scavalca. `StatusDisplayItalian` è la chiave di raggruppamento della griglia
stessa mentre il predicato di ricerca confronta l'enum grezzo. Lo script di
deploy è una variabile locale uno statement sopra il costruttore del dialogo che
non lo prende. `GenerateFullTableBody` è pubblico e puro mentre un resolver da
690 righe riquera il server per il suo input. **Quasi ogni voce ad alto valore
di questa lista è riconnettere due cose che il codice ha già costruito.**

---

# Parte 5 — Code review `v1.0.1..HEAD`

Quattro commit: pin SSH.NET (GHSA-q939-rpr3-3284), tema tri-stato con
persistenza, marker «chi ha cambiato per ultimo», note 1.0.2.

**Verificato e regge:** i dizionari `Light` e `Dark` di `Themes.axaml` hanno
insiemi di chiavi **identici**, quindi passare `RequestedThemeVariant` da
`Light` a `Default` non può produrre una risorsa irrisolta con l'OS scuro al
primo avvio. Il `GetAwaiter().GetResult()` allo startup è davvero senza
deadlock: `LoadThemeAsync` attende con `ConfigureAwait(false)`, nessuna
continuazione vuole il thread UI. `HasComparableDates` esclude correttamente
righe identiche, righe da un solo lato e date nulle. La rinomina `[RelayCommand]`
in `CycleThemeCommand` non lascia riferimenti stale a `ToggleTheme`/`IsDarkTheme`
in nessun punto del repo. `JsonUiSettingsStore` degrada a `System` su ogni
percorso corrotto/assente/schema-futuro, e i test coprono ciascuno.

**Finding (3, tutti low):**

1. **`src/DbDelta.App.Avalonia/Views/Controls/LastModifiedCell.axaml:23` — il
   tooltip è attaccato incondizionatamente, quindi mente sulle celle senza
   marker.** `ToolTip.Tip="{Binding Tip, ElementName=Self}"` sta sullo
   `StackPanel` radice, ed entrambe le colonne della griglia passano sempre
   `NewerTooltip`. Passando sul lato *più vecchio*, su una riga identica, su una
   riga da un solo lato o su una Sequence/Synonym con cella vuota compare
   «Modifica più recente fra i due lati…» su una cella che non lo afferma.
   *Fix:* legare `Tip` a `IsNewer ? NewerTooltip : null` nel view-model, o
   condizionare il tooltip su `IsNewer`.

2. **`src/DbDelta.App.Avalonia/ViewModels/DifferenceRowViewModel.cs:104` — il
   confronto più-recente/più-vecchio avviene fra gli orologi locali di due
   server, quindi può indicare il lato sbagliato.**
   `LastModifiedSourceDisplay` rende `modify_date` verbatim **proprio perché** è
   ora locale del server (il commento a `:71` lo dice), ma poi
   `IsSourceNewer`/`IsTargetNewer` confrontano quei due valori `Unspecified` con
   `>`. Caso concreto: source in UTC+0 modificato alle 10:00 UTC, target in
   UTC+2 modificato alle 11:00 locali (= 09:00 UTC). Il target è genuinamente
   più vecchio, ma 11:00 > 10:00 e la freccia verde marca il target come il lato
   più recente — e la freccia è un suggerimento direzionale «allinea in questo
   verso». Il tooltip si smarca («secondo l'orologio di ciascun server»), ed è
   per questo che resta low, ma quella smarcatura si raggiunge solo in hover.

3. **`tests/DbDelta.App.HeadlessTests/ViewModels/ThemeCycleTests.cs:47` —
   quattro test mutano il globale `Application.Current.RequestedThemeVariant` e
   non lo ripristinano.** `CycleThemeCommand` scrive la variante
   sull'`Application` condivisa; solo
   `Cycling_applies_the_variant_to_the_running_application` (`:122`) la avvolge
   in try/finally. `Icon_flags_follow_the_cycle` lascia l'app su `Default`,
   `Chosen_theme_survives_a_restart` e `Cycling_still_switches_…` la lasciano su
   `Dark`. Oggi non rompe niente perché gli unici test che asseriscono colori
   (`AccentBandContrastTests`, `LastModifiedCellTests`) fissano la variante da
   sé — ma il prossimo test headless che legge un brush dipendente dal tema
   senza fissarla sarà dipendente dall'ordine, quindi flaky. Stesso
   `try/finally` dell'unico test che già lo fa.

**Non-finding valutati e scartati:** `CentralPackageTransitivePinning` + il pin
SSH.NET 2026.0.0 raggiunge solo i progetti di test (Testcontainers è l'unico
percorso) e pinna verso l'alto, quindi nessun downgrade né effetto sul pack. Il
percorso `.tmp` fisso di `SaveThemeAsync` è sicuro: `FileMode.Create`
sovrascrive un orfano, e `AsyncRelayCommand` di default non è concorrente e
disabilita il pulsante durante la scrittura. L'intestazione del CHANGELOG
combacia con il regex `^##\s+\[(?<ver>[^\]]+)\]` di
`scripts/docs/build-version-history.ps1`, e non c'è nessun file di versione da
alzare — il workflow di release timbra `-p:Version` dal tag.
