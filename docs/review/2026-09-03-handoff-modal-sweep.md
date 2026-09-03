# HANDOFF — 2026-09-03: la modale inservibile, lo sweep fermato a metà, e la strada fino alla 1.1.1

> **STORIA, non stato.** Questo file dice *perché* le cose sono state fatte così
> e *quali trappole sono state pagate*. **Ciò che è aperto sta SOLO in
> `docs/BACKLOG.md`**, l'unica lista di lavoro: qui non è duplicato, e se un
> giorno i due divergono ha ragione il backlog. Ogni riga di stato qui sotto
> invecchia il commit dopo: riverificala con `git status -sb`, `git log -1`,
> `CHANGELOG.md`.

**Da leggere per primo in una sessione nuova**, con `docs/BACKLOG.md` accanto.
Non sostituisce `2026-09-02-handoff-post-review.md`: quello resta valido come
storia della 1.1.0. Questo comincia il giorno dopo, da una segnalazione che
nessun test aveva visto.

---

## Stato al momento della scrittura

- **`main` = `f3f6e29`, `origin/main` = `09368f6`**: un commit **avanti, NON
  pushato**. È la prima cosa da fare (§ «Da dove si riparte», passo 0).
- **1024 test verdi** senza Docker (Core 630, Headless **214**, Persistence.Unit
  88, Golden 68, Property 12, Architecture 6, Shared 6) — ricontati, non
  incrementati a mente. `dotnet format DbDelta.sln --verify-no-changes` esce 0.
  I DB-backed non sono stati girati in questa sessione: Docker era spento e
  nessuna modifica tocca il motore.
- **10 voci aperte** — P1 3 · P2 3 · P3 1 · P4 2 · P5 1. `awk` sulle righe delle
  quattro tabelle «Voce | Reg. | Sforzo | Stato reale»: 3+3+1+2+1 = 10.
  **Nove su dieci le ha aperte questa segnalazione**; la decima è quella del
  proprietario sulla selezione oggetti da CLI, ferma dal 2026-09-02.
- **Ultima release pubblicata: `v1.1.0`** (2026-09-02). La prossima è una
  **PATCH**, `v1.1.1`: le due correzioni non aggiungono superficie.

## Cosa è successo

Il proprietario installa l'MSI della 1.1.0, apre «Nuovo progetto» per
confrontare due DB su `172.31.188.36\SQLSTERI` e **non trova il campo
password**; cliccando a caso compare, in grigio in fondo alla modale, un errore
di connessione che non aveva chiesto. Due difetti distinti, entrambi già nella
1.0.2, entrambi invisibili ai 1020 test allora verdi.

| Commit | Effetto |
|---|---|
| `f3f6e29` | Le due chiuse: `StyleKeyOverride` su `MaskedTextBox`, e l'auto-connect che non parte più da credenziali in digitazione. Backlog e CHANGELOG nello stesso commit |

Il dettaglio tecnico completo — meccanismo, prove, sonde di mutazione — sta
nella sezione «Segnalazione dal build installato — 2026-09-03, v1.1.0» del
backlog. Qui solo ciò che il backlog non dice.

### La lezione, ed è la sola cosa da portarsi dietro

**La sonda che conferma va girata prima su un caso noto-sano.** La prima sonda
headless ha detto esattamente ciò che speravo — `Template == null` sul campo
password — e sembrava la prova. Il controllo, la **stessa** sonda su un `TextBox`
normale, ha detto `null` anche lui: un controllo staccato da una radice visuale
non viene mai templato, quindi la sonda misurava l'**harness**, non il bug. Con
`Window { Content = c }.Show()` la sonda diventa valida e il controllo passa.

Senza quel controllo sarebbe finita in `src/` una diagnosi giusta per una
ragione falsa, con in mano un test verde per il motivo sbagliato. È la stessa
famiglia di «La correzione è a sua volta un'affermazione» nella memoria di sessione, presa dal verso della
sonda invece che da quello della causa.

**Il corollario sui test UI headless:** costruire il controllo e misurarlo non
basta quasi mai. `PasswordBoxTests` esisteva dal 2026-08-20, raggiungeva
`PART_PasswordBox` **per nome**, e un controllo senza template è ancora
nell'albero e risponde ancora a `FindControl` — binding, masking e peer di
automazione passavano tutti su un controllo che nessuno poteva vedere. Il guard
durevole che ne è uscito (`UiInvariantTests.Every_control_the_user_can_see_actually_has_a_template`)
percorre l'albero visuale di **tutti e nove** i dialog: sotto mutazione stampa
`MaskedTextBox` **e `ConnectionEditDialog`**, ed è così che si è saputo che i
dialog colpiti erano due e non uno.

---

## Lo sweep: dov'è fermo, e cosa NON si può riprendere

