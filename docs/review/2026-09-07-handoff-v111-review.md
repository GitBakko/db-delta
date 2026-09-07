# HANDOFF — 2026-09-07: la review della 1.1.1, il riarmo P3 a metà commit, e la strada fino al tag

> **STORIA, non stato.** Questo file dice *perché* le cose sono state fatte così
> e *quali trappole sono state pagate*. **Ciò che è aperto sta SOLO in
> `docs/BACKLOG.md`**, l'unica lista di lavoro: qui non è duplicato, e se un
> giorno i due divergono ha ragione il backlog. Ogni riga di stato qui sotto
> invecchia il commit dopo: riverificala con `git status -sb`, `git log -1`,
> `CHANGELOG.md`.

**Da leggere per primo in una sessione nuova**, con `docs/BACKLOG.md` accanto.
Non sostituisce `2026-09-03-handoff-modal-sweep.md`: quello resta valido come
storia della segnalazione e dello sweep. Questo comincia dal 2026-09-05, il
giorno in cui lo sweep è stato chiuso e i suoi quattro commit sono stati
messi sotto review, e finisce con un commit **a metà**: scritto, verificato per
classe, non ancora passato dalla suite intera né dal gate di formato.

---

## Stato al momento della scrittura

- **`main` = `a8fb905` = `origin/main`**, CI verde (run `33956931040` e
  `34020537255` per `ci`, `33956931033` per `docs`).
- **Albero di lavoro SPORCO, e questo è il primo punto aperto** (§ «Da dove si
  riparte», passo 0). Sei file, tutti della P3 del riarmo:
  `CHANGELOG.md`, `docs/BACKLOG.md`,
  `src/DbDelta.App.Avalonia/ViewModels/ProjectEndpointPanelViewModel.cs`,
  `tests/DbDelta.App.HeadlessTests/ViewModels/EndpointCredentialResetTests.cs`,
  `tests/DbDelta.App.HeadlessTests/ViewModels/ModalLifetimeTests.cs`, più il
  nuovo `tests/DbDelta.App.HeadlessTests/ViewModels/PanelProbe.cs`.
  `git diff --stat`: 311 righe aggiunte, 88 tolte.
- **Ultima release pubblicata: `v1.1.0`** (2026-09-02). La prossima è
  `v1.1.1`, e il suo ambito è deciso: le tre P1, la P2 del «tieni premuto»,
  ciò che la review ha chiuso in `a8fb905`, **e la P3 del riarmo** — scelta
  del proprietario del 2026-09-05 («P3 deve entrare nella 1.1.1»).
- I conteggi di test nel blocco «Stato» del backlog (**1062** senza Docker,
  Headless **249**, **1203** con) sono **aritmetica sul commit sporco**, non
  una misura della suite intera: le due classi toccate danno 23/23, il totale
  va ricontato al passo 0.

## Cosa è successo il 2026-09-05

| Commit | Effetto |
|---|---|
| `e9abc18` | Chiusa la P2 «tieni premuto per mostrare»: era l'unica voce del backlog dichiarata non verificata, riprodotta con input headless vero e chiusa con un handler di `PointerCaptureLost` in `Direct`. Il lavoro era già su disco da una sessione interrotta; **verificato prima di crederci** — pre-fix il test cade, sonda `Direct→Tunnel` idem |
| `a8fb905` | La review adversariale dei quattro commit della 1.1.1 (`273be43..e9abc18`) e ciò che ha chiuso: cinque difetti, otto lacune di test, una riga del backlog che diceva il falso. Dettaglio completo nella sezione «Review adversariale della 1.1.1» del backlog |
| *(non committato)* | La P3 del riarmo, fatta entrare nella 1.1.1 dal proprietario. § sotto |

### La review, e la forma che ha funzionato

