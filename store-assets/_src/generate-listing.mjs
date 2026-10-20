import { writeFileSync } from 'node:fs';
import { fileURLToPath } from 'node:url';
import { dirname, join } from 'node:path';

function cp(str) {
  return [...str].length;
}

function field(level, title, limit, text) {
  const count = cp(text);
  const hashes = '#'.repeat(level);
  return `${hashes} ${title} — ${count.toLocaleString('en-US')} / ${limit.toLocaleString('en-US')}\n\n\`\`\`\n${text}\n\`\`\`\n`;
}

// -----------------------------------------------------------------------------
// English (en-US)
// -----------------------------------------------------------------------------
const en = {
  title: "Coo: Baby Tracker & Sleep Log",
  altA: "Coo: Baby Feed & Sleep Tracker",
  altB: "Coo: Newborn & Baby Tracker",
  short: "Log breastfeeding, sleep and diapers on a calm timeline. Private & family sync.",
  full: `Your baby’s day, without the noise.

Coo is a calm, private baby tracker designed for real life – especially at 3 a.m. Follow breastfeeding, bottles, sleep, diapers, and pumping on a continuous 24-hour visual timeline. No blinding neon screens, no intrusive ads, and no mandatory account.

Free, private by design, and built for exhausted parents.

ONE CALM 24-HOUR TIMELINE
• A single visual rhythm: see feeds, sleep sessions, and diaper changes as a clear, continuous band throughout the day.
• Sleep that flows past midnight: baby fell asleep at 10 p.m. and woke at 3 a.m.? Coo shows the full 5-hour continuous stretch, never cutting your night in half at midnight.
• Zoom in on any hour: tap the timeline to inspect the rhythm up close, and glide smoothly between days.

SMART BREASTFEEDING & NURSING TIMER
• Left and right timers with clear active side indicator so you always know where you left off.
• Pause and resume: baby took a short break to burp or look around? Pause and resume without starting a whole new session.
• Fast side switch: switch breasts with one tap while keeping the total feeding duration accurate.
• Intelligent feed merging: split sessions (e.g. 5 minutes left, a 3-minute burp pause, 10 minutes right) combine into one clean nursing event.

SLEEP, NAPS & WAKE WINDOWS
• Track daytime naps and nighttime sleep with a single tap.
• Live sleep timer with fell asleep and woke up actions.
• Clear sleep durations calculated accurately across midnight transitions.

BOTTLES, DIAPERS & PUMPING
• Bottle feeding: record formula or expressed breast milk in milliliters (ml) or ounces (oz).
• Diaper tracker: log wet, dirty, or mixed diapers with a single touch.
• Expressed milk pumping: track milk yield per side or total session volume.
• Add notes to any event to remember medicines, tummy time, or mood.

SMART FAMILY SYNC & CONFLICT MERGE
• Keep your log safe and share with your partner, grandparents, or babysitter via Google Sign-In.
• Real-time updates so both caregivers always know when baby last fed or slept.
• "Needs attention" smart resolver: if both parents log at the same time or entries overlap, Coo flags them and lets you merge or resolve them in one tap.

STATISTICS & PEDIATRICIAN SUMMARIES
• Daily, weekly, and monthly summaries: total sleep, average nap length, feed count, average nursing duration, diaper counts, and pumped milk volume.
• Have exact answers ready when your pediatrician asks "How many wet diapers today?" or "How long are the wake windows?"
• Compare patterns across days to discover emerging routines naturally.

GENTLE ON TIRED EYES AT 3 A.M.
• Designed with warm terracotta and calming paper tones – no harsh glaring white screens.
• Automatic Quiet Hours mode adapts to nighttime feedings so your screen never blinds you or wakes your baby.

PRIVATE BY DESIGN: LOCAL-FIRST
• Works completely offline: your baby log lives on your device and opens instantly without an internet connection.
• No account required: start tracking the moment you open the app.
• No ads, no trackers, no third-party data broker SDKs. Intimate baby health data should never be monetized.
• Export or delete your data at any time in Settings.

WHY I BUILT COO
When our newborn arrived, night feeds were stressful enough without loud ads, clunky subscriptions, and apps that reset sleep logs at midnight. I built Coo to give parents a calm, reliable, and private companion for the newborn journey. If you have feedback or ideas, write to me directly: andrzej@lechdigital.nl

Privacy policy: https://lechdigital.nl/projects/coo/privacy/`
};

