# PostImages Downloader

Applicazione console scritta in **VB.NET 8** per scaricare immagini dalle gallerie pubbliche o anche un intero account (purchè pubbliche) di <span style="color:blue">Postimages</span> e <span style="color:blue">ImgBB</span>.

## Perché?

Postimages offre un servizio gratuito di hosting delle immagini, ma alcune funzionalità sono riservate agli account Premium.

In particolare, quei ferengi 💸 pretendono di essere pagati per poter scaricare un' intera galleria.

Non voglio pagare un abbonamento premium solo per recuperare in blocco immagini che **ho caricato io stesso** 🤬.

Considero **sbagliato** pagare per ottenere qualcosa che è già mio, in altre parole il takeout.

---

Questo programma esegue quindi localmente il lavoro necessario:

* accetta l' URL di una galleria Postimages/ImgBB;
* scarica tutte le immagini della galleria;
* accetta l' URL del profilo di un utente;
* individua le gallerie pubbliche presenti nel profilo;
* scarica le immagini di ogni galleria;
* determina il formato effettivo delle immagini analizzandone i dati;
* conserva, quando possibile, il nome originale delle immagini;
* crea una cartella separata per ogni galleria.

Il programma **non carica, modifica o elimina** alcun contenuto sui servizi supportati.

ATTENZIONE: in caso di timeout non tutte le immagini vengono scaricate.
Se un' immagine è già presente non cerca di riscaricarla. Quindi se desideri riscaricare tutta la galleria, rinomina la sua cartella. Il controllo avviene sulle immagini che la galleria dichiara di avere, non solo nome file, quindi non ci saranno problemi in caso di immagini con lo stesso nome, nel caso di stesso nome ed estensione la seconda verrà rinominata.
Tuttavia se il programma viene interrotto con Ctrl+C lo stato di "scaricato" per certe immagini non viene aggiornato, quindi verranno riscaricate, sovrascrivendo il precedente download.

## Requisiti

* Windows o un'altra piattaforma supportata da .NET 8;
* .NET 8 SDK se vuoi compilare il progetto in locale.

Non sono richiesti pacchetti NuGet esterni.

## Utilizzo

Funziona di linea di comando, non ha interfaccia grafica e se lanci l' exe da solo la finestra si chiude immediatamente.

### Scaricare una singola galleria

```
PostImagesDownloader.exe "https://postimg.cc/gallery/ID_GALLERIA"
```

Ad esempio:

```
PostImagesDownloader.exe "https://postimg.cc/gallery/4dw5KCx"
```

Il programma crea una cartella con il nome della galleria e salva al suo interno tutte le immagini.

### Scaricare tutte le gallerie pubbliche di un utente
```
PostImagesDownloader.exe "https://postimg.cc/user/NOME_UTENTE"
```

Ad esempio:

```
PostImagesDownloader.exe "https://postimg.cc/user/federico-boccacc"
```

Anche senza virgolette

Il programma analizza la pagina pubblica dell' utente, individua i collegamenti alle gallerie (`/gallery/`) e le elabora una alla volta.

### ImgBB
```
PostImagesDownloader.exe https://ibb.co/album/ID_ALBUM
PostImagesDownloader.exe https://NOMEUTENTE.imgbb.com/albums
```

## Formati delle immagini

L' estensione del file **non viene assunta in base ai dati della galleria**.

Dopo aver scaricato un' immagine, il programma analizza la sua intestazione (magic bytes) e determina il formato effettivo del file.

Sono attualmente riconosciuti:

* JPEG
* PNG
* GIF
* BMP
* TIFF
* WebP
* AVIF
* HEIC
* SVG

Che credo sia più dei formati supportati da PostImages (ma il lavoro lo ha fatto ChatGPT... Copilot si è rifiutato).

Se il formato non può essere riconosciuto, il file viene salvato con estensione `.bin` invece di sperare che sia un JPEG.

## Download immediato

Le immagini vengono scaricate e salvate **una alla volta**.

```text
trova immagine
     ↓
recupera URL originale
     ↓
scarica
     ↓
determina formato
     ↓
salva sul disco
     ↓
passa all'immagine successiva
```

Questo permette inoltre di conservare sul disco le immagini già completate anche nel caso in cui il programma venga interrotto durante il download.

## Compilazione tramite GitHub Actions

Puoi compilare direttamente su online: GitHub compila il programma e produce uno zip pronto all' uso.

Il workflow `.github/workflows/build.yml`:

1. scarica il repository;
2. installa il .NET 8 SDK;
3. compila e pubblica il programma per Windows x64, self-contained, in un unico `.exe`;
4. crea lo zip `PostImagesDownloader <tag>.zip` con l'eseguibile, il `README.md` e la cartella `doc`;
5. carica lo zip come artefatto della esecuzione.

Il `<tag>` nel nome dello zip è il tag Git su cui gira il workflow oppure, se manca, l'hash breve del commit.

### Avviare la compilazione

Ci sono due modi, in base ai permessi:

**Hai accesso in scrittura al repository** (proprietario o collaboratore):

1. aprire il repository su GitHub;
2. selezionare **Actions**;
3. selezionare il workflow **Build** nella colonna a sinistra;
4. premere **Run workflow**, indicare facoltativamente un tag da inserire nel nome dello zip e confermare.

Scaricare il risultato

1. aprire la scheda **Actions**;
2. selezionare l' ultima esecuzione completata con successo del workflow **Build**;
3. nella sezione **Artifacts**, in fondo alla pagina, scaricare `PostImagesDownloader <tag>.zip`;
4. estrarre lo zip: contiene la cartella `PostImagesDownloader <tag>` con `PostImagesDownloader.exe`, `README.md` e `doc`.

Per scaricare l' artefatto serve un account GitHub. Gli artefatti vengono conservati per 90 giorni, salvo diversa impostazione del repository.

Guida GitHub per la configurazione del .NET SDK nelle Actions: [Build and test .NET](https://docs.github.com/en/actions/tutorials/build-and-test-code/net).

**Sei un utente qualsiasi:**

1. aprire una issue qualsiasi del repository (o crearne una nuova);
2. scrivere il commento `/build`;
3. attendere: il workflow compila l'ultimo commit del ramo principale e risponde nella issue con il collegamento al risultato.

In alternativa puoi creare un fork e avviare il workflow dal tuo fork, dopo aver abilitato le Actions nella sua scheda **Actions**.

## Possibili espansioni
Il programma è grezzo, ha fatto quello che mi serviva ma può essere rifinito

* Supporto multilingua
* Aggiunta ulteriori servizi