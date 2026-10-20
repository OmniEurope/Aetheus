# Module AgentUpdate

## Responsabilité

Orchestre la disponibilité et la livraison des mises à jour d’agent.

## Points d’entrée

- API : routes de `ServersController` sous `/api/servers/{id}/agent/*` et
  `/api/servers/agent/update-all*`; il n’existe pas de contrôleur dédié.
- Enregistrement DI : `AgentUpdateModuleExtensions.cs`.
- `AgentUpdateService` réserve le serveur, attend son inactivité et crée l’unique
  tâche `AgentSelfUpdate`. `QueueUpdateAsync` **ne court-circuite pas** un agent déjà
  à la bonne version : sa seule appelante est l’action par serveur, où un opérateur a
  désigné ce serveur-là, et réinstaller un agent à jour est une raison de cliquer, pas
  une raison de refuser. Le balayage de flotte (`update-all`) continue lui de sauter
  les agents à jour, personne n’y ayant rien désigné. `AlreadyUpToDate` n’est donc plus
  atteignable par le chemin par serveur.
- `AgentCompatibilityPolicy` et `AgentReleaseCatalog` évaluent la fenêtre de
  protocole, les capacités et le manifeste distribué.
- `AgentUpdateRepository` persiste `AgentUpdateRequest`; le coordinateur traite
  la file et `AgentUpdateConfirmationService` confirme uniquement depuis le
  heartbeat d’une nouvelle session.
- `AgentUpdateNotificationPublisher` (recette R2-034) notifie l’issue d’une mise à jour
  (`agent-update.completed` à la confirmation, `agent-update.failed` à l’échec) aux abonnés
  des projets du serveur, en notifications utilisateur uniquement.

## Garanties

Les contrôleurs restent minces et délèguent aux services. Les échanges inter-modules passent par les interfaces publiques et les DTOs de `Aetheus.Shared`; aucun autre module ne doit accéder directement aux détails de persistance de ce dossier.

Le `ServerTask` reste `Running` après le handoff. Le heartbeat conforme d’une
nouvelle session rend atomiquement la demande `Confirmed`, la tâche `Success`
et libère la réservation. Une tâche terminale avant handoff, une mauvaise
version ou l’expiration de confirmation terminent la demande en échec et
libèrent également la réservation.

## Tâche qu'aucun agent ne peut réclamer

Le claim (`TaskRepository.ClaimPendingTasksAsync`) ignore sans bruit une tâche dont
l'opération exige une capacité ou un protocole que l'agent du serveur ne déclare pas.
Il n'existe aucune politique de mise à jour automatique d'agent (une mise à jour est
toujours l'action d'un opérateur) : attendre ne ferait que rejouer l'attente. Le
coordinateur échoue donc la tâche (`TaskFailureCodes.AgentIncompatible`) avec une
raison en français (agent, version, capacité manquante, version cible, action
« Mettre à jour »), règle l'étape de pipeline et prévient le run. Garde-fous : tâche
plus ancienne que `AgentUpdateCoordinatorService.UnexecutableTaskGrace`, serveur en
ligne avec un bail de polling actif, serveur non réservé par une mise à jour en cours
(le travail fencé reprend avec le nouvel agent). Diagnostic : `AgentContractDiagnosis`,
qui réutilise le test du claim (`AgentCapabilities.IsDeclared`).