**7 agenti a conteggio fisso**: cinque lenti (regressione, sicurezza,
concorrenza, stringa di connessione, qualità dei test), **un solo
verificatore** che ha ricevuto tutti i 24 finding e li ha confutati in un
passaggio, un critico di completezza. 31 minuti, 1,16 M token di subagent.
La stessa review lanciata la mattina con tre scettici *per finding* era stata
uccisa a metà senza un verdetto — cinque agenti da 400 KB di transcript e zero
`StructuredOutput` — perché generava prompt di permesso a raffica. Regola già
in memoria («Agent budget»): il numero di agenti si conta *prima* di partire,
mai proporzionale ai dati.

Esito: 24 finding, 10 refutati, 14 in piedi più 3 del critico. Ogni
sopravvissuto **riverificato dal principale sul codice** prima di decidere.
Tutti i numeri e tutte le sonde sono nel backlog; qui solo ciò che il backlog
non dice.

### La P3 del riarmo — dov'è ferma

Il difetto: `OnAuthModeChanged` chiama `ScheduleAutoConnect()`, che cancellava
l'arm appena fatto dall'auto-fill e si rifiutava di rimpiazzarlo. Ogni
assegnazione in blocco (`LoadFromEndpoint`, `SwapEndpoints`, `CopyEndpoint`,
`CloneSourceToTarget`) imposta `AuthMode` subito dopo `ServerName`, quindi un
pannello in auth Windows che riceveva un endpoint SQL ricordato si riempiva e
non connetteva mai.

**Prima stesura**: un campo `_vouchedServer` — impostato dall'auto-fill dopo
aver scritto i due campi, azzerato da ogni modifica a utente, password o
server — e una clausola in più nella guardia di `ScheduleAutoConnect`.

**Poi un solo scettico** (workflow da un agente, `wf_675f531c-33e`) l'ha
attaccata e ha vinto su due punti, entrambi corretti prima del commit:

1. **Il predicato va chiesto anche allo sparo**, non solo all'arm: i setter di
   utente e password non cancellano un arm pendente, quindi una password
   ricordata che l'utente comincia a ridigitare entro i 450 ms sarebbe
   partita come prefisso — stesso server, non la P0 del 2026-08-18, ma contro
   la regola del 2026-09-03 «ciò che si digita aspetta Connetti». Il primo
   controllo in negativo che avevo scritto passava **solo perché** il cambio
   di `AuthMode` faceva da cancellatore.
2. **Il mio commento mentiva**: diceva che il confronto col nome vivo copriva
   uno store che risponde in ritardo, ma il `true` del parametro
   `credentialsAreKnownForThisServer` lo scavalcava. Irraggiungibile con
   `DpapiCredentialStore` (sincrono), raggiungibile il giorno che uno store
   cede il controllo (Keychain/Secret Service di una v2).

**Forma finale, quella su disco**: il parametro `credentialsAreKnownForThisServer`
è **cancellato**; un solo predicato `MayAutoConnect()` — Windows, oppure
`_vouchedServer` uguale al `ServerName` vivo — chiesto all'arm **e** allo
sparo in `AutoConnectAfterDelayAsync`; e dopo l'await dello store l'auto-fill
esce se il server è cambiato. Tutto commentato nel file con la data.

**Verificato su questo stato**:
- `EndpointCredentialResetTests` + `ModalLifetimeTests`: **23/23** verdi.
- **Sette sonde di mutazione**: cinque uccise, una non compilava (`IDE0051`,
  membro orfano — sostituita da un mutante equivalente), **una sopravvive di
  proposito** — tolta la guardia all'arm lasciando il solo controllo allo
  sparo, tutto resta verde: sono lo stesso predicato chiesto due volte, quella
  all'arm risparmia un timer. Il backlog lo dichiara nella riga della voce.
- Il primo tentativo delle sonde sulla stesura precedente è nell'output di
  `probes2.py`; quelle sulla forma finale in `probes3.py` (scratchpad di
  sessione, non nel repo — i risultati sono trascritti nel backlog).

**NON verificato su questo stato**: la suite Headless intera, la suite intera
con Docker, `dotnet format --verify-no-changes`. Sono i passi 0.2–0.4.

### `PanelProbe` — perché esiste

