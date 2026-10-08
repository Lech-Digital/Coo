with open("eng/strings/strings.py", "r") as f:
    lines = f.readlines()

for i, line in enumerate(lines):
    if line.startswith("P = {"):
        # Insert before P = {
        # The line before is '}'
        lines.insert(i-1, """
'Update.Title': ('Update Available', 'Update verfügbar', 'Mise à jour disponible', 'Actualización disponible', 'Aggiornamento disponibile', 'Dostępna aktualizacja', 'Update beschikbaar'),
'Update.Forced': ('A critical update is required to continue using Coo. Please update the app now.', 'Ein kritisches Update ist erforderlich, um Coo weiter zu nutzen. Bitte aktualisieren Sie die App jetzt.', 'Une mise à jour critique est requise pour continuer à utiliser Coo. Veuillez mettre à jour l\\'application maintenant.', 'Se requiere una actualización crítica para seguir usando Coo. Actualiza la aplicación ahora.', 'È necessario un aggiornamento critico per continuare a usare Coo. Aggiorna l\\'app ora.', 'Wymagana jest krytyczna aktualizacja, aby dalej używać Coo. Zaktualizuj aplikację teraz.', 'Een kritieke update is vereist om Coo te blijven gebruiken. Werk de app nu bij.'),
'Update.Recommended': ('A new version of Coo is available. Update now to get the latest features and bug fixes.', 'Eine neue Version von Coo ist verfügbar. Aktualisieren Sie jetzt, um die neuesten Funktionen und Fehlerbehebungen zu erhalten.', 'Une nouvelle version de Coo est disponible. Mettez à jour maintenant pour obtenir les dernières fonctionnalités et corrections de bugs.', 'Hay una nueva versión de Coo disponible. Actualiza ahora para obtener las últimas funciones y correcciones de errores.', 'È disponibile una nuova versione di Coo. Aggiorna ora per avere le ultime funzioni e correzioni di bug.', 'Dostępna jest nowa wersja Coo. Zaktualizuj teraz, aby uzyskać najnowsze funkcje i poprawki błędów.', 'Er is een nieuwe versie van Coo beschikbaar. Werk nu bij voor de nieuwste functies en bugfixes.'),
'Update.Button': ('Update Now', 'Jetzt aktualisieren', 'Mettre à jour', 'Actualizar ahora', 'Aggiorna ora', 'Zaktualizuj teraz', 'Nu bijwerken'),
""")
        break

with open("eng/strings/strings.py", "w") as f:
    f.writelines(lines)