// -----------------------------------------------------------------------------
// Polish (pl-PL)
// -----------------------------------------------------------------------------
const pl = {
  title: "Coo: Dziennik maluszka i sen",
  altA: "Coo: Dziennik noworodka i sen",
  altB: "Coo: Karmienie piersią i sen",
  short: "Karmienie piersią, sen i pieluszki na osi czasu. Bez reklam, synchronizacja par.",
  full: `Dzień Twojego maluszka, bez zbędnego szumu.

Coo to spokojny, prywatny dziennik opieki nad noworodkiem i niemowlakiem, stworzony z myślą o prawdziwym życiu – zwłaszcza o 3 w nocy. Zapisuj karmienie piersią, butelkę, sen, pieluszki i odciąganie pokarmu na czytelnej, 24-godzinnej osi czasu. Bez rażących ekranów, bez reklam i bez wymuszonego zakładania konta.

Prywatny, bezpłatny i stworzony dla niewyspanych rodziców.

CZYTELNA 24-GODZINNA OŚ CZASU
• Rytm dnia maluszka: karmienia, drzemki i pieluszki ułożone w ciągły, przejrzysty pas wydarzeń.
• Sen przechodzący przez północ: maluszek zasnął o 22:00 i obudził się o 3:00? Coo pokazuje pełne 5 godzin ciągłego snu, zamiast ucinać noc w połowie.
• Przybliżenie osi czasu: stuknij w wykres, by przyjrzeć się szczegółom i płynnie przechodzić między dniami.

INTELIGENTNY STOPER KARMIENIA PIERSIĄ
• Pomiary dla lewej i prawej piersi z wyraźnym wskaźnikiem aktywnej strony.
• Pauza i wznowienie: maluszek potrzebuje chwili na odbicie lub przerwę? Wstrzymaj stoper i wznów go bez tworzenia nowego wpisu.
• Szybka zmiana piersi: przełącz stronę jednym dotknięciem, zachowując dokładny łączny czas karmienia.
• Automatyczne łączenie karmień: krótkie serie (np. 5 minut lewa pierś, 3 minuty przerwy, 10 minut prawa) Coo łączy w jedno spójne karmienie.

SEN, DRZEMKI I OKNA AKTYWNOŚCI
• Zapisuj drzemki w ciągu dnia i sen nocny jednym dotknięciem.
• Bieżący licznik snu z opcjami „Zasnął” oraz „Obudził się”.
• Rzeczywisty czas snu bez przekłamań na przełomie dób.

BUTELKA, PIELUSZKI I ODCIĄGANIE POKARMU
• Karmienie butelką: mleko modyfikowane lub odciągnięte w mililitrach (ml) lub uncjach (oz).
• Pieluszki: mokra, brudna lub obie (mieszana) za jednym dotknięciem.
• Laktator: zapisuj ilość odciągniętego mleka z podziałem na strony lub łącznie.
• Notatki do wpisów: zapisuj leki, witaminy, czas na brzuszku czy nastrój.

SYNCHRONIZACJA RODZINY I ROZWIĄZYWANIE KONFLIKTÓW
• Bezpieczna kopia i współdzielenie z partnerem lub opiekunem przez Logowanie Google.
• Aktualizacje na żywo: oboje rodzice natychmiast wiedzą, kiedy maluch ostatnio jadł lub spał.
• „Do sprawdzenia”: gdy oboje rodzice zapiszą wpis w tym samym momencie, Coo wyróżnia duplikaty i pozwala połączyć je jednym dotknięciem.

STATYSTYKI NA WIZYTY U PEDIATRY
• Podsumowania dnia, tygodnia i miesiąca: łączny czas snu, średnia długość drzemki, liczba karmień, średni czas przy piersi, liczba pieluszek i objętość odciągniętego mleka.
• Gotowe odpowiedzi, gdy lekarz pyta: „Ile mokrych pieluszek było dzisiaj?” lub „Jak długie są przerwy między karmieniami?”.
• Porównuj dni i obserwuj, jak naturalnie kształtuje się rytm Twojego dziecka.

ŁAGODNY DLA ZMĘCZONYCH OCZU O 3 W NOCY
• Ciepła paleta barw papieru i terakoty – żadnych jaskrawych, oślepiających neonów.
• Automatyczny tryb nocny dostosowany do nocnych karmień, by nie rozbudzać maluszka ani rodziców.

PRYWATNOŚĆ I DZIAŁANIE OFFLINE
• Działa w 100% offline: dane dziecka są zapisane w telefonie i otwierają się natychmiast bez zasięgu sieci.
• Bez konieczności logowania: korzystaj od razu po zainstalowaniu.
• Zero reklam, zero trackerów i brokerów danych. Wrażliwe dane zdrowotne Twojego dziecka nie są towarem.
• Eksport i usuwanie danych w każdej chwili w Ustawieniach.

DLACZEGO STWORZYŁEM COO
Kiedy na świecie pojawiła się nasza córka, nocne karmienia były wystarczająco trudne bez wyskakujących reklam, drogich subskrypcji i aplikacji ucinających sen o północy. Stworzyłem Coo, by dać rodzicom spokojne, niezawodne i w pełni prywatne narzędzie. Jeśli masz uwagi lub pomysły, napisz do mnie: andrzej@lechdigital.nl

Polityka prywatności: https://lechdigital.nl/projects/coo/privacy/`
};

