# HANDOFF — il primo smoke live, e i due bug di deploy che ha trovato

**Da leggere per primo in una sessione nuova.**

- **HEAD:** `f87b98a` su `main`, **origin sincronizzato**, working tree pulito.
- **Test:** 632 verdi su 10 progetti (Compat esclusa, gira solo di notte).
- **Gate formato:** `dotnet format DbDelta.sln --verify-no-changes` esce 0. Build senza avvisi.

## Da dove ripartire

**Il dialogo di backfill.** Il motore è già scritto e testato
(`BackfillPreflight` + il parametro `backfillDefaults` di
`ScriptGenerator.Generate`); manca solo la UI che raccoglie i valori. È il pezzo
che sblocca lo smoke: finché non c'è, `Corrieri_TipiDocumentazioni` continua a
fallire — correttamente, ma continua.

Comportamento deciso dal proprietario: **avvisare, chiedere un default tecnico,
applicarlo.** Quindi prima di generare o eseguire, se `BackfillPreflight.Scan`
restituisce qualcosa, mostrare un dialogo con una riga per colonna (tabella,
colonna, tipo, valore proposto da `SuggestedValue`, editabile) e passare la mappa
a `Generate`. Va agganciato a **entrambi** i percorsi: «Genera script» e
«Allinea destinazione».

## La cosa importante di oggi

Il post-wave è stato chiuso (sedici finding, vedi in fondo), ma **il valore vero
è arrivato dal primo smoke live** contro `192.168.3.243`, che ha trovato due bug
che nessun test aveva mai visto — **entrambi rompevano ogni deploy di una forma
che nel mondo reale è ordinaria.**

### 1. Msg 207 — un vincolo su una colonna nuova non compila (`93e95b1`)

```sql
ALTER TABLE [dbo].[Corrieri_Regole] ADD [CondizioneEndpoint] nvarchar(8) NULL;
ALTER TABLE [dbo].[Corrieri_Regole] ADD CONSTRAINT [CK_...] CHECK ([CondizioneEndpoint] ...);
```

SQL Server **compila un batch per intero prima di eseguirne una riga**, quindi il
CHECK non risolve una colonna che a compile time non esiste ancora. Con
`XACT_ABORT` si porta via tutto il deploy.

**Colpiva ogni deploy che aggiunge una colonna più un vincolo su di essa.** Non
un caso limite.

`EmitAlter` emette un separatore di batch prima della sezione vincoli, **solo**
se una colonna è stata davvero aggiunta. `DeploymentScriptWriter` spezza il body
sui suoi `GO` e mette il gate su ogni pezzo, così nessun emitter può produrre un
batch non protetto.

### 2. Msg 4901 — colonna NOT NULL senza default su tabella popolata (`e87bab6`)

Indeployabile per costruzione: non c'è un valore da mettere nelle righe che già
esistono. **Verificato che nemmeno Redgate ce la fa** — il suo rebuild crea la
tabella con le colonne `NOT NULL` ma **le lascia fuori dalla lista dell'INSERT**,
quindi il suo script muore sugli stessi dati, più tardi e con un `DROP TABLE` già
in coda. Su questo non eravamo indietro.

`BackfillPreflight.Scan` le trova prima che parta una riga di SQL. Il generatore
riprende i valori come mappa `(schema, tabella, colonna)`: ognuno entra su un
vincolo usa-e-getta nominato che l'istruzione successiva droppa, così le righe
prendono il valore e la colonna resta come la dichiara la sorgente. Senza un
valore fornito la colonna esce invariata e fallisce ancora: **nulla viene
inventato per conto dell'utente.**

**Manca il dialogo che raccoglie i valori.** È il prossimo pezzo, e finché non
c'è lo smoke si ferma su `Corrieri_TipiDocumentazioni`.

## La lezione

**Nessun test di questo repo poteva trovare quei due bug.** 631 test verdi, 68
golden, un gate ScriptDom che verifica che ogni golden *parsi* — e lo script
moriva alla prima tabella reale. I golden confrontano testo; ScriptDom valida la
sintassi; nessuno dei due sa che SQL Server compila un batch intero prima di
eseguirlo, o che una tabella ha righe dentro.

Il primo smoke live ha trovato in due esecuzioni più difetti di deploy di quanti
ne avesse trovati l'intera suite. **Non rimandare più lo smoke a dopo il
refactor.**

