# HANDOFF — 4 blocker + 1 regressione da chiudere prima di toccare l'undo

**Da leggere per primo in una sessione nuova.** Non serve ricostruire nulla: tutto
quello che conta è qui o nei due documenti citati.

- **HEAD:** `3723ae7` su `main`, origin **sincronizzato**, working tree pulito.
- **Test:** 532 verdi, 3 skip di design (compat matrix, scheduled-only).
- **CI:** verde su `ed051a3`; `472eb56` e `3723ae7` sono test/docs-only.
- **Gate formato:** `dotnet format --verify-no-changes` esce 0.

## Regola d'ingaggio

**NON iniziare W3-1 (tassonomia avvisi) né W3-2 (`down.sql`).** Prima chiudere i 5
item qui sotto. Il motivo non è prudenza generica: **B1 dimostra che il
meccanismo su cui l'undo dovrebbe poggiare non funziona sul path che gli utenti
usano**, e costruirci sopra eredita la cecità dal primo giorno.

## Contesto in due righe

Una review multi-agent del codebase (2026-07-30) ha prodotto 96 finding verificati
e un piano a ondate. Le ondate W0, W1-2/3/4/5, W2-1 e W3-0 sono state eseguite in
15 commit (`19131d8..ed051a3`). Poi una **seconda review adversariale su quel
diff** — agent a cui non è stato detto il razionale delle scelte, più un agent in
worktree isolato che ha rimosso ogni fix e rieseguito il suo test — ha trovato 46
finding sopravvissuti alla confutazione.

Leggere, in ordine:
1. `docs/review/2026-07-30-self-diff-adversarial-review.md` — **il verdetto e i 4
   blocker sono nella sezione «Verdetto»**; sotto ci sono i finding integrali con
   evidenza, scenario e fix per ognuno.
2. `docs/review/2026-07-30-undo-architecture.md` — il design dell'undo, da
   riprendere solo dopo i blocker.
3. `docs/review/2026-07-30-full-codebase-review.md` — il digest a ondate, per
   sapere cosa resta dopo.

---

## L'errore da non ripetere

B1 esiste perché ho verificato le mie modifiche rileggendo **il mio ragionamento**
invece del codice dei chiamanti. Avevo scritto un commento che diceva «scanning
`result.Differences` unfiltered is the same trick the inbound-FK orchestration
uses» — vero per la CLI, falso per la GUI, e non ho aperto `DeployScriptBuilder`
per controllare.

Per ognuno dei fix sotto: **aprire ogni chiamante prima di dichiarare fatto.**
`grep` del simbolo, non memoria.

---

## B1 — critical — i recuperi "risultato non filtrato" sono codice morto sul path GUI

**File:** `src/DbDelta.Core/ScriptGen/DeployScriptBuilder.cs:63-66`

```csharp
ComparisonResult syntheticResult = new(selectedPairs);
string body = _generator.Generate(syntheticResult, selection: null, ...);
```

Dentro `Generate`, `result.Differences` **è** la selezione. Due pass aggiunti in
questa ondata dipendono dall'opposto:

- `ScriptGenerator.cs:350` — ricrea i trigger *Identical* su una tabella ricostruita
- `ScriptGenerator.cs:159` — droppa le FK tenute da una tabella *Identical* che
  puntano a una tabella droppata o ricostruita

E una coppia Identical **non può** entrare nella selezione:
`DifferenceRowViewModel.cs:140` → `IsSelectable => !IsIdentical`; `_isSelected`
default `false`; `MainWindowViewModel.SelectedPairs()` filtra su `IsSelected`.

**Danno concreto oggi:** target con `dbo.Fatture` + trigger di audit
byte-identico; la sorgente aggiunge IDENTITY a `Id` → rebuild. L'utente spunta la
riga Fatture e preme Esegui. Lo script fa `DROP TABLE Fatture` / `sp_rename` e
**nessun batch trigger**. `Success = true`. Produzione perde il trigger in
silenzio. Il messaggio del commit `95313c4` è quindi **falso come spedito** per i
trigger (indici e FK in uscita sono davvero corretti: leggono `pair.SideA`).

**Fix.** `AppState.LastComparisonRaw` ha già il risultato completo. Cambiare
`Build` per accettarlo e chiamare `_generator.Generate(fullResult, selection: selectedPairs, ...)`.
La CLI lo fa già così ed è corretta — vedere `src/DbDelta.Cli/Commands/ScriptCommand.cs:81`
come riferimento.

