# Module Users

## Responsabilité

Gère les comptes utilisateurs et leurs opérations administratives.

## Points d’entrée

- API ou consommateur principal : `UsersController.cs`.
- Enregistrement DI : `UsersModuleExtensions.cs`.
- Les interfaces `I*` définissent les contrats du module; les services portent la logique et les repositories l’accès persistant lorsqu’il existe.

## Limites

Les contrôleurs restent minces et délèguent aux services. Les échanges inter-modules passent par les interfaces publiques et les DTOs de `Aetheus.Shared`; aucun autre module ne doit accéder directement aux détails de persistance de ce dossier.

## Désactivation ou suppression d’un propriétaire de pipelines (recette R1-49)

Un utilisateur inactif ne détient aucun rôle : la porte F-EXEC-1b refuse alors toutes les exécutions chaînées des
pipelines dont il est le propriétaire (`Pipeline.CreatedByUsername`). `UserPipelineOwnershipGuard` refuse donc
(409, pipelines nommés) la désactivation (`PUT api/users/{id}`) ou la suppression (`DELETE api/users/{id}`) d’un
propriétaire, sauf si la requête nomme un utilisateur actif qui reprend les pipelines
(`TransferPipelinesToUsername`, ou `?transferPipelinesTo=` pour la suppression) ou force l’opération
(`ForceWithOwnedPipelines`, `?force=true`, audité `DeactivatedWithOwnedPipelines` / `DeletedWithOwnedPipelines`).
Le transfert est demandé par l’événement `UserPipelinesTransferRequestedEvent`, diffusé strictement et traité par le
module Pipelines (couche supérieure) ; la liste des pipelines est relue ensuite, et l’opération est refusée s’il en
reste. Tous les refus sont décidés avant toute modification du compte.