// -----------------------------------------------------------------------------
// German (de-DE)
// -----------------------------------------------------------------------------
const de = {
  title: "Coo: Baby Tracker & Babyschlaf",
  altA: "Coo: Stillen & Baby-Tagebuch",
  altB: "Coo: Still-App & Babyschlaf",
  short: "Stillen, Babyschlaf und Windeln im ruhigen Tagesverlauf. Privat & Familiensync.",
  full: `Der Tag deines Babys, ganz ohne Hektik.

Coo ist ein ruhiger, privater Baby-Tracker für den echten Familienalltag – besonders um 3 Uhr nachts. Verfolge Stillen, Fläschchen, Schlaf, Windeln und Abpumpen auf einem übersichtlichen, fortlaufenden 24-Stunden-Zeitstrahl. Ohne grelle Displays, ohne störende Werbung und ohne Zwangskonto.

Kostenlos, datensparsam und für müde Eltern entwickelt.

EIN RUHIGER 24-STUNDEN-ZEITSTRAHL
• Ein klarer Tagesrhythmus: Stillmahlzeiten, Schläfchen und Windeln als zusammenhängendes Band über den Tag.
• Schlaf über Mitternacht hinweg: Baby ist um 22:00 Uhr eingeschlafen und um 3:00 Uhr aufgewacht? Coo zeigt die vollen 5 Stunden Schlaf am Stück, anstatt die Nacht um 00:00 Uhr abzuschneiden.
• Detailansicht: Tippe auf den Zeitstrahl, um einzelne Abschnitte zu vergrößern und nahtlos zwischen Tagen zu wechseln.

INTELLIGENTER STILL-TIMER
• Getrennte Zeiten für linke und rechte Brust mit klarer Anzeige der aktiven Seite.
• Pause und Fortsetzen: Das Baby macht ein Bäuerchen oder eine kurze Pause? Pausiere einfach, ohne eine neue Mahlzeit anlegen zu müssen.
• Schneller Seitenwechsel: Wechsle die Brust mit einem Fingertipp bei exakter Gesamtdauer.
• Automatisches Zusammenführen: Kurze aufeinanderfolgende Mahlzeiten fasst Coo übersichtlich zusammen.

SCHLAF, NICKERCHEN & WACHPHASEN
• Tagesschläfchen und Nachtschlaf mit einem Tippen erfassen.
• Live-Schlaftimer mit „Eingeschlafen“ und „Aufgewacht“.
• Exakt berechnete Schlafdauer auch über Tagesgrenzen hinweg.

FLÄSCHCHEN, WINDELN & ABPUMPEN
• Fläschchen: Pre-Nahrung oder abgepumpte Muttermilch in Millilitern (ml) oder Unzen (oz).
• Windeln: Nass, voll oder beides mit einer einzigen Berührung.
• Abpumpen: Erfasse die Milchmenge pro Seite oder als Gesamtergebnis.
• Notizen hinzufügen: Für Medikamente, Bauchlage oder Beobachtungen.

FAMILIENSYNCHRONISATION & ABGLEICH
• Sicher synchronisieren mit Partner oder Betreuungsperson via Google-Anmeldung.
• Live-Abgleich: Beide Eltern wissen sofort, wann das Baby zuletzt getrunken oder geschlafen hat.
• „Zu prüfen“: Haben beide gleichzeitig etwas eingetragen, erkennt Coo Überschneidungen und führt sie sauber zusammen.

STATISTIKEN FÜR DIE U-UNTERSUCHUNG
• Übersichten für Tag, Woche und Monat: Gesamtschlaf, durchschnittliche Schlafdauer, Mahlzeiten, Windelanzahl und Abpumpmengen.
• Sofort auskunftsfähig bei der Kinderärztin: „Wie viele nasse Windeln heute?“ oder „Wie lange sind die Wachphasen?“.
• Erkenne den natürlichen Rhythmus deines Kindes.

ANGENEHM FÜR MÜDE AUGEN UM 3 UHR NACHTS
• Warme Terrakotta- und Papiertöne – kein grellweißes Licht.
• Automatischer Nachtmodus für nächtliches Stillen, der weder dich noch dein Baby blendet.

ECHTER DATENSCHUTZ: LOCAL-FIRST
• Funktioniert komplett offline: Alle Daten bleiben auf deinem Smartphone und öffnen sich ohne Internet sofort.
• Kein Account nötig: Direkt nach der Installation startklar.
• Keine Werbung, keine Tracker, kein Weiterverkauf von Daten.
• Exportiere oder lösche deine Einträge jederzeit in den Einstellungen.

WARUM ICH COO ENTWICKELT HABE
Als unsere Tochter geboren wurde, waren die Nächte fordernd genug – laute Werbebanner, teure Abos und Apps, die den Schlaf um Mitternacht trennten, halfen nicht. Ich habe Coo gebaut, um Eltern einen ruhigen, zuverlässigen Begleiter zu geben. Bei Fragen oder Ideen schreibe mir gerne: andrzej@lechdigital.nl

Datenschutzerklärung: https://lechdigital.nl/projects/coo/privacy/`
};

// -----------------------------------------------------------------------------
// French (fr-FR)
// -----------------------------------------------------------------------------
const fr = {
  title: "Coo: Suivi bébé & allaitement",
  altA: "Coo: Suivi de bébé & sommeil",
  altB: "Coo: Carnet et suivi de bébé",
  short: "Allaitement, sommeil et couches sur une frise claire. Nuits sereines et synchro.",
  full: `La journée de votre bébé, sans superflu.

Coo est un carnet de suivi pour bébé apaisant et privé, pensé pour la vraie vie – surtout à 3 heures du matin. Suivez l'allaitement, les biberons, le sommeil, les couches et le tire-lait sur une frise chronologique fluide de 24 heures. Sans écran agressif, sans publicité et sans compte obligatoire.

Privé, gratuit et conçu pour les parents fatigués.

UNE FRISE DE 24 HEURES CLAIRE ET APAISANTE
• Un aperçu continu du rythme : visualisez les tétées, les siestes et les changes sous forme de bande continue tout au long de la journée.
• Sommeil continu après minuit : bébé s'est endormi à 22h et s'est réveillé à 3h ? Coo affiche les 5 heures complètes, sans couper la nuit en deux à minuit.
• Vue détaillée : touchez la frise pour examiner les heures de près et passez d'un jour à l'autre en douceur.

CHRONOMÈTRE D'ALLAITEMENT INTELLIGENT
• Minuteurs distincts pour le sein gauche et droit avec indicateur clair du dernier côté utilisé.
• Pause et reprise : bébé s'interrompt pour un rot ou un câlin ? Mettez en pause sans créer une nouvelle tétée.
• Changement de sein rapide : alternez d'un geste tout en conservant la durée totale exacte.
• Fusion intelligente : les tétées fractionnées rapprochées se regroupent automatiquement en une seule session claire.

SOMMEIL, SIESTES ET TEMPS D'ÉVEIL
• Enregistrez les siestes de la journée et le sommeil nocturne en un seul geste.
• Minuteur de sommeil en direct avec actions rapides « Endormi » et « Réveillé ».
• Durées réelles calculées sans coupure à minuit.

BIBERONS, COUCHES ET TIRE-LAIT
• Biberons : lait infantile ou lait maternel exprimé en millilitres (ml) ou en onces (oz).
• Couches : mouillée, selle ou mixte d'un simple toucher.
• Tire-lait : notez la quantité de lait tirée par côté ou au total.
• Ajoutez des notes pour suivre les médicaments, l'éveil ou l'humeur.

SYNCHRONISATION EN FAMILLE ET RÉSOLUTION DE CONFLITS
• Sauvegardez et partagez en temps réel avec votre partenaire ou la nounou via la connexion Google.
• Mises à jour instantanées pour savoir immédiatement quand bébé a mangé ou dormi.
• « À vérifier » : si les deux parents notent un événement en même temps, Coo détecte les doublons et permet de les fusionner en un geste.

STATISTIQUES PRÊTES POUR LE PÉDIATRE
• Bilans quotidiens, hebdomadaires et mensuels : sommeil total, durée moyenne des siestes, nombre de tétées, temps moyen au sein, nombre de couches et volumes tirés.
• Répondez en un coup d'œil aux questions du médecin : « Combien de couches mouillées aujourd'hui ? » ou « Quelle est la durée des phases d'éveil ? ».
• Suivez l'évolution naturelle des rythmes de votre enfant.

DOUX POUR LES YEUX FATIGUÉS À 3H DU MATIN
• Couleurs douces inspirées du papier chaud et de la terre cuite – aucun écran blanc éblouissant.
• Mode Heures Calmes automatique pour les tétées de nuit afin de ne réveiller ni bébé ni vos yeux.

CONFIDENTIALITÉ TOTALE : LOCAL D'ABORD
• Fonctionne 100 % hors ligne : le carnet de votre bébé reste sur votre téléphone et s'ouvre instantanément sans réseau.
• Aucun compte exigé : commencez dès l'ouverture de l'application.
• Sans pub, sans traceurs, sans revente de données personnelles.
• Exportez ou supprimez l'intégralité de vos données quand vous le souhaitez.

POURQUOI J'AI CRÉÉ COO
À la naissance de notre fille, les nuits étaient déjà assez éprouvantes sans avoir à subir des publicités agressives ou des applications qui coupent le sommeil à minuit. J'ai créé Coo pour offrir aux parents un outil serein, fiable et respectueux de leur intimité. Pour toute suggestion : andrzej@lechdigital.nl

Politique de confidentialité : https://lechdigital.nl/projects/coo/privacy/`
};