Uno sweep adversariale su cinque lenti (style-key, selettori, completabilità
della modale, superficie d'errore, lavoro non richiesto) ha sollevato **26
finding**, ne ha rifiutati **15** alla verifica, e ne ha lasciati **11 in
piedi** — due dei quali descrivono lo stesso difetto, chiuso in `f3f6e29`.
Restano quindi **9 voci**, tutte già scritte nel backlog con `file:riga`
verificati il 2026-09-03.

**È stato fermato di proposito**, con un verdetto ancora in volo: i suoi agenti
generavano prompt di permesso a raffica e il proprietario era bloccato. Vedi «Prompt di permesso»
nella memoria di sessione.

### Cosa resta sul disco

| Artefatto | Percorso |
|---|---|
| Journal (i verdetti già scritti) | `~/.claude/projects/D--Develop-AI--ClaudeCode-SQL-Compare/02627a46-03d3-43c4-953d-94ea28cb868d/subagents/workflows/wf_75258601-436/journal.jsonl` |
| Transcript per agente | stessa cartella, `agent-*.jsonl` |
| Script del workflow | `…/02627a46-…/workflows/scripts/dbdelta-setup-modal-sweep-wf_75258601-436.js` |

### La trappola, e va detta chiaro

**`resumeFromRunId` funziona solo nella STESSA sessione.** Una sessione nuova
non riprende `wf_75258601-436`: il run id non risolve. Non provarci e non
aspettare che risolva.

E non serve: **l'output dello sweep è già interamente nel backlog.** L'unica
cosa che il backlog NON ha è il verdetto sulla voce rimasta senza — ed è
dichiarata tale nella sua riga, in P2, con la parola `NON VERIFICATA` in
testa. Le altre otto sono verdetti completi.

### L'unica voce da verificare a mano

**«Tieni premuto per mostrare la password» potrebbe non rimascherare mai** se la
pressione finisce senza un `PointerReleased` — Alt+Tab, una notifica che ruba
l'attivazione, un contatto touch/penna annullato. `Views/Controls/PasswordBox.axaml.cs:41`
è l'unico handler che rimaschera; non c'è alcun handler di `PointerCaptureLost`
nel file.

**Prima riga di lavoro: riprodurre, non correggere.** Il meccanismo è
plausibile e le righe citate sono reali, ma nessuno ha misurato che Avalonia si
comporti così. La sonda ha la stessa forma di quella che ha chiuso il difetto
principale — e ha lo stesso bisogno di un controllo:

1. `Window { Content = new PasswordBox() }.Show()` (staccato non basta, § lezione).
2. Premere sul `PART_RevealButton`, poi far **perdere la cattura** invece di
   rilasciare. In headless: `PointerCaptureLost` alzato sul bottone, oppure
   `e.Pointer.Capture(null)`.
3. Asserire `PasswordChar` sul `PART_PasswordBox`: se resta `'\0'`, la voce è
   vera e il rimedio è un handler di `PointerCaptureLost` che rimaschera.
4. **Controllo in negativo obbligatorio**: lo stesso giro con un
   `PointerReleased` normale deve rimascherare. Senza, non sai se hai misurato
   la perdita di cattura o il fatto che la sonda non preme davvero.

Se non riproduce, **la voce si chiude come non-difetto e lo si scrive** — con
la misura, non con «non sono riuscito».

---

## Da dove si riparte

Ordine pensato, non obbligatorio. I passi 0 e 1 vengono comunque prima.

**0. Pushare `f3f6e29` e guardare la CI.**
`git push` e poi `gh run list --limit 3`. Il gate duro è
`dotnet format --verify-no-changes` nel job windows-build: usciva 0 al momento
del commit, ma è il primo posto dove guardare se va rosso. **Trappola pagata
in questa sessione**: Python lanciato dal tool Bash scrive **LF**, il build e i
test non se ne accorgono e `dotnet format` sputa un `error ENDOFLINE` per riga.
Vedi «Il tool Bash è Git Bash», memoria di sessione.

**1. Verificare la voce `NON VERIFICATA`** (§ sopra). È l'unica affermazione
del backlog senza prova, e va o promossa o chiusa prima di dire che la
segnalazione è esaurita.

**2. Decidere l'ambito della 1.1.1 — è una scelta del proprietario.**
Due strade oneste, e la differenza è settimane contro ore:

- **Minima**: si rilascia subito ciò che è in `f3f6e29`. La modale torna
  usabile, che è il difetto bloccante segnalato. Le altre 9 voci restano
  aperte e dichiarate.
- **Larga**: si chiudono prima le **tre P1**, che sono le uniche altre a
  produrre un risultato sbagliato e non solo attrito.

La raccomandazione di chi scrive è la **minima**: il proprietario è fermo su una
build inservibile, e le tre P1 non sono regressioni — sono nella 1.0.2 da
sempre.

**3. Se si va sulla larga, l'ordine ha un vincolo vero.**
La voce **P3 (i due pannelli copia-incolla)** non è cosmetica in questo
contesto: **ogni** correzione di markup fra quelle aperte va applicata due
volte finché resta. Se si toccano due o più voci che passano da
`ProjectSetupDialog.axaml`, l'estrazione del `UserControl` viene **prima**.
Trappola già misurata e scritta nella riga: `SrcServerPicker` **non** è un nome
morto — `ServerPickerTests.cs:33` lo risolve per nome sul dialogo, quindi
un'estrazione ingenua ne cambia il namescope e rompe quel test.

Le tre P1, in ordine di rischio decrescente:
1. `SqlConnectionStringBuilder` al posto della concatenazione — è anche
   «Validate input at system boundaries» di `CLAUDE.md`, e il danno vero è che
   OK si chiude su una stringa rotta che finisce in `AppState`.
2. Il ciclo di vita della modale: `Closing` che cancella, e i
   `CancellationToken.None` che vanno filati. Tocca anche la scrittura in
   Credential Manager dopo «Annulla».
3. L'azzeramento delle credenziali a ogni tasto del nome server. **Attenzione**:
   quel comportamento è deliberato e chiude una P0 del 2026-08-18 —
   `EndpointCredentialResetTests` lo pinna con tre test. Cambiarlo significa
   riscrivere quei test, e va scritto **perché** il nuovo assetto regge lo
   stesso: `IsAutoConnectEligible` rilegge comunque i campi allo sparo.

**4. Smoke dal vivo PRIMA della release, non dopo.**
«Smoke prima, non dopo» (memoria di sessione) vale qui alla lettera, e questa segnalazione
ne è la prova più cara: **il difetto è arrivato dall'utente, non dalla suite.**
Lo smoke da fare non è quello del motore — è quello della **modale**, che nessun
test headless copre come la vede un umano:

1. Installare l'MSI prodotta dalla build (non `dotnet run`: il difetto era
   visibile in entrambi, ma il canale segnalato è l'installer).
2. «Nuovo progetto» → il campo password **si vede** e ha la stessa altezza degli
   altri controlli (32 px, regola UI #2).
3. Digitare la password **con una pausa deliberata di 2 s a metà**: **nessun**
   messaggio d'errore deve comparire prima di «Connetti».
4. Server reale della segnalazione: `172.31.188.36\SQLSTERI`. Le credenziali
   **non si scrivono da nessuna parte** — «Live SQL endpoints», memoria di sessione.
5. Stessa passata su «Modifica connessione», che è il secondo dialog colpito.

**5. Tagliare la release.**
Non c'è nessun file di versione da bumpare: **la versione viene dal tag**
(`.github/workflows/release.yml:35`, `github.ref_name`). Quindi:

```bash
git push                          # se non già fatto al passo 0
git tag -a v1.1.1 -m "…"
git push origin v1.1.1
```

Il workflow parte sul tag (`on: push: tags: ['v*']`) e ha **9 step nominati**
nel file — la run su GitHub ne elenca di più perché conta anche checkout e gli
step impliciti; il backlog della 1.1.0 dice «undici», ed è quel conteggio lì.
Lo step che conta davvero è **`Smoke install / uninstall`** (`:51`): installa
per davvero, verifica app, CLI e PATH di macchina, disinstalla, e verifica che
sia sparito. `SHA256` e `Attest build provenance` stanno **dopo** di lui, ed è
voluto: firmare riscrive il file.

MSI non firmata, come la 1.1.0. `prerelease` è calcolato su un trattino nel
nome del tag, quindi `v1.1.1` esce come release piena.

**6. Dopo la pubblicazione, le righe che la release fa marcire.**
Stessa manutenzione della 1.1.0, ed è già stata dimenticata una volta:
`README.md` e il blocco «Stato» di `docs/BACKLOG.md` nominano la versione
corrente. Vanno riportati a `v1.1.1` **nello stesso commit**, e il conteggio
dei test va **ricontato**, non incrementato a mente.

---

## Trappole pagate in questa sessione, oltre a quella della sonda

- **`dotnet format` e le terminazioni di riga** — § passo 0. Il gate è duro in
  CI e non lo vedi finché non lo lanci.
- **Prompt di permesso a raffica.** Auto mode fa passare ogni lettura e ogni
  modifica da Bash, e ogni stringa di comando nuova è un permesso a sé. Il
  proprietario ha messo `Bash` in allow il 2026-09-03
  (`.claude/settings.local.json`, git-ignored). Indipendentemente da quello:
  **usa Read/Edit/Grep per i file di progetto**, Bash per `dotnet`, `git` e le
  sonde. Dettagli e rimedi in «Prompt di permesso», memoria di sessione.
- **Un workflow fermato non si riprende da un'altra sessione** — § sweep.
  Leggi `journal.jsonl`, non sperare nel resume.
- **I `file:riga` marciscono nello stesso commit che li scrive.** Le citazioni
  di questa sessione sono state scritte a memoria e **poi** verificate una per
  una con `grep -n`: **diciassette correzioni su una trentina di citazioni**, quasi tutte di 1-3
  righe, perché il commento appena aggiunto sopra aveva spostato tutto il resto
  del file. Verificale
  **dopo** l'ultima modifica al file, mai prima.
