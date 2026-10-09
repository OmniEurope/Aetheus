# Module Projects

## Responsabilité

Gère les projets et leurs relations avec dépôts, serveurs et pipelines.

## Points d’entrée

- API ou consommateur principal : `ProjectsController.cs`.
- Enregistrement DI : `ProjectsModuleExtensions.cs`.
- Le créateur d'un projet le suit dès sa création (`ProjectService.CreateProjectAsync`, recette
  R2-034), pour que les notifications du projet aient au moins un destinataire.
- Supprimer un projet supprime tout ce qui lui appartient (`ProjectDeletionService`, recette R2-122) :
  pipelines et exécutions (tâches, journaux, approbations, artefacts et leurs fichiers), environnements,
  applications supervisées et leur télémétrie, serveurs du projet (le lien seulement, jamais
  l'enregistrement du serveur), coffres, bibliothèques, connexions de service, flux de paquets, ports,
  abonnements, releases, analyses et dépôts Git. Une seule transaction, les grandes tables par lots ;
  refus (409) tant qu'une exécution du projet n'est pas terminée. `GET /api/projects/{id}/deletion-preview`
  compte ce qui partirait, et l'entrée d'audit reprend ces comptes.
- « Conservation des données » (PLAN-013 lot 4) : profil Léger, Standard ou Complet et valeurs propres
  (`ProjectDataRetentionService`, `GET/PUT /api/projects/{id}/data-retention`), lues par les purges de la
  télémétrie, des journaux et du détail d'analyse et par la configuration de la clé d'ingestion.
- Budget de stockage (PLAN-013 lot 5) : `ProjectStorageBudgetService` estime l'usage de chaque projet,
  alerte à 80 % et purge d'abord le détail le plus ancien au-delà du budget ; `GET /api/data-storage/projects`.
- « Récupérer la place » (PLAN-013 lot 6) : `VACUUM FULL` d'une table purgée à la demande d'un administrateur
  (`ReclaimSpaceService`, `GET/POST /api/data-storage/reclaim`), refusé pendant les pipelines, mesuré et journalisé.
- Les interfaces `I*` définissent les contrats du module; les services portent la logique et les repositories l’accès persistant lorsqu’il existe.

## Limites

Les contrôleurs restent minces et délèguent aux services. Les échanges inter-modules passent par les interfaces publiques et les DTOs de `Aetheus.Shared`; aucun autre module ne doit accéder directement aux détails de persistance de ce dossier.
