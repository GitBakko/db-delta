# HANDOFF — verso la v1.0 finale: cosa resta, e cosa non deve essere rifatto

**Da leggere per primo in una sessione nuova.** Sostituisce
`2026-07-31-handoff-post-wave.md`, che resta valido come storia (la diagnosi dei
33 moduli e il confronto con Redgate vivono lì).

## Stato

- **HEAD `4b36cb4`** su `main`, **origin sincronizzato**, working tree pulito.
- **683 test verdi** su 10 progetti (Compat esclusa, gira solo di notte).
- **CI verde su tutti i job.** Prima volta da settimane — vedi la trappola Linux
  in fondo.
- **`v1.0.0-rc5` rilasciata** (2026-08-01, prerelease, MSI allegata):
  https://github.com/GitBakko/db-delta/releases/tag/v1.0.0-rc5
- **rc5 è installata su questa macchina** dalla sua MSI e funziona.
- 73 commit dall'rc4.

## Cosa resta per la v1.0 finale

Due voci, e sono entrambe del proprietario. Nessuna riga di codice le blocca.

### 1. Code signing — bloccato sul certificato

Serve un certificato Authenticode. Quando c'è, la firma va aggiunta in
`.github/workflows/release.yml` **fra «Build MSI» (riga 44) e «Smoke install /
uninstall» (riga 47)**: si firmano la MSI e gli `.exe` impacchettati
(`DbDelta.App.exe`, `cli/dbdelta.exe`) prima dello smoke, così lo smoke esercita
l'artefatto firmato e non un altro.

Il certificato va in un secret del repository, non nel repo. Con
`signtool` servono `/fd sha256` e un timestamp server (`/tr` + `/td sha256`) —
senza timestamp la firma scade con il certificato.

### 2. Annuncio pubblico

