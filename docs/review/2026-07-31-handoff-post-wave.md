# HANDOFF — cosa resta dopo la chiusura del post-wave

**Da leggere per primo in una sessione nuova.**

- **HEAD:** `f2cdcf8` su `main`, working tree pulito. **Non ancora pushato.**
- **Test:** 609 verdi su 10 progetti (Compat esclusa, gira solo di notte).
- **Gate formato:** `dotnet format DbDelta.sln --verify-no-changes` esce 0. Build senza avvisi.
- **Chiuso in `21bf477..f2cdcf8`:** tutti i finding aperti che questo handoff
  elencava — sette di correttezza, due di guardie/test, sette commenti falsi, e
  la riformulazione del banner.
- **Ancora dovuto prima di rc5:** lo **smoke live 243 → 242**. Serve la password
  sa, quindi il proprietario. È l'unica cosa che blocca; W3-1/W3-2 (undo)
  restano dietro di esso.

## Cosa è stato chiuso, e come è stato verificato

Ogni fix porta un test **verificato invertendo il fix sotto di esso** — otto
probe di mutazione, ognuna con `Errori: 0` controllato prima di credere al
risultato.

### Correttezza (`21bf477`)

| Cosa | Prova |
|------|-------|
| Il re-add orchestrato restituiva la forma FK **della sorgente** a una tabella fuori selezione. Ora un holder non selezionato riceve il proprio vincolo verbatim | `OrchestratedFkReAddTests.An_unselected_holder_gets_its_own_foreign_key_back_not_the_sources` |
| Il feeder inbound camminava il lato SORGENTE, quindi una tabella solo-sorgente non spuntata raccoglieva un `ALTER TABLE … ADD CONSTRAINT` contro una tabella mai creata (Msg 4902). Ora cammina il lato TARGET e salta gli holder che lo script droppa | `..._source_only_table_left_unselected_gets_no_add_constraint`, `..._holder_the_script_drops_gets_no_add_constraint` |
| Il secondo `DependencyResolver.Order` (S3) lanciava su un ciclo negli archi target | `DropOrderingTests.A_cycle_among_the_target_edges_falls_back_instead_of_throwing` |
| `EmitIndexDelta` / `EmitFkAdds` accoppiavano i nomi ordinalmente | `CaseDriftKeyTests.An_unchanged_index_and_foreign_key_survive_a_name_case_difference` |
| `SqlTypeFormatter` lasciava passare il nome di tipo non quotato | `IdentifierEscapingTests.A_column_type_name_is_quoted_whatever_punctuation_it_holds` |
| `BracketedNames` non capiva il `]]` | `..._check_over_a_bracketed_column_is_dropped_before_that_column_is_retyped` |
| La CLI usciva **1** su eccezione non gestita | `CompareCommandTests.Returns_exit_code_99_when_the_comparison_cannot_be_represented` (`f2cdcf8`) |

### Guardie (`86a0f7e`)

Il lint di `IdentifierQuotingTests` vedeva 2 forme su 6. Ora i pattern stanno in
una tabella sola con il campione che deve accenderli, la camminata sui file
esiste una volta invece di due, e il corpus arriva a
`DbDelta.Providers.LiveDb`. Un terzo fact verifica che la scansione raggiunga
davvero i file che dichiara — un refuso in un path svuota il corpus in
silenzio. Le quattro evasioni sono state provate iniettandole in un file
scansionato.

`Source_side_edges_cannot_order_the_drop_and_the_fallback_gets_it_wrong` è stato
cancellato: fissava il fallback rotto come comportamento atteso, e il test
schemabound copre già il meccanismo.

### Commenti e prodotto (`7743eab`)

Sette doc falsi corretti (`DefaultCollation`, `TargetDependencies`,
`ColumnsDroppedOrAltered`, `PublishComparison`, i tre in `ScriptGenerator`), più
il banner di staleness: non afferma più che «le connessioni sono diverse», che è
falso nel caso più frequente — un refresh fallito **sugli stessi endpoint**,
perché `CompareAsync` azzera la coppia memorizzata prima di partire.
`MainWindowViewModelTests` non aggira più `PublishComparison`.

## Aperto — una cosa sola, ed è una scelta

Il gate non si azzera dopo l'esecuzione riuscita dell'app: lo stesso script
resta rieseguibile. È una scelta di prodotto (comoda o pericolosa a seconda del
flusso), non un difetto. Lasciata com'è: cambiarla senza sapere quale dei due
flussi il proprietario vuole sarebbe indovinare.

## Le lezioni di questa ondata

**Una guardia che vede solo le forme già scritte non guarda niente.** Il lint di
S11 cercava due pattern perché due erano i pattern trovati quel giorno.
`AppendLine("… [")`, `AppendFormat`, `string.Concat` e `"[" + x + "]"` gli
passavano davanti. Quando scrivi un lint, il test che conta non è «passa sul
repo pulito» ma «fallisce su ognuna delle forme che vieta», con la forma
iniettata davvero in un file scansionato.

**Un ordinamento nuovo è anche un modo nuovo di fallire.** S3 ha dato al DROP
pass un ordine vero, e con esso un `DependencyCycleException` su input che prima
venivano scriptati senza problemi. Il lato target di un server vivo può
contenere cicli che il lato sorgente non può: sono stati creati in un ordine che
il catalogo non mostra più. Un risolutore in più vuole sempre un fallback.

**«La sorgente è autoritativa» vale solo su ciò che l'utente ha spuntato.** Due
dei sette finding erano la stessa svista: passare la forma della sorgente a una
tabella che lo script per il resto non tocca. La selezione non è un filtro sulla
sola emissione DDL — decide di quali oggetti abbiamo il diritto di cambiare la
forma.

## Vale ancora

Tutto in `2026-07-30-handoff-criticals.md`: la sezione «Cosa NON fare», le
trappole di processo e l'appendice del 2026-07-31.

E la trappola pagata di nuovo qui: **un probe di mutazione che non compila fa
stampare a `dotnet test --no-build` il verde dell'assembly precedente.** Questa
volta è stato un `IDE0046` su un `if` che la mutazione aveva reso semplificabile.
Controlla `Errori: 0` prima di credere al risultato.

Nuova, minore: la suite completa in parallelo può far fallire in blocco i
progetti containerizzati per contesa su Docker subito dopo una serie di probe.
Rilancia il singolo progetto prima di dare la colpa a una modifica.