Su cinque giri della suite Headless intera, **uno** ha dato rosso:
`Loading_a_sql_project_into_a_windows_panel…` in **12 s**, per un test che
aspetta 900 ms; da solo passa sempre, e tre giri successivi sono puliti. Il
messaggio d'errore è andato perduto (il mio `grep` lo ha buttato: trappola,
vedi in fondo). Ipotesi di starvation del thread pool **refutata dalla
misura**: 32 `OpenAsync` verso `.invalid` in volo occupano 6 thread e un
`Task.Delay(900)` riparte a 901 ms (`D:\tmp\dbdelta-review-scratch\tests\dnsprobe`).
Ipotesi residua, non misurata: il `MaxConcurrencySyncContext` di xUnit sotto
carico ritarda la continuazione del test oltre i ~10 s in cui il tentativo
verso `.invalid` muore.

La correzione non dipende dalla causa: **ogni asserzione «è in volo» delle due
classi passa ora da `PanelProbe.LoadStarted(vm)`**, che registra su
`PropertyChanged` che un caricamento è *partito*, invece di campionare
`IsLoadingDatabases` a 900 ms. Vale in entrambe le direzioni: un positivo non
cade se la continuazione arriva tardi, un negativo non passa a torto se un
arm sbagliato spara e fallisce in fretta (la `PoolBlockingPeriod` di SqlClient
lo fa, 5 s dopo un fallimento sulla stessa pool — è il motivo per cui ogni
test in volo ha ora una password propria).

---

## Da dove si riparte

Ordine pensato. Il passo 0 viene comunque prima di tutto.

**0. Chiudere il commit della P3.** Sull'albero sporco, in quest'ordine:

1. `dotnet build DbDelta.sln -c Debug` — deve dare `Errori: 0`.
2. `dotnet test tests/DbDelta.App.HeadlessTests --no-build` **almeno due
   volte** (è la classe con il rosso raro) e `dotnet test DbDelta.sln
   --no-build` con Docker acceso (`docker version` deve mostrare `Server:`).
   Attesi: Headless 249, totale 1062 senza Docker, 1203 con. **Se i numeri
   non tornano, ha ragione la misura: correggi il blocco «Stato» del backlog**,
   non il contrario.
3. `dotnet format DbDelta.sln --verify-no-changes > /dev/null; echo $?` — il
   gate CI. Leggere `$?` nudo, **non** in coda a una pipe (trappola pagata:
   `| tail` restituisce l'exit di `tail`). `IDE0007`, `IDE0017`, `IDE0051`
   sono **errori**.
4. `wc -l src/DbDelta.App.Avalonia/ViewModels/ProjectEndpointPanelViewModel.cs`
   deve dare **865**, che è ciò che la voce P4 del backlog dichiara; se il
   passo 3 ha mosso righe, aggiornare la voce.
5. Commit di tutti e sei i file insieme (regola: la voce si chiude nello
   stesso commit del codice). Messaggio proposto:
   `fix(app): a remembered pair still connects after a switch to SQL auth, and only that pair`
   — nel corpo: il parametro cancellato, il predicato chiesto due volte, il
   bail-out dopo l'await, `PanelProbe`, le sette sonde. Poi `git push` e
   `gh run list --limit 3` fino a `completed success` su entrambi i job.

**1. L'unica voce nuova aperta dalla review** è la P4 «`Closed` non distingue
OK da Annulla»: una scrittura «Ricorda credenziali» dietro un caricamento
ancora in volo al momento di OK viene scartata. XS, corsa stretta, forse un
compromesso accettabile — ma **non è dichiarato come tale**, ed è questo il
punto aperto: o si chiude con un `TryPersistCredentialsAsync` esplicito in
`OnOkClick`, o si dichiara la scelta nella riga della P1 sul ciclo di vita.
Del proprietario.

**2. Il resto delle 8 voci aperte** non è cambiato: due P2 (testo d'errore
troncato; «Carica» che fallisce con le parole del salvataggio), la P3 dei
pannelli copia-incolla, tre P4 (scansione che «trova» sempre tre server;
«(N trovati)» che conta i separatori; la crescita di
`ProjectEndpointPanelViewModel`, ora 865 righe, la cui estrazione va fatta
**insieme** alla P3 dei pannelli), la P5 del proprietario sulla selezione
oggetti da CLI. Nessuna è nella 1.1.1.

**3. Cose che lo scettico ha visto e che NON sono voci**, perché
pre-esistenti o irraggiungibili — registrate qui perché nessuno le riscopra:
- `FromEndpoint` costruisce pannelli usa-e-getta con lo store vero
  (`ProjectSetupViewModel.FromProject`): ognuno fa auto-fill e arma un
  auto-connect che nessuno cancella — un login nascosto per endpoint, verso il
  server del progetto con la sua stessa coppia. Non è disclosure; sotto una
  policy di lockout è un tentativo contato. Pre-esistente da prima di
  `cb01e7a`.
- Con uno store asincrono, la soppressione `_autoFillFromCredentialsInFlight`
  fa perdere l'auto-fill del nome vivo quando la risposta arriva per il nome
  vecchio: dopo A→B dentro la finestra, la coppia di B non viene mai messa.
  Funzionale, non sicurezza, e irraggiungibile col DPAPI sincrono.
- Un `.invalid` risolto da un wildcard DNS aziendale cambierebbe l'esito dei
  test positivi. Speculativo; nessun ambiente noto lo fa.

**4. Smoke dal vivo PRIMA del tag — del proprietario.** La lista di 11 gesti
sta nel backlog, sezione «Review adversariale della 1.1.1», e il punto 6 è
già aggiornato all'esito atteso con la P3 chiusa. I tre che nessun test
headless può vedere: il 5 (A poi B rapidi, `cmdkey` invariato), l'8 (Alt+Tab
con l'occhio premuto; penna e touch sono la parte **non letta** nel sorgente
di Avalonia), il 9 («Carica…» di un progetto SQL dopo aver digitato una
password per un altro server → OK spento).

