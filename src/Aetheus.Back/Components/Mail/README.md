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
