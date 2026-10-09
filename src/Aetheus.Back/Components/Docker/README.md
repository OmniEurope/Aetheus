<!-- SPDX-License-Identifier: EUPL-1.2 -->
# Docker

Docker management per server: containers, images, networks, volumes, compose stacks, resource limits, container inspection, env vars, file browsing, image builds, prune, and shell exec.

## API Surface

| Endpoint | Method | Auth | Description |
|----------|--------|------|-------------|
| `/api/servers/{serverId}/docker/containers` | GET | User | List containers |
| `/api/servers/{serverId}/docker/action` | POST | User | Container action (start/stop/restart/remove) |
| `/api/servers/{serverId}/docker/containers/logs` | POST | User | Container logs |
| `/api/servers/{serverId}/docker/containers/{id}/inspect` | POST | User | Inspect container |
| `/api/servers/{serverId}/docker/containers/{id}/env` | POST | User | Container env vars |
| `/api/servers/{serverId}/docker/containers/browse` | POST | User | Browse container filesystem |
| `/api/servers/{serverId}/docker/images` | GET | User | List images |
| `/api/servers/{serverId}/docker/images/pull` | POST | User | Pull image |
| `/api/servers/{serverId}/docker/images/{id}` | DELETE | Admin | Remove image |
| `/api/servers/{serverId}/docker/build` | POST | Admin | Build image (typed `DockerBuildImage` operation) |
| `/api/servers/{serverId}/docker/networks` | GET | User | List networks |
| `/api/servers/{serverId}/docker/volumes` | GET | User | List volumes |
| `/api/servers/{serverId}/docker/compose` | GET | User | List compose stacks |
| `/api/servers/{serverId}/docker/compose/action` | POST | User | Compose action (up/down/restart) |
| `/api/servers/{serverId}/docker/compose/{stack}/file` | GET | User | Read compose file |
| `/api/servers/{serverId}/docker/compose/file` | PUT | Admin | Save compose file and deploy the stack (typed `DockerComposeUp` operation) |
| `/api/servers/{serverId}/docker/prune` | POST | Admin | Prune unused resources |
| `/api/servers/{serverId}/docker/resource-limits` | POST | User | Update resource limits |
| `/api/servers/{serverId}/docker/exec` | POST | Admin | Shell exec in container |

## Key Classes

- `DockerController` -- thin controller with `ValidateServerExistsFilter`
- `IDockerService` / `DockerService` -- business logic, delegates to agent tasks
- `CachedDockerService` -- in-memory cache decorator
- `IDockerRepository` / `DockerRepository` -- EF data access
- Compose deploy, exec and build send their payload base64-encoded in an env var of a typed operation (`DockerComposeUp`, `DockerExecScript`, `DockerBuildImage`), executed by the agent's `DockerPayloadOperationExecutor`
- `DockerIdValidator` -- validates container/image ID format

## Cross-Module Dependencies

- Depends on: Audit, Shared (`ValidateServerExistsFilter`)
- Depended on by: (none)
