# HANDOFF — cosa resta dopo la chiusura dei sei finding

**Da leggere per primo in una sessione nuova.**

- **HEAD:** `40c7461` su `main`, origin sincronizzato, working tree pulito.
- **Test:** 600 verdi su 10 progetti (Compat esclusa, gira solo di notte).
- **Gate formato:** `dotnet format DbDelta.sln --verify-no-changes` esce 0. Build senza avvisi.
- **Chiuso:** i sei finding di `2026-07-30-handoff-criticals.md` (C1, C2, C3, S2,
  S3, S11) in `2c7776d..e249d24`, **più** una critical e sette high che quella
  stessa ondata aveva introdotto, in `40c7461`.
- **Ancora dovuto prima di rc5:** lo **smoke live 243 → 242**. Serve la password
  sa, quindi il proprietario.

## La lezione di questa ondata

C2 ha reso accoppiabili due grafie della stessa tabella. Da quel momento
`ScriptGenerator` — che tiene una dozzina di set chiavati su **value tuple di
stringhe**, la cui uguaglianza è ordinale — ha cominciato a riempire un set da un
lato e a interrogarlo dall'altro. Il comparer era stato infilato nel motore e
nell'emitter, non nelle chiavi. Risultato: `rebuildTargets` non trovava la
tabella ricostruita (Msg 3726 sul DROP TABLE) e `fkDropKeys` emetteva due DROP
per lo stesso vincolo (Msg 3728). Entrambi abortiscono il deploy.

**La regola, per la terza volta in questo repo:** quando cambi la semantica di
un confronto, cerca *ogni* struttura che confronta la stessa cosa — non solo
quelle che il difetto originale nominava. `NameKey` esiste perché una chiave
case-aware non si può dimenticare a un call site, mentre normalizzare i call site
sì.

## Aperto — nessuno di questi distrugge dati

Tutti confermati da una verifica adversariale che ha aperto i file e, dove il
finding lo affermava, rieseguito il probe. Ordinati per costo di non farli.

### Correttezza

| Sev | Cosa | Dove |
|-----|------|------|
| M | Il re-add orchestrato applica la forma FK **della sorgente** a una tabella che l'utente non ha selezionato | `ScriptGenerator.cs`, feeder sulla colonna referenziata |
| M | Il pass di re-add emette `ALTER TABLE … ADD CONSTRAINT` per una tabella che lo script non crea mai (holder solo-sorgente non selezionato) | `ScriptGenerator.cs`, feeder del rebuild |
| M | Il secondo `DependencyResolver.Order` (S3) può lanciare `DependencyCycleException` su input che prima venivano scriptati: F legge T e T ha una colonna calcolata che chiama F, entrambi `OnlyInB`. Serve un `try/catch` con fallback su `createOrder.Reverse()` | `ScriptGenerator.cs` |
| M | `MapByIdentity` lancia e la CLI esce **1**, che `ExitCodes` definisce «successo, differenze trovate». Un `try/catch` attorno a `InvokeAsync` in `Program.cs` copre tutti e quattro i verbi | `src/DbDelta.Cli/Program.cs` |
| M | `SqlTypeFormatter` lascia passare il nome di tipo non quotato quando inizia con `[` o contiene `.`; il commento che giustifica il passthrough è falso per ogni produttore del repo | `SqlTypeFormatter.cs` |
| L | `EmitIndexDelta` / `EmitFkAdds` e i loro comparatori di forma accoppiano ancora i nomi ordinalmente: su target CI un indice identico viene droppato e ricreato | `ScriptGenerator.cs` |
| L | `BracketedNames` non capisce il `]]` che S11 stesso produce, quindi `DependsOnColumn` manca un CHECK su una colonna con `]` nel nome | `TableScriptEmitter.cs` |

### Guardie e test

| Sev | Cosa |
|-----|------|
| M | Il lint di `IdentifierQuotingTests` è aggirabile da forme presenti nei file che scansiona (`AppendLine("…[")`, `string.Concat`, `"[" + x + "]"`) e non copre `DbDelta.Providers.LiveDb`. Le due regex vanno unificate e allargate a `Append(Line\|Format)?\(` |
| L | `Source_side_edges_cannot_order_the_drop_and_the_fallback_gets_it_wrong` fissa il fallback rotto come comportamento atteso: da cancellare (il test schemabound copre già il meccanismo) o da marcare come limite noto |

### Commenti e doc ora falsi

Il repo ha già pagato due critical per un commento falso, quindi contano.

- `ScriptGenerator.cs` — «Three sources feed this» contraddice il doc di classe
  («Four feeders») e i **cinque** call site di `AddFkDrop`.
- `ScriptGenerator.cs` — il commento del pass FK attribuisce ancora lo skip set
  «al rebuild orchestrator» dopo che S2 l'ha rinominato e allargato.
- `ScriptGenerator.cs` — il nuovo feeder reintroduce la giustificazione che S3
  ha cancellato dal gemello dieci righe sopra.
- `Database.cs` — il doc di `DefaultCollation` parla solo di clausole `COLLATE`:
  in realtà è l'input che decide **tutta** la regola di accoppiamento, e `null`
  significa case-insensitive.
- `AppStateViewModel.cs` — il doc di `TargetDependencies` e il commento in
  `CompareAsync` dicono ancora che gli archi target servono solo a un eventuale
  rollback; da S3 ordinano il pass di DROP dello script in avanti.
- `TableScriptEmitter.cs` — il doc di `ColumnsDroppedOrAltered` dice «target-side
  column names», ma metà del set porta la grafia della sorgente.
- `AppStateViewModel.cs` — «impostato qui e in nessun altro posto» per
  `PublishComparison` non è imposto dal codice, e `MainWindowViewModelTests`
  già lo viola.

### Prodotto, non difetti

- Il banner di staleness dice che «le connessioni sono diverse», ma il caso che
  lo attiva più spesso è un refresh fallito **con gli stessi endpoint**. Riformulare
  senza affermare nulla sulle connessioni.
- Il gate non si azzera dopo l'esecuzione riuscita dell'app: lo stesso script
  resta rieseguibile. È una scelta di prodotto (comoda o pericolosa a seconda
  del flusso), non un bug.

## Vale ancora

Tutto in `2026-07-30-handoff-criticals.md`: la sezione «Cosa NON fare», le
trappole di processo e l'appendice del 2026-07-31.

Una in più, pagata due volte in questa sessione: **un probe di mutazione che non
compila fa stampare a `dotnet test --no-build` il verde dell'assembly
precedente.** Controlla `Errori: 0` prima di credere al risultato. Le due cause
qui sono state un probe scritto in LF da uno script Python (gate ENDOFLINE) e un
`IDE0060`/`CS8321` su un parametro o una funzione locale rimasti inutilizzati
dalla mutazione stessa.
