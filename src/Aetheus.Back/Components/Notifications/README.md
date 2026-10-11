# Module Notifications

## Responsabilité

Gère les notifications, règles et canaux de diffusion.

## Points d’entrée

- API ou consommateur principal : `NotificationsController.cs` (canaux et règles d'envoi).
- Notifications de l'utilisateur connecté : `UserNotificationsController.cs`, sous
  `api/notifications/me` : liste (`GET`), valeurs de filtre (`GET filter-values`), compteur de non
  lues (`GET unread-count`), lecture (`POST {id}/read`, `POST read-all`), préférences
  (`GET`/`PUT preferences`), projets suivis (`GET subscriptions`, `PUT`/`DELETE subscriptions/{projectId}`)
  et « Tout suivre » (`POST subscriptions/all`, suit tous les projets lisibles, recette R2-034).
- Enregistrement DI : `NotificationsModuleExtensions.cs`.

## Destinataires

Les notifications d'un projet ne vont qu'à ceux qui le suivent. Le créateur d'un projet le suit dès
sa création (`ProjectService.CreateProjectAsync`), et la migration
`SubscribeProjectCreatorsToTheirProjects` a rattaché le créateur des projets existants. Producteurs
hors de ce module : `release.deployed` (`Tasks/ReleaseDeployedNotificationHandler`),
`agent-update.completed` et `agent-update.failed` (`AgentUpdate/AgentUpdateNotificationPublisher`).
Un événement de plateforme sans projet passe par `IUserNotificationService.RecordAdministratorEventAsync`,
qui écrit une notification pour chaque administrateur actif : c'est le cas de la dérive sudoers
(`Servers/Handlers/SudoersDriftNotificationHandler`, événement `alert.triggered`).
- Les interfaces `I*` définissent les contrats du module; les services portent la logique et les repositories l’accès persistant lorsqu’il existe.

## Limites

Les contrôleurs restent minces et délèguent aux services. Les échanges inter-modules passent par les interfaces publiques et les DTOs de `Aetheus.Shared`; aucun autre module ne doit accéder directement aux détails de persistance de ce dossier.

## Envoi par e-mail (recette R1-16)

Chaque notification d'un utilisateur part aussi par e-mail vers l'adresse de son compte (`NotificationEmailer`, au-dessus de `Services/Email/IEmailSender`) ; la ligne de livraison garde le vrai résultat : `Sent`, `Failed` avec le texte SMTP, ou `NotConfigured` avec la raison. Un canal E-mail n'a pas de liste d'adresses : ses règles partent vers les administrateurs actifs, et son test écrit à l'administrateur qui le lance. L'expéditeur est celui des paramètres de la plateforme (onglet E-mail) : par défaut la boîte `no-reply` du domaine de messagerie géré, sinon un serveur SMTP personnalisé.