**5. Tagliare la release.** Come per la 1.1.0, e il critico l'ha ricordato:
`CHANGELOG.md` porta ancora `## [Unreleased]` sopra le nove voci della 1.1.1
(ne sono state aggiunte cinque il 2026-09-05), e ogni release precedente ha
avuto il suo commit di taglio (`## [1.1.1] — data`) prima del tag;
`docfx/articles/version-history.md` viene rigenerato da lì. Poi
`git tag -a v1.1.1 -m "…"` e `git push origin v1.1.1`: la versione viene dal
tag, non c'è nessun file da bumpare. Dopo la pubblicazione: `README.md` e il
blocco «Stato» del backlog nominano la versione corrente, **nello stesso
commit**, e il conteggio dei test va **ricontato**.

---

## Trappole pagate il 2026-09-05, oltre a quelle già in memoria

- **`--no-build` mente anche compilando il progetto sbagliato.** `dotnet build
  src/DbDelta.App.Avalonia` non copia la DLL nel `bin` del progetto di test:
  due sonde sono passate «verdi» misurando il fix ancora in memoria. Compila
  il progetto di **test**, leggi l'mtime di `DbDelta.App.dll` nel suo `bin`.
  (In memoria.)
- **`dotnet format … | tail; echo $?` stampa l'exit di `tail`.** Un `IDE0007`
  è passato inosservato una volta. (In memoria.)
- **`grep -E "Superato|\[FAIL\]"` sull'output di `dotnet test` butta il
  messaggio d'errore.** Il rosso raro di `Loading_a_sql_project…` è rimasto
  senza diagnosi per questo. Cattura `-A30` dopo `[FAIL]`, sempre.
- **Un mutante che orfana un membro privato non compila** (`IDE0051`): la
  sonda «togli la clausola dalla guardia» va scritta togliendo *chi imposta*
  la condizione, non chi la legge. Terza volta che il repo lo paga.
- **Uno scettico solo, sul diff, prima del commit, paga.** Ha trovato in sei
  minuti due difetti della prima stesura che sette sonde verdi non vedevano,
  perché le sonde provano il test e lo scettico prova la *proprietà*.