// -----------------------------------------------------------------------------
// Spanish (es-ES)
// -----------------------------------------------------------------------------
const es = {
  title: "Coo: Registro de bebé y sueño",
  altA: "Coo: Lactancia y registro bebé",
  altB: "Coo: Diario del recién nacido",
  short: "Lactancia, sueño y pañales en una línea de tiempo clara. Privado y sincronizado.",
  full: `El día de tu bebé, sin distracciones.

Coo es un diario de bebé sereno y privado, pensado para la vida real, especialmente a las 3 de la madrugada. Registra lactancia materna, biberones, sueño, pañales y extractor de leche en una línea de tiempo visual continua de 24 horas. Sin pantallas deslumbrantes, sin anuncios molestos y sin cuenta obligatoria.

Privado, sin publicidad y pensado para padres cansados.

UNA LÍNEA DE TIEMPO VISUAL DE 24 HORAS
• Todo el ritmo de un vistazo: tomas, sueño y cambios de pañal en una franja continua a lo largo del día.
• Sueño continuo que pasa de medianoche: ¿el bebé se durmió a las 22:00 y despertó a las 3:00? Coo muestra las 5 horas completas de descanso, sin partir la noche por la mitad a las 00:00.
• Vista detallada: toca la línea de tiempo para ampliar las horas y deslízate suavemente de un día a otro.

TEMPORIZADOR INTELIGENTE DE LACTANCIA
• Cronómetros para pecho izquierdo y derecho con indicación clara del último lado utilizado.
• Pausa y reanudación: ¿el bebé necesita una pausa para eructar o descansar? Pausa y reanuda sin tener que iniciar una nueva toma.
• Cambio de pecho con un toque: alterna el pecho al instante manteniendo el tiempo total exacto.
• Fusión inteligente de tomas: si das un pecho, haces una pausa corta y continúas, Coo puede unirlos en una sola toma ordenada.

SUEÑO, SIESTAS Y VENTANAS DE VIGILIA
• Registra siestas y sueño nocturno con un solo toque.
• Temporizador de sueño en directo con acciones de «Se durmió» y «Despertó».
• Duración real calculada con precisión sin cortes a medianoche.

BIBERÓN, PAÑALES Y SACALECHES
• Biberón: leche de fórmula o leche materna extraída en mililitros (ml) u onzas (oz).
• Pañales: mojado, sucio o ambos con un solo toque.
• Sacaleches: registra la cantidad de leche extraída por pecho o en total.
• Añade notas para medicinas, tiempo boca abajo o cualquier observación.

SINCRONIZACIÓN FAMILIAR Y RESOLUCIÓN DE CONFLICTOS
• Comparte en tiempo real con tu pareja o cuidadores iniciando sesión con Google.
• Información al instante para saber cuándo comió o durmió el bebé por última vez.
• «Para revisar»: si ambos progenitores anotan algo a la vez, Coo detecta duplicados y permite unirlos con un toque.

ESTADÍSTICAS LISTAS PARA EL PEDIATRA
• Resúmenes por día, semana y mes: sueño total, duración media de siestas, número de tomas, tiempo de lactancia, pañales y leche extraída.
• Responde al instante a las preguntas del médico: «¿Cuántos pañales mojados hoy?» o «¿Cuánto duran sus siestas?».
• Observa cómo madura el ritmo natural de tu bebé.

SUAVE PARA OJOS CANSADOS A LAS 3 A.M.
• Tonos cálidos de papel y terracota – nada de pantallas blancas deslumbrantes.
• Modo Noche silencioso automático para tomas nocturnas, pensado para no desvelarte ni despertar al bebé.

MÁXIMA PRIVACIDAD: LOCAL FIRST
• Funciona 100 % sin conexión: los datos del bebé viven en tu teléfono y abren al instante sin internet.
• Sin cuenta obligatoria: úsalo desde el primer segundo tras instalarlo.
• Sin anuncios, sin rastreadores ni venta de datos personales a terceros.
• Exporta o borra tus datos en cualquier momento desde Ajustes.

POR QUÉ CREÉ COO
Cuando nació nuestra hija, las tomas nocturnas ya eran bastante duras sin tener que lidiar con publicidad invasiva, suscripciones caras y aplicaciones que cortaban el sueño a medianoche. Diseñé Coo para ofrecer un apoyo tranquilo, fiable y respetuoso con la intimidad familiar. Para sugerencias o dudas: andrzej@lechdigital.nl

Política de privacidad: https://lechdigital.nl/projects/coo/privacy/`
};