Corollario, dallo stesso pomeriggio: tre bug UI di fila (checkbox a larghezza
zero, controllo vivo in `Header` invece che in `HeaderTemplate`, header più basso
del glifo) di cui **il renderer headless ne riproduce uno solo**. Dove il test
non può mordere, il commento nel test lo dice — vedi
`ResultsGridSelectionTests`.

## Aperto, in ordine di gravità

| Cosa | Stato |
|------|-------|
| Dialogo di backfill (Msg 4901) | Motore pronto e testato, manca la UI |
| Gate: su errore niente cambia, su successo ricompare automatico, più indicatore di stato ultima run con popup degli errori | Spec del proprietario accettata, non iniziato. Richiede prima di agganciare `SqlConnection.InfoMessage` e catturare `SqlException.Errors`: oggi teniamo solo `ex.Message`, per questo la app mostra 2 errori dove SSMS ne mostra cento |
| `DATA_COMPRESSION` (tabella e indice) non è nel modello | Gap confermato su `WebhookDeliveries` |
| `uses_ansi_nulls` / `uses_quoted_identifier` per-modulo non letti | Gap confermato su `SpClonaApplicationMenu`: corpo byte-identico, `QuotedId` 1 vs 0 |
| Il gate non si azzera dopo un'esecuzione riuscita | Assorbito dal punto "Gate" sopra |

## Confronto con Redgate — dove siamo

Su 166 create e ~110 modifiche i due strumenti concordano su tutto tranne sei
oggetti, **e mai nella direzione opposta**: DbDelta non segnala mai qualcosa che
Redgate consideri identico.

Dei sei, **quattro sono casi in cui abbiamo ragione noi**: due erano artefatti
della mia classificazione (una tabella che entrambi droppano, contata fra i
modificati); uno è una FK droppata e ri-aggiunta identica come collaterale del
rebuild della tabella referenziata, quindi l'oggetto non cambia; e due sono le
viste `VwAi*`, che per un pomeriggio hanno avuto l'aria del falso negativo più
grave della giornata — **e non lo erano**. Messi i corpi affiancati, sono la
stessa istruzione, una indentata e l'altra appiattita su una riga. Fissato in
`ModuleDiffTests.A_view_reformatted_but_not_changed_is_identical`.

Restano due gap reali, entrambi di modello, entrambi elencati sopra:
`DATA_COMPRESSION` e le opzioni SET per-modulo.

**Nota metodologica, perché mi ha fatto perdere tempo:** `CHECKSUM` sui testi
ripuliti dava "diversi" su corpi che erano identici, e SSMS tronca a 256
caratteri in Results to Text. Per confrontare due definizioni usa `=` con
`COLLATE Latin1_General_BIN2`, e leggile con `CAST(definition AS xml)`.

Fixture e procedura in `tests/Fixtures/Parity/README.md`.

## Vale ancora

Tutto in `2026-07-30-handoff-criticals.md`: la sezione «Cosa NON fare» e le
trappole di processo. Più queste, pagate oggi:

- **Un probe di mutazione che non compila fa stampare a `dotnet test --no-build`
  il verde dell'assembly precedente.** Controlla `Errori: 0` prima di credere al
  risultato. `TreatWarningsAsErrors` è attivo, quindi un `if (false)` diventa
  CS0162 e la mutazione non gira: usa mutazioni che compilano.
- La suite completa in parallelo può far fallire in blocco i progetti
  containerizzati per contesa su Docker. Rilancia il singolo progetto prima di
  dare la colpa a una modifica.
- L'app tiene bloccati i DLL: va chiusa prima di ogni build.
- Per uno screenshot dell'app usa `PrintWindow` con flag 2, non
  `CopyFromScreen`: da un processo in background `SetForegroundWindow` non ha
  effetto e catturi la finestra sbagliata.

## Storico — i sedici finding del post-wave, chiusi in `21bf477..1ed46d8`

Sette di correttezza (forma FK su tabella non selezionata, `ALTER TABLE` su
tabella mai creata, ciclo negli archi target, accoppiamento ordinale di indici e
FK, passthrough di `SqlTypeFormatter`, `]]` in `BracketedNames`, exit code della
CLI), due di guardie (lint di quoting allargato da 2 a 6 forme più
`DbDelta.Providers.LiveDb`; test morto cancellato), sette commenti falsi, più la
riformulazione del banner di staleness. Ognuno con probe di mutazione eseguito.