- firma: `DeployScriptBuilder.Build` (`:34`)
- 2 call site: `MainWindowViewModel.cs:603` e `:636`
- **effort M**

**Test obbligatorio:** un test che passa **attraverso `DeployScriptBuilder`** con
un `ComparisonResult` che contiene un trigger Identical **non presente** in
`selectedPairs`. Oggi `TableRebuildPkSwapTests.Rebuild_recreates_an_identical_trigger_and_keeps_it_disabled`
costruisce una forma che la GUI non può produrre, quindi non può cogliere questo.

---

## B2 — `forcedIndexRecreates` ha chiave sul solo nome indice

**File:** `src/DbDelta.Core/ScriptGen/ScriptGenerator.cs:204`, `:217`, `:323` e
`EmitIndexDelta`

La riga sopra usa già la tupla giusta — `blockingIndexDrops.Add((sideB.Schema, sideB.Name, ix))` —
poi `:217` salva `ix.Name` da solo, e lo **stesso set globale** viene passato a
`EmitIndexDelta` di ogni tabella Different. I nomi indice sono unici per
`object_id`, **non** per database: `IX_TenantId` su due tabelle è normale.

- **metà rumorosa:** l'`IX_TenantId` identico della tabella B ottiene
  `mustRestore = true` → `CREATE INDEX` su un indice che esiste ancora → Msg 1913
  → rollback di tutto il deploy.
- **metà silenziosa:** l'`IX_TenantId` della tabella B era stato *rimosso* nella
  sorgente → `alreadyDropped.Contains(t.Name)` salta il DROP → l'indice
  sopravvive in produzione, il tool dice successo, il compare successivo mostra
  ancora Different.

**Fix:** `HashSet<(string, string, string)>`, e testare l'appartenenza con
`(src.Schema, src.Name, name)` dentro `EmitIndexDelta`. **effort S.**
Introdotto da `275660a`. Invisibile perché ogni fixture è a tabella singola.

---

## B3 — `fkDropNames` sul solo nome, giustificato da un commento falso

**File:** `src/DbDelta.Core/ScriptGen/ScriptGenerator.cs:131-139`

```csharp
// Constraint names are database-scoped, so the name alone dedupes.   ← FALSO
if (fkDropNames.Add(fk.Name)) { fkDrops.Add((schema, table, fk)); }
```

I vincoli sono righe di `sys.objects` che portano lo `schema_id` della tabella
padre: `dbo.FK_Righe_Testa` e `sales.FK_Righe_Testa` coesistono legalmente. Sono
**schema**-scoped. Due tabelle Different in schemi diversi che perdono una FK
omonima producono due `AddFkDrop`, il secondo ritorna false, viene emesso **un
solo** `DROP CONSTRAINT`, e nulla lo ri-emette (`EmitFkAdds` solo aggiunge). **La
FK che la sorgente ha rimosso sopravvive in produzione e il tool dice successo.**

**Fix:** chiave `(schema, table, name)`. **Cancellare il commento** — e anche
l'affermazione identica preesistente in `TableScriptEmitter.cs:448`, che riguarda
il PK-swap del rebuild. **effort S.** Introdotto da `ec6eb83`.

> Correggere i commenti conta quanto il codice: il lavoro sull'undo li leggerà
> come specifica.

---

## B4 — `RolledBack` dichiara un rollback che non è avvenuto

**File:** `src/DbDelta.Persistence/Sql/SqlExecutor.cs:174-201` (`TryRollbackAsync`)

Il ramo `tx is null` esegue `IF @@TRANCOUNT > 0 ROLLBACK TRANSACTION;` e ritorna
`true` ogni volta che **il comando** riesce. Un ramo, due significati, nessun
discriminante:

- script DbDelta con envelope proprio → `@@TRANCOUNT == 0` significa che
  XACT_ABORT ha già rollbackato → `true` è corretto;
- `--no-transaction` → `@@TRANCOUNT == 0` significa che non c'era transazione e
  tutto è auto-committato → `true` è **una bugia**.

Il doc del record dice che `RolledBack` significa «the target is known to be
unchanged». Il test acceptance `No_transaction_opt_out_leaves_the_earlier_batches_applied`
crea esattamente quello stato e non guarda stdout: `dbdelta apply --no-transaction`
stampa `{"success":false,"rolledBack":true,"transaction":"none"}` su un target
permanentemente a metà.