// -----------------------------------------------------------------------------
// Italian (it-IT)
// -----------------------------------------------------------------------------
const it = {
  title: "Coo: Diario del bebè e sonno",
  altA: "Coo: Diario neonato e sonno",
  altB: "Coo: Allattamento e sonno bebè",
  short: "Allattamento, sonno e pannolini su una linea del tempo chiara. Privato e sereno.",
  full: `La giornata del tuo bebè, senza complicazioni.

Coo è un diario per neonati calmo e privato, creato per la vita reale – soprattutto alle 3 di notte. Monitora allattamento al seno, biberon, sonno, pannolini e tiralatte su una linea temporale continua di 24 ore. Niente schermate accecanti, nessuna pubblicità e nessun account obbligatorio.

Privato, gratuito e pensato per genitori stanchi.

UNA LINEA TEMPORALE CONTINUA DI 24 ORE
• Il ritmo della giornata a colpo d'occhio: poppate, sonno e cambi di pannolino visualizzati in una barra fluida e continua.
• Sonno che supera la mezzanotte: il bebè si è addormentato alle 22:00 e si è svegliato alle 3:00? Coo mostra le 5 ore piene di sonno consecutivo, senza spezzare la notte a mezzanotte.
• Vista dettagliata: tocca la linea temporale per ingrandire ogni ora e passare da un giorno all'altro con facilità.

CRONOMETRO ALLATTAMENTO INTELLIGENTE
• Timer separati per seno sinistro e destro con indicazione visiva dell'ultimo lato usato.
• Pausa e ripresa: il bebè fa una pausa per il ruttino? Metti in pausa e riprendi senza dover creare una nuova poppata.
• Cambio lato rapido: passa all'altro seno con un tocco mantenendo la durata complessiva esatta.
• Unione automatica delle poppate: sessioni ravvicinate vengono raggruppate in un unico evento pulito.

SONNO, PISOLINI E FINESTRE DI VEGLIA
• Registra i pisolini diurni e il sonno notturno con un solo tocco.
• Timer del sonno in tempo reale con comandi «Addormentato» e «Svegliato».
• Durata effettiva calcolata con precisione oltre la mezzanotte.

BIBERON, PANNOLINI E TIRALATTE
• Biberon: latte artificiale o latte materno spremuto in millilitri (ml) o once (oz).
• Pannolini: bagnato, sporco o entrambi con un solo tocco.
• Tiralatte: annota il volume di latte estratto per lato o totale.
• Aggiungi note per farmaci, bagnetto o momenti sul pancino.

SINCRONIZZAZIONE FAMILIARE E GESTIONE CONFLITTI
• Condividi in tempo reale con partner o nonni effettuando l'accesso con Google.
• Aggiornamenti immediati: entrambi i genitori sanno subito quando il bimbo ha mangiato o dormito.
• «Da controllare»: se entrambi inserite un evento nello stesso momento, Coo segnala i duplicati e vi permette di unirli con un tocco.

STATISTICHE PRONTE PER IL PEDIATRA
• Riepiloghi giornalieri, settimanali e mensili: sonno totale, durata media dei pisolini, numero di poppate, tempo al seno, cambi di pannolino e latte estratto.
• Rispondi all'istante al pediatra: «Quanti pannolini bagnati oggi?» o «Quanto durano i pisolini?».
• Scopri i ritmi naturali del tuo bambino nel corso dei giorni.

DELICATO SUGLI OCCHI ALLE 3 DI NOTTE
• Tonalità calde ispirate alla carta e alla terracotta – niente luci bianche abbaglianti.
• Modalità Notte silenziosa automatica per le poppate notturne, per non disturbare né te né il bambino.

MASSIMA PRIVACY: PRIMA DI TUTTO IN LOCALE
• Funziona al 100% offline: i dati del tuo bambino restano sul telefono e sono subito accessibili senza rete.
• Nessun account necessario: inizia a registrare subito dopo l'installazione.
• Senza pubblicità, senza tracciamento e senza rivendita di dati sanitari sensibili.
• Esporta o elimina tutti i dati in qualsiasi momento dalle Impostazioni.

PERCHÉ HO CREATO COO
Quando è nata la nostra bimba, le notti erano già abbastanza faticose senza dover sopportare pubblicità invadenti e app che azzeravano il sonno a mezzanotte. Ho creato Coo per offrire ai genitori uno strumento sereno, affidabile e rispettoso della privacy. Se hai suggerimenti o domande, scrivimi a: andrzej@lechdigital.nl

Informativa sulla privacy: https://lechdigital.nl/projects/coo/privacy/`
};

