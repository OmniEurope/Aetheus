# Module AiTasks

## Responsabilité

Gère les tâches IA (ADR-023, amendement (b) du 2026-10-11) : profils d’API IA (`AiRunnerProfileService`,
clé chiffrée, test de connexion), cycle de vie des runs IA dispatchés aux agents, qui appellent l’API du
fournisseur, et planification en arrière-plan.

## Points d’entrée

- API ou consommateur principal : `AiTasksController.cs`.
- Enregistrement DI : `AiTasksModuleExtensions.cs`.
- `AiTaskSchedulerService` : service d'arrière-plan qui planifie les runs.
- `AiModelListRefreshService` : service d'arrière-plan qui relit environ une fois par semaine (réveil nocturne, 01:00 à 05:00 UTC, profils non lus depuis 6,5 jours) la liste des modèles de chaque profil API chez son fournisseur, avec sa clé, et la garde sur le profil (recette R3-7). Coupé quand `AiTasks:ModelListRefresh:Enabled` vaut `false` (hôtes de test, E2E et QA) ; actif par défaut.
- `AiModelListReader` : lecture d'une liste chez le fournisseur et enregistrement du résultat sur le profil (liste filtrée aux modèles de chat pour OpenAI et compatibles, `AiChatModelFilter`), partagée par la tâche nocturne et l'action « Relire maintenant » (`POST api/ai/profiles/{id}/model-list/refresh`, administrateur, une lecture par minute et par profil).
- Les interfaces `I*` définissent les contrats du module; les services portent la logique et les repositories l’accès persistant lorsqu’il existe.

## Limites

Les contrôleurs restent minces et délèguent aux services. Les échanges inter-modules passent par les interfaces publiques et les DTOs de `Aetheus.Shared`; aucun autre module ne doit accéder directement aux détails de persistance de ce dossier.