**Fix:** `ExecuteScalar` su
`IF @@TRANCOUNT > 0 BEGIN ROLLBACK TRANSACTION; SELECT 1 END ELSE SELECT 0` e
ritornare quel valore; ritornare `false` quando i batch sono girati fuori da ogni
transazione. **effort S.** Asserire il campo JSON in **entrambi** i test
acceptance nuovi.

---

## S1 — regressione introdotta da questa ondata: un cambio di solo DEFAULT droppa PK e indici

**File:** `src/DbDelta.Core/ScriptGen/TableScriptEmitter.cs:335` (`ColumnsDroppedOrAltered`),
`:520` (`ColumnShapeEqual`)

La review la classifica «should fix soon», non blocker, perché non compromette la
sicurezza dell'undo. **Va comunque chiusa in questo passaggio** perché è una
regressione che questa ondata ha causato ed è effort S.

`ColumnShapeEqual` include `ExpressionsEqual(DefaultExpression)`, quindi
`((0)) → ((1))` mette la colonna in `touchedColumns`; `DependsOnColumn(PK)` scatta
e `blockingIndexDrops` scatta — mentre la sezione 3 emette solo un `ALTER COLUMN`
no-op. Risultato: Msg 3725 se la PK è referenziata da una FK, altrimenti un
rebuild dell'indice clustered dentro un batch con cap a 60 s. **Prima del diff lo
stesso cambio emetteva `DROP CONSTRAINT DF_…` + ALTER no-op + re-add e finiva in
millisecondi.**

Ironia utile a capire il bug: il doc di `DependsOnColumn` argomenta che i DEFAULT
vanno esclusi — e un cambio di DEFAULT è proprio ciò che popola il set.

**Fix:** separare i due concetti. Un `ColumnRequiresAlterColumn` (tipo,
nullabilità, collation, computed, identity) alimenta `touchedColumns`;
`ColumnShapeEqual` resta com'è per il lato confronto. **effort S.**
**Test:** cambio di solo default su una tabella con PK e indice → nessun DROP.

---

## Test morti — provati empiricamente rimuovendo il fix

Sono l'item più corrosivo della lista: rendono verde una suite che non copre.

| Test | Stato |
|---|---|
| `SqlExecutorTests.cs:156` `ExecuteAsync_rejects_a_negative_command_timeout` | **Morto provato.** Metodo `void`, `act.Should().ThrowAsync<…>()` ritorna un `Task` scartato. Cancellando la guardia resta verde; nessun CS4014 perché il metodo non è `async`. Fix: `async Task` + `await`. In tutta la suite questo è l'**unico** `.ThrowAsync` non awaited (verificato), l'altro in `DpapiCredentialStoreTests.cs:63` è corretto. |
| `ForeignKeyDropOrderingTests.cs` `A_reshaped_fk_is_dropped_up_front_and_re_added_at_the_end` | **Morto provato.** Passa con l'intera ristrutturazione di `ec6eb83` rimossa: il vecchio `EmitFkDelta` emetteva DROP poi ADD nello stesso batch tardivo, quindi `addFk > dropFk` era già vero. Ancorarlo a un altro pass (`"Dropping foreign keys" < dropFk < "Adding foreign keys"`) o rinominarlo. |
| `ForeignKeyDropOrderingTests.cs` `A_dropped_table_does_not_get_a_pointless_drop_for_its_own_fk` | **Morto provato** contro il comportamento pre-fix. Difendibile come guardia in avanti, ma la copertura di regressione effettiva di `ec6eb83` è **2** test, non 4. |
| `TableRebuildPkSwapTests.cs:288`, terza assert di `Rebuild_recreates_every_index_including_the_identical_ones` | `NotContain("DROP INDEX [IX_Invoice_Amount]")` non può fallire: il path delta non emette mai un DROP per un indice identico. Peggio, il commento che la giustifica **afferma il contrario di quello che fa il codice**. Cancellare l'assert e il commento. |
| `SchemaEmissionTests.cs:62` `Schemas_present_on_both_sides_produce_no_row` | Passa anche cancellando `CompareSchemas` dal motore. Non vacuo, ma non coglie un refactor che rimuove la feature. Renderlo positivo-e-negativo nello stesso arrange. |
| `SqlExecutorTests.cs:149-151` — caso "immunità ai commenti" di `ScriptManagesItsOwnTransaction` | Il commento afferma che una menzione in un commento non conta; l'unico caso testato passa solo perché `-` non è whitespace. Commenti a blocco, corpi di procedura indentati e literal multi-riga **matchano tutti**. L'affermazione è falsa e non testata. |
| **Peggio di un test morto** | Cancellando `AppState.SourceDependencies` da **entrambi** i call site, Core 312/312 + Headless 52/52 + Golden 31/31 restano verdi. Il punto di `95549e3` era proprio che solo il path GUI era rotto, e quella metà ha **zero** copertura. Guardia più economica: togliere il default `= null` dal parametro `dependencies` di `Build`, così nessun chiamante può ometterlo in silenzio. |