// -----------------------------------------------------------------------------
// Dutch (nl-NL)
// -----------------------------------------------------------------------------
const nl = {
  title: "Coo: Baby Tracker & Babyslaap",
  altA: "Coo: Borstvoeding & Babyslaap",
  altB: "Coo: Baby Dagboek & Slaaplog",
  short: "Borstvoeding, slaap en luiers op een tijdlijn. Geen advertenties, geen account.",
  full: `De dag van je baby, zonder de drukte.

Coo is een rustige, privacy-vriendelijke baby tracker voor het echte leven – vooral om 3 uur 's nachts. Volg borstvoeding, flesjes, slaap, luiers en kolven op een doorlopende 24-uurs tijdlijn. Zonder fel wit licht, zonder advertenties en zonder verplicht account.

Gratis, ontworpen voor privacy en gemaakt voor vermoeide ouders.

EEN RUSTIGE 24-UURS TIJDLIJN
• Het dagritme in één oogopslag: voedingen, dutjes en luiers overzichtelijk weergegeven in een doorlopende band.
• Slaap over middernacht heen: sliep je baby om 22:00 uur en werd hij om 03:00 uur wakker? Coo toont de volledige 5 uur aaneengesloten slaap, zonder de nacht om middernacht doormidden te knippen.
• Detailweergave: tik op de tijdlijn om in te zoomen op specifieke uren en wissel soepel tussen dagen.

SLIMME BORSTVOEDINGSTIMER
• Aparte timers voor linker- en rechterborst met duidelijke weergave van de actieve zijde.
• Pauzeren en hervatten: stopt je baby even voor een boertje? Pauzeer en hervat zonder een nieuwe voeding te starten.
• Snel wisselen van borst: wissel van kant met één tik terwijl de totale voedingstijd nauwkeurig blijft.
• Slim samenvoegen: korte opeenvolgende voedingen voegt Coo netjes samen tot één overzichtelijke sessie.

SLAAP, DUTJES EN WAAKTIJDEN
• Leg dutjes overdag en de nachtslaap vast met één tik.
• Live slaaptimer met snelle acties voor „In slaap gevallen” en „Wakker geworden”.
• Exacte slaapduur berekend over de middernachtgrens heen.

FLESJES, LUIERS EN KOLVEN
• Flesvoeding: melkpoeder of afgekolfde moedermelk in milliliters (ml) of ounces (oz).
• Luiers: nat, vies of beide met één simpele aanraking.
• Kolven: houd de opbrengst per borst of het totale kolfvolume bij.
• Notities toevoegen: handig voor medicijnen, buiktijd of algemene waarnemingen.

GEZINSSYNCHRONISATIE & CONFLICTEN OPLOSSEN
• Deel veilig in realtime met je partner of oppas via Google Login.
• Altijd up-to-date: beide ouders zien direct wanneer de baby voor het laatst heeft gedronken of geslapen.
• „Aandacht nodig”: voeren beide ouders tegelijk iets in, dan herkent Coo dubbele invoer en voeg je ze met één tik samen.

STATISTIEKEN VOOR HET CONSULTATIEBUREAU
• Overzichten per dag, week en maand: totale slaapduur, gemiddelde dutjestijd, aantal voedingen, gemiddelde drinkduur, luiers en kolfopbrengst.
• Direct antwoord op vragen van de arts: „Hoeveel plasluiers vandaag?” of „Hoe lang zijn de wakkertijden?”.
• Volg het natuurlijke ritme van je kindje over de tijd.

ZACHT VOOR VERMOEIDE OGEN OM 3 UUR 'S NACHTS
• Warme terracotta- en papiertinten – geen oogverblindende witte schermen.
• Automatische Stille Uren-modus voor nachtvoedingen, zodat jij en je baby rustig blijven.

EERLIJKE PRIVACY: LOCAL-FIRST
• Werkt 100% offline: alle gegevens blijven veilig op je telefoon en openen direct zonder internetverbinding.
• Geen account verplicht: direct te gebruiken na installatie.
• Geen advertenties, geen trackers en geen verkoop van gevoelige gezondheidsdata.
• Exporteer of verwijder al je gegevens wanneer je maar wilt in Instellingen.

WAAROM IK COO HEB GEBOUWD
Toen onze dochter werd geboren, waren de nachten al vermoeiend genoeg zonder schreeuwerige reclames, dure abonnementen en apps die de nacht om middernacht afbraken. Ik bouwde Coo om ouders een rustig, betrouwbaar en privacy-vriendelijk hulpmiddel te geven. Heb je feedback of ideeën? Mail me gerust: andrzej@lechdigital.nl

Privacybeleid: https://lechdigital.nl/projects/coo/privacy/`
};

const locales = [
  { code: 'en-US', num: 1, name: 'English', data: en },
  { code: 'pl-PL', num: 2, name: 'Polish', data: pl },
  { code: 'de-DE', num: 3, name: 'German', data: de },
  { code: 'fr-FR', num: 4, name: 'French', data: fr },
  { code: 'es-ES', num: 5, name: 'Spanish', data: es },
  { code: 'it-IT', num: 6, name: 'Italian', data: it },
  { code: 'nl-NL', num: 7, name: 'Dutch', data: nl }
];

let out = `# Coo — Google Play Store Listing

Copy and paste from here into Play Console → Grow → Store presence → Main store listing.
All character counts are measured in Unicode code points (the standard enforced by Google Play Console)
and verified automatically by:

\`\`\`bash
node store-assets/_src/check-listing.mjs
\`\`\`

---
`;