`README.md` con il link alla MSI dell'ultima Release e al sito DocFX
(https://gitbakko.github.io/db-delta/), note di rilascio, pubblicazione.

### Poi: taggare `v1.0.1`

**La prima release definitiva è `1.0.1`, non `1.0.0` — deciso dal proprietario
l'2026-08-08.** Motivo: rc1…rc5 hanno tutte `ProductVersion` numerica `1.0.0`,
e `MajorUpgrade` non scatta fra versioni identiche, quindi una `1.0.0` finale
avrebbe costretto ogni utente di una RC a disinstallare a mano. Con `1.0.1`
l'upgrade è automatico.

La pipeline pubblica un tag senza `-` come **non-prerelease «Latest»** da sola —
il flag viene da `contains(github.ref_name, '-')` — e ricava la `MsiVersion` dal
numero prima del primo `-`, quindi `v1.0.1` diventa `MsiVersion=1.0.1` senza
toccare niente.

**L'upgrade è stato verificato**, non dedotto: MSI `1.0.1` costruita in locale e
installata SOPRA la rc5 senza disinstallarla prima. Esito in
`scripts/SMOKE-RESULTS.md`. Le due cose che potevano rompersi e non si sono
rotte: una sola voce in Installazione applicazioni (non due), e la cartella
della CLI **non** duplicata né persa dal PATH di macchina — `MajorUpgrade`
programma `RemoveExistingProducts` dopo `InstallValidate`, quindi il vecchio
prodotto esce prima che il nuovo entri e la sua disinstallazione non porta via
la voce PATH appena scritta.

**La correzione ARP dell'installer (`4b36cb4`) è committata ma NON è nella MSI di
rc5.** Entra automaticamente nel prossimo tag: arriva con la v1.0.1, e la prova
di upgrade qui sopra l'ha già esercitata (`DisplayIcon` e `InstallLocation`
risultano popolate dopo l'aggiornamento).

## Rinviato per decisione del proprietario

**L'undo va a una versione successiva.** Deciso il 2026-08-01. Per memoria di
cosa comporta: l'atomicità c'è ed è corretta (se lo script fallisce non applica
niente), il buco è **dopo** un commit riuscito — non esiste down-script, backup
o journal. La review completa lo indicava come il vero buco di resilienza, e i
prerequisiti tecnici sono stati chiusi in questa ondata: `SideB` è l'oggetto
target catturato prima del deploy e `Generate` è puro, quindi invertire le coppie
è ~20 righe e zero emitter nuovi. Materiale in
`docs/review/2026-07-30-full-codebase-review.md`.

## Accettato consapevolmente, non da correggere

| Cosa | Perché resta |
|------|--------------|
| Compressione per-partizione non modellata | Un oggetto compresso a macchia di leopardo viene scriptato come la sua prima partizione. Deliberato |
| Divergenze cosmetiche da Redgate | `CREATE OR ALTER` (tenuto per idempotenza, il proprietario ha deselezionato l'allineamento), `[X_tmp]`, spaziatura `IDENTITY(1,1)`, `xp_logevent` finale |
| MSI da ~94 MB | Il runtime condiviso la dimezzerebbe. YAGNI |
| Timeout di deploy limitato a 10 minuti per batch | Illimitato richiede un Annulla vero nel dialogo (W3-5). La CLI ha già `--command-timeout 0`: una console ha Ctrl-C, il dialogo no |

## Le reti che non vanno rotte

Tre test valgono più di quanto sembri. Se una modifica futura li fa diventare
scomodi, il difetto è nella modifica.

1. **`DeployedModuleConvergesTests`** — deploya un modulo, rileggilo,
   confrontalo: **deve** risultare Identical. È l'invariante su cui poggia lo
   strumento, e per mesi non era asserita da nessuna parte. Quando aggiungi un
   emitter, aggiungi il suo caso qui.
2. **`AccentBandContrastTests`** — misura il rapporto di contrasto WCAG di ogni
   banda piena nei due temi. Include un controllo che le due varianti risolvano
   davvero a riempimenti diversi: senza quello una teoria asserisce Light due
   volte e non prova niente. Una banda nuova va aggiunta in `Bands()`.
3. **`CompressionRoundTripTests`** — emette, esegue su un server vero, rilegge.
   L'unico posto dove un'assunzione sul comportamento di SQL Server viene
   misurata invece che creduta.

## Trappole pagate, in ordine di quanto costano

- **Un `dotnet test --no-build` dopo una build fallita stampa il verde
  dell'assembly precedente.** Controlla `Errori: 0` prima di credere al
  risultato. È scattata tre volte oggi.
- **L'app tiene bloccati i DLL: va chiusa prima di ogni build.** È la causa più
  frequente della build fallita di cui sopra.
- **CI rossa solo su Linux? Sono i fine-riga.** `.editorconfig` impone CRLF, il
  repo salvava LF, e solo il checkout Windows riconvertiva: stesso commit,
  compila su Windows e `IDE0055` (che qui è errore) su Linux. Risolto con
  `.gitattributes` in `3dd1608` — se ricompare, guarda lì per primo.
- **Per un probe di mutazione usa mutazioni che COMPILANO.**
  `TreatWarningsAsErrors` trasforma un parametro inutilizzato o un `if (false)`
  in errore, e il probe non gira.
- **Differenze residue dopo un deploy riuscito: non partire dal codice nuovo.**
  Il 2026-08-01 il sospetto ovvio erano i flag `QUOTED_IDENTIFIER` scritti tre
  ore prima, ed erano innocenti. Una query li ha esclusi in un colpo — la trovi
  in `2026-07-31-handoff-post-wave.md`, sezione «Il diff che il proprio script
  non appiattiva». Poi confronta il testo emesso da due generazioni successive:
  se è identico, il difetto sta fra ciò che scriviamo e ciò che normalizziamo.
- **`CHECKSUM` mente sui corpi dei moduli** e SSMS tronca a 256 caratteri in
  Results to Text. Usa `=` con `COLLATE Latin1_General_BIN2`.
- **Screenshot dell'app:** `PrintWindow` con flag 2, non `CopyFromScreen`.
- **Installare la MSI richiede elevazione** e da qui parte un prompt UAC: uno
  script elevato che fa disinstalla+installa+verifica in un colpo evita di
  chiederlo tre volte (`D:\tmp\dbdelta-rc5\smoke.ps1` come modello).

## Dove guardare

- `docs/BACKLOG.md` — la lista formale; §A sono le due voci del proprietario.
- `docs/review/2026-07-31-handoff-post-wave.md` — i 33 moduli, il confronto con
  Redgate, i due bug del primo smoke.
- `scripts/SMOKE-RESULTS.md` — il log degli smoke live, ultimo in testa.
- `CHANGELOG.md` — la sezione `[1.0.0-rc5]` è il riassunto utente-visibile di
  tutta questa ondata.