Il pass di revert ha anche stabilito che **nessun golden si muove per nessuno dei
7 revert**: i golden non coprono la forma del temp-table del rebuild, il
riordino delle FK, né il pass degli indici bloccanti. Gli unit test aggiunti in
questa ondata sono l'unica cosa che fissa l'ordine di emissione.

---

## Dopo i blocker

In ordine, dal digest: `S2` (nessuna FK viene droppata per liberare una colonna
ritipizzata — è la migrazione `int→bigint` per cui `275660a` è stato scritto, e
serve un `forcedFkRecreates` a specchio di `forcedIndexRecreates`), `S3` (l'ordine
"reverse-topological" del DROP pass è vacuo: gli archi sono sempre source-side e
un oggetto `OnlyInB` non appare in nessuno), `S5` (`CREATE`/`DROP SCHEMA` sono
gated dalla selezione → Msg 2760, esattamente il fallimento che `3dd4d09` doveva
prevenire), `S6`, `S10`.

Poi **F-2** (round-trip per kind: seed → LoadAsync → Generate → apply → ricarica →
assert zero differenze; oggi copre 3 kind su 13) e solo dopo W3-1 → W3-2.

## Cosa NON fare

4 finding sono stati confutati e sono elencati in fondo alla review. In più, dal
digest originale: non passare `SqlExecutor` a `useOwnTransaction: true` per gli
script generati (darebbe `@@TRANCOUNT = 2` e il `COMMIT` dello script diventa un
decremento), e XXE nel project store XML, XSS nel report HTML, gestione culture e
MSI sono stati verificati **puliti** — non ri-auditarli.

---

## Trappole di processo già pagate

- **Il tool Bash è Git Bash, non PowerShell.** Le here-string `@'…'@` finiscono
  dentro il messaggio di commit. Usare `git commit -F - <<'EOF'`. Il repo usa il
  trailer `Co-Authored-By`.
- **CI gatea duro** su `dotnet format --verify-no-changes` + `TreatWarningsAsErrors`.
  Analizzatori che mordono: **IDE0046** (forza il ternario al posto di if/return),
  **IDE0061** (corpo a blocco per le funzioni locali), **IDE0055**, **CA1062**.
  Girare `dotnet format` prima di ogni commit.
- **`DbDelta.ScriptGen.GoldenTests` non ha `xunit.assert`** (solo
  `extensibility.core` via Verify) e il csproj avverte di non aggiungere xunit per
  non collidere su `FactAttribute`. Per far fallire un test lì: lanciare
  un'eccezione.
- **Verify** crea un `.verified.txt` vuoto da 3 byte al primo run: `mv -f` del
  received sopra.
- Il tool Write emette LF; `--` è illegale nei commenti XML.
- **Un hook sporca la root del repo** a ogni edit con una lambda C#: `=>` in shell
  non quotata è una redirezione, quindi `l => l.Trim()` crea il file `l.Trim()`.
  Sono file vuoti; nessuno è mai entrato in un commit, ma vanno rimossi. Vale la
  pena sistemare il quoting dell'hook.
- Push automatico a fine lavoro verificato; **tag e release solo con l'ok del
  proprietario**. Terminologia UI: sempre **"Carica"**, mai "Apri".
- Endpoint live per smoke/parity: `192.168.3.243` e `.242`; password sa chiesta
  ogni volta, **mai** salvata.

## Definizione di "fatto"

```bash
dotnet build DbDelta.sln -c Debug              # 0 errori
dotnet test DbDelta.sln                        # 532 + i nuovi, 0 rossi
dotnet format DbDelta.sln --verify-no-changes  # esce 0
```

Più, per ogni fix: **aver aperto ogni chiamante del simbolo toccato** (`grep`, non
memoria), e per ogni test nuovo aver risposto a «passerebbe anche senza il fix?».
Se la risposta non è un no dimostrabile, il test non serve.