for (const loc of locales) {
  out += `\n## ${loc.num}. ${loc.name} (${loc.code}) → Store listing → App details\n\n`;
  out += field(3, 'App name', 30, loc.data.title);
  out += `Alternates:\n\n`;
  out += field(4, 'Alternate A', 30, loc.data.altA);
  out += field(4, 'Alternate B', 30, loc.data.altB);
  out += `### Short description\n\n`;
  // Replace the heading to include count / limit
  out = out.slice(0, out.lastIndexOf('### Short description\n\n'));
  out += field(3, 'Short description', 80, loc.data.short);
  out += field(3, 'Full description', 4000, loc.data.full);
  out += `---\n`;
}

// Add Screenshots, Graphics, and Store Settings sections
out += `
## Screenshots (8 phone screenshots)

The first three screenshots appear in Google Play search results on mobile devices. They carry the primary conversion hooks: the continuous 24h timeline, calm night mode for 3 a.m., and the smart breastfeeding timer.

| Slot | File | Headline / subline | Target screen |
|---|---|---|---|
| 1 | \`phone/phone-1-timeline.png\` | Every feed and nap, *on one calm timeline.* / Continuous 24h rhythm at a single glance | Home (Log view with continuous 24h rhythm band) |
| 2 | \`phone/phone-2-night.png\` | Gentle on tired eyes *at 3 a.m.* / Warm paper tones & Quiet Hours that won't wake baby | Night Quiet Hours mode (3:15 a.m. nursing) |
| 3 | \`phone/phone-3-breastfeed.png\` | Smart nursing timer. *Pause, switch, done.* / Remembers last side and merges split feeds | Live Breastfeeding sheet (timer running, Left side active) |
| 4 | \`phone/phone-4-sleep.png\` | Sleep tracking that flows *past midnight.* / Full continuous rest without splitting the night | Zoomed Day Detail sheet (10 p.m. to 3 a.m. sleep stretch) |
| 5 | \`phone/phone-5-family.png\` | Both parents in sync, *without guesswork.* / Real-time sharing with smart duplicate merging | Family sync & "Needs attention" merge card |
| 6 | \`phone/phone-6-stats.png\` | Averages & insights *for doctor visits.* / Daily, weekly, and monthly totals at your fingertips | Statistics tab (sleep averages, feed counts, diapers) |
| 7 | \`phone/phone-7-bottle-diaper-pump.png\` | Bottles, diapers & pumping, *in seconds.* / Track formula, milk yield, and changes in one tap | Log Earlier menu / quick entry cards |
| 8 | \`phone/phone-8-private.png\` | Private by design. *Your baby's data is yours.* / Zero ads, works 100% offline, no account required | Settings page (privacy, export, local data) |

### Localized Screenshot Headlines

#### Polish (pl-PL)
1. Każde karmienie i sen, *na spokojnej osi czasu.* / Rytm całej doby widoczny na jednym rzucie oka
2. Łagodny dla zmęczonych oczu *o 3 w nocy.* / Ciepłe barwy papieru i tryb nocny, który nie rozbudza malucha
3. Mądry stoper karmienia. *Pauza, zmiana, gotowe.* / Pamięta stronę i łączy krótkie serie w jedno karmienie
4. Sen, który trwa *przez północ.* / Pełna długość snu bez sztucznego ucinania nocy
5. Oboje rodzice w kontakcie, *bez zgadywania.* / Synchronizacja na żywo i łączenie wpisów obu opiekunów
6. Średnie i statystyki *na wizyty u pediatry.* / Podsumowania dnia, tygodnia i miesiąca w zasięgu ręki
7. Butelki, pieluszki i laktator *w kilka sekund.* / Mleko, odciąganie i zmiany pieluch jednym dotknięciem
8. Prywatność z założenia. *Dane dziecka są Twoje.* / Bez reklam, działa w pełni offline, bez wymogu konta

#### German (de-DE)
1. Jede Mahlzeit und jedes Schläfchen, *im ruhigen Zeitstrahl.* / Der 24-Stunden-Rhythmus auf einen Blick
2. Sanft zu müden Augen *um 3 Uhr nachts.* / Warme Papiertöne & Nachtmodus, der das Baby nicht weckt
3. Schlauer Still-Timer. *Pause, Wechsel, fertig.* / Merkt sich die Seite und fasst kurze Pausen zusammen
4. Babyschlaf, der *über Mitternacht fließt.* / Echter Gesamtschlaf ohne Schnitt um 00:00 Uhr
5. Beide Eltern im Bilde, *ohne Nachfragen.* / Echtzeit-Sync mit intelligenter Duplikaterkennung
6. Durchschnitte & Einblicke *für die U-Untersuchung.* / Schlaf-, Still- und Windelstatistiken sofort parat
7. Fläschchen, Windeln & Abpumpen *in Sekunden.* / Pre-Milch, Abpumpmenge und Windeln mit einem Tippen
8. Maximaler Datenschutz. *Deine Daten gehören dir.* / Ohne Werbung, 100% offline nutzbar, kein Zwangskonto

#### French (fr-FR)
1. Chaque tétée et sieste, *sur une frise apaisante.* / Le rythme de 24h d'un seul coup d'œil
2. Doux pour les yeux fatigués *à 3h du matin.* / Teintes chaudes et mode nuit pour préserver le sommeil
3. Minuteur d'allaitement malin. *Pause, changez, validé.* / Mémorise le côté et fusionne les tétées rapprochées
4. Un sommeil qui se poursuit *après minuit.* / Votre nuit comptée en entier, sans coupure à minuit
5. Les deux parents synchronisés, *sans hésitation.* / Partage en direct et fusion intelligente des doublons
6. Moyennes et bilans *pour les visites pédiatre.* / Sommeil, tétées et couches résumés en quelques secondes
7. Biberons, couches et tire-lait *en un clin d'œil.* / Quantités de lait et changes enregistrés en un geste
8. Privé par conception. *Vos données vous appartiennent.* / Sans pub, 100% hors ligne, sans compte imposé

#### Spanish (es-ES)
1. Cada toma y siesta, *en una línea de tiempo serena.* / Todo el ritmo de 24 horas a simple vista
2. Suave para ojos cansados *a las 3 a.m.* / Tonos cálidos y modo noche que no desvelan al bebé
3. Temporizador de lactancia inteligente. *Pausa, cambia, listo.* / Recuerda el último pecho y agrupa tomas
4. Sueño continuo que fluye *más allá de medianoche.* / El descanso completo sin cortes a las 00:00
5. Ambos progenitores al día, *sin dudas.* / Sincronización en tiempo real con detección de duplicados
6. Promedios y resúmenes *para el pediatra.* / Estadísticas de sueño, tomas y pañales al instante
7. Biberones, pañales y sacaleches *en segundos.* / Cantidades de leche y cambios con un solo toque
8. Privacidad ante todo. *Los datos de tu bebé son tuyos.* / Sin anuncios, 100% offline, sin cuenta obligatoria

#### Italian (it-IT)
1. Ogni poppata e sonno, *su una linea del tempo calma.* / Tutto il ritmo delle 24 ore a colpo d'occhio
2. Delicato sugli occhi stanchi *alle 3 di notte.* / Tonalità calde e modalità notte per non svegliare il bebè
3. Timer allattamento intelligente. *Pausa, cambio, fatto.* / Ricorda il lato e raggruppa le poppate vicine
4. Sonno continuo che scorre *oltre la mezzanotte.* / Il riposo effettivo senza divisioni a mezzanotte
5. Entrambi i genitori allineati, *senza dubbi.* / Condivisione in tempo reale e unione intelligente delle voci
6. Medie e resoconti *per le visite dal pediatra.* / Statistiche di sonno, poppate e pannolini pronte subito
7. Biberon, pannolini e tiralatte *in pochi secondi.* / Latte, quantità estratta e cambi con un solo tocco
8. Privato fin dall'inizio. *I dati del bebè sono solo tuoi.* / Senza pubblicità, 100% offline, nessun account richiesto

#### Dutch (nl-NL)
1. Elke voeding en dutje, *op één rustige tijdlijn.* / Het hele 24-uurs ritme in één oogopslag
2. Zacht voor vermoeide ogen *om 3 uur 's nachts.* / Warme papiertinten & nachtmodus die je baby niet wekt
3. Slimme borstvoedingstimer. *Pauze, wissel, klaar.* / Onthoudt de laatste borst en voegt korte pauzes samen
4. Slaap die doorloopt *voorbij middernacht.* / Volledige nachtrust zonder knip om middernacht
5. Beide ouders op één lijn, *zonder giswerk.* / Realtime synchronisatie en slim samenvoegen van dubbelingen
6. Gemiddelden & inzichten *voor het consultatiebureau.* / Slaap, voedingen en luiers direct overzichtelijk
7. Flesjes, luiers en kolven *in enkele seconden.* / Melkhoeveelheden en luierwissels met één tik
8. Privacy voorop. *De gegevens van je baby zijn van jou.* / Geen reclame, 100% offline bruikbaar, geen verplicht account

---

## Graphics & Asset Specifications

| Asset | File | Size | Format | Description |
|---|---|---|---|---|
| App icon | \`icon/play-icon-512.png\` | 512 × 512 | 32-bit RGBA PNG, max 1024 KB | High-res store icon (Option D2: stylized dove/baby silhouette on warm terracotta/paper) |
| Feature graphic | \`feature-1024x500.png\` | 1024 × 500 | 24-bit RGB JPEG/PNG, no alpha | Clean warm washi/terracotta backdrop with Coo logo, timeline wave preview, and tagline "Calm baby tracking for real nights." |
| Phone screenshots | \`phone/phone-{1..8}.png\` | 1080 × 1920 | 24-bit PNG, 9:16 aspect ratio | Headless Chrome rendered templates on washi background with localized captions |

---

## Store Settings & Metadata

- **Application category:** Parenting (Primary). Alternative: Health & Fitness.
- **Tags (up to 5):**
  1. Baby tracker
  2. Breastfeeding
  3. Baby sleep
  4. Parenting
  5. Newborn
- **Target audience:** 18+ (Parents, caregivers). Coo is intended for parents to record their infant's care; it is not directed at children.
- **Content rating questionnaire:**
  - Violence: No
  - Sexuality / Profanity: No
  - Controlled substances / Tobacco / Alcohol: No
  - User interaction / Social sharing: No public feeds or chat; private family sharing between invited caregivers only.
  - Shares user physical location: No
  - In-app digital purchases: None in the initial open testing release.
  - Result: PEGI 3 / Everyone.
- **Data safety declaration:**
  - **Data collection:**
    - Optional account (Google Sign-In) to sync between family devices.
    - If user signs in: App stores user email and baby event log in private, encrypted cloud storage solely for family synchronization.
    - If user does not sign in: Zero data leaves the phone. 100% local-first storage.
  - **Data sharing:** Does NOT share data with any third party, data broker, or advertising network.
  - **Security practices:** Data in transit is encrypted (HTTPS/TLS). Data can be deleted at any time in-app ("Delete account" and "Delete data").
- **Government apps:** No.
- **Health apps declaration:** General health and lifestyle tracking app for infants. NOT a medical device; does not provide medical diagnosis or treatment advice.
- **Ads:** No (strictly ad-free).
- **Privacy policy URL:** \`https://lechdigital.nl/projects/coo/privacy/\`
`;

const filePath = join(dirname(fileURLToPath(import.meta.url)), '..', 'PLAY-LISTING.md');
writeFileSync(filePath, out, 'utf8');
console.log('Generated store-assets/PLAY-LISTING.md successfully.');
