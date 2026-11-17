<!-- SPDX-License-Identifier: EUPL-1.2 -->
# Tasks

Distributed task queue for agent-executed work: free-form shell commands, typed operations, agent claim/start/complete lifecycle, cancellation, and timeout.

## Clôture des déploiements

La clôture d'une tâche de déploiement valide les identifiants d'artefact et de release propagés
par la pipeline. Une erreur ou une annulation de cette clôture fait échouer l'étape et avance son
état avant les notifications temps réel effectuées au mieux : aucune notification défaillante ne
peut masquer le résultat réel de la tâche.

## API Surface

| Endpoint | Method | Auth | Description |
|----------|--------|------|-------------|
| `/api/tasks` | GET | User | List tasks (paginated, filterable) |
| `/api/tasks/active` | GET | User | In-flight tasks (for top-bar tracker) |
| `/api/tasks/{id}` | GET | User | Task detail |
| `/api/tasks/{uid}` | GET | User | Task detail by public identifier (ADR-060), same Server Read check |
| `/api/tasks` | POST | ServerAdmin | Create free-form command task |
| `/api/tasks/operation` | POST | User | Create typed operation task |
| `/api/tasks/statuses` | POST | AgentToken | Bulk status query for task reconciliation |
| `/api/tasks/claim` | POST | AgentToken | Agent claims pending tasks |
| `/api/tasks/{id}/start` | POST | AgentToken | Agent marks task running |
| `/api/tasks/{id}/complete` | POST | AgentToken | Agent reports task result |
| `/api/tasks/{id}/deployment-build-refusal` | POST | AgentToken | Report a deployment build refusal |
| `/api/tasks/{id}/cancel` | POST | User | Cancel a task |
| `/api/admin/task-identity-backfill` | GET | Admin | Tasks left to fill by the ADR-060 backfill |

## Key Classes

- `TasksController` -- thin controller, RBAC + agent-token gated
- `ITaskService` / `TaskService` -- task lifecycle, operation dispatch
- `ITaskRepository` / `TaskRepository` -- EF data access (with `TaskLeaseRepository` and `TaskQueuePersistence` collaborators)
- `TaskCompletionFinalizer` -- shared completion path (result persistence, pipeline notification)
- `TaskEnvProtection` -- filters the environment handed to agent task processes
- `SelfUpdateTaskHandoff` / `AgentTaskProtocolCompatibility` -- agent update handoff and protocol gating
- `PipelineTaskFailureClassifier` -- maps task failures to pipeline failure categories
- `TaskTimeoutService` (in `Services/`, outside this module) -- background: expires stale tasks

## Cross-Module Dependencies

- Depends on: Audit, Pipelines (pipeline-run context for operation tasks)
- Depended on by: AiTasks, Logs, Servers (task listing per server)
