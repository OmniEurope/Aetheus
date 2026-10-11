# Module DailyRecap

## Responsabilité

Envoie chaque matin (07:00 Europe/Paris par défaut) un e-mail de synthèse de la veille : exécutions des pipelines parents, état des serveurs (mises à jour système, agent, disque), versions promues ou restaurées, sauvegardes, certificats et secrets proches de l'expiration, constats de sécurité et de qualité, serveurs de messagerie.

## Points d’entrée

- API : `DailyRecapController.cs` (`api/settings/daily-recap`, administrateurs) : réglages, aperçu, envoi immédiat.
- Planification : `DailyRecapSchedulerService.cs`, une vérification par minute sous bail de leader.
- Enregistrement DI : `DailyRecapModuleExtensions.cs`.

## Limites

- L'envoi passe par `Services/Email/IEmailSender` (boîte no-reply du domaine géré, ou serveur SMTP personnalisé).
- Les réglages et le dernier jour envoyé sont des lignes `DailyRecap:*` de `AppSettings` ; le jour est réservé avant l'envoi, donc jamais envoyé deux fois, et un échec n'est pas renvoyé automatiquement : un envoi planifié qui échoue, même par une exception, est journalisé et affiché sur la page des réglages (dernier envoi).
- Les textes de l'e-mail sont dans `DailyRecapStrings.resx` (anglais) et `DailyRecapStrings.fr.resx` (français).
