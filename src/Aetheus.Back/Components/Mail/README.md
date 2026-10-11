# Module Mail

## Responsabilité

Expose l’état et les opérations de gestion de la pile mail d’un serveur.

## Points d’entrée

- API ou consommateur principal : `MailController.cs`.
- Enregistrement DI : `MailModuleExtensions.cs`.
- Les interfaces `I*` définissent les contrats du module; les services portent la logique et les repositories l’accès persistant lorsqu’il existe.

## Limites

Les contrôleurs restent minces et délèguent aux services. Les échanges inter-modules passent par les interfaces publiques et les DTOs de `Aetheus.Shared`; aucun autre module ne doit accéder directement aux détails de persistance de ce dossier.

## PLAN-005 : adoption et exploitation de la pile

- `MailInventoryReconciliationHandler` reçoit `MailInventoryReportedEvent` (publié par le module Servers à chaque heartbeat) et délègue à `MailStateReconciler`, qui crée les lignes adoptées et désactive, sans jamais supprimer, celles que le serveur ne remonte plus.
- `MailStackController` expose certificat TLS, antispam rspamd, test de livraison, file d’attente, usage des boîtes, vérification DNS et diagnostic ; chaque opération renvoie l’identifiant de sa tâche.
- `MailDnsVerifier` (DnsClient) et `MailDnsRefreshService` tiennent à jour les indicateurs SPF, DKIM et DMARC.
- Les helpers root `mail-setup` et `mail-manage` sont prouvés par `tests/mail-stack/run.sh` (conteneur systemd, Debian 12, Debian 13, Ubuntu 24.04).

## Recettes R2-080 et R2-090 : prérequis et état

- `GET mail/prerequisites?hostname=` vérifie, pour le nom d’hôte que propose l’assistant de première configuration, l’enregistrement A (il pointe sur l’adresse du serveur) et le DNS inverse (il nomme l’hôte), avec l’enregistrement attendu.
- `POST mail/prerequisites/outbound-smtp` (droit Write) met en file `MailOutboundProbe` : l’agent ouvre lui-même une connexion sortante sur le port 25 (sans helper ni privilège) et écrit une ligne `CHECK outbound-25`. Un agent qui ne publie pas la capacité `mail.outbound-probe` est refusé par son nom.
- Le diagnostic porte, par domaine actif, MX, SPF, DKIM et DMARC avec l’enregistrement à publier ; il lit ces vérifications sans modifier les indicateurs stockés (`MailDnsVerifier.CheckAsync`).

## Recettes R1-10 et R1-14 : SPF et cache DNS

- Le SPF n'est conforme que s'il autorise ce serveur : l'enregistrement proposé (`mx a` en tête) est publié, ou l'évaluation de l'enregistrement (RFC 7208, `MailSpfAuthorization` : `ip4`, `ip6`, `a`, `mx`, `include`, `redirect`, au plus dix requêtes DNS) donne un passage à l'adresse du serveur. Sinon la ligne est « À corriger » (`server-not-authorized`) avec l'enregistrement à publier, qui garde les termes déjà publiés.
- Un MX qui montre encore un autre hôte porte la durée de vie restante de la réponse DNS lue (`MxTimeToLiveAsync`), telle que le résolveur la donne ; rien n'est inventé quand elle manque.

## Recette R1-17 : messagerie intégrée

- `Mailbox/` lit et écrit le courrier d'un compte du serveur géré : `MailboxController` (`api/servers/{id}/mail/accounts/{accountId}/mailbox…`, droit Read pour lire, Write pour envoyer, supprimer, marquer et créer un dossier), `MailboxService`, `MailKitMailboxConnector` (IMAPS 993 et envoi 587 en STARTTLS, validation du certificat conservée). `GET mail/client-settings` donne les paramètres d'un client de messagerie.
- Le mot de passe de la boîte est saisi à l'ouverture (`unlock`, limité par la politique `auth-token`), vérifié par une vraie connexion IMAP, puis gardé seulement en mémoire par `MailboxCredentialStore` (clé : utilisateur, serveur, compte ; 30 min sans usage, 8 h au plus, 500 entrées) ; « Verrouiller », un refus de connexion ou un redémarrage l'oublient, et la fin de session de l'utilisateur (déconnexion, chaîne de jetons révoquée, changement de son propre mot de passe, désactivation, suppression) oublie tous les siens à la fois (R1-37, `MailboxCredentialSessionEndHandler` sur `UserSessionEndedEvent`). Jamais en base, dans les journaux ni dans l'audit.
- Le HTML d'un message est nettoyé côté serveur (`MailboxHtmlSanitizer`) puis affiché dans un cadre isolé sans script ; les images distantes sont bloquées et ne se chargent qu'à la demande, par `MailboxRemoteImageFetcher` à travers la protection SSRF partagée (redirections suivies à la main, types d'image et tailles bornés, 15 s au plus par image, corps compris). Un message que le serveur IMAP déclare plus gros que 25 Mo (`MailboxService.MaxMessageBytes`) est refusé (400) avant son téléchargement.
- Reste à faire : oublier les mots de passe à la déconnexion (R1-37), joindre ses propres fichiers (R1-38).
