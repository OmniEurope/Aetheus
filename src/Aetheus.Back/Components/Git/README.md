<!-- SPDX-License-Identifier: EUPL-1.2 -->
# Git

External git connections (GitHub/GitLab), internal hosted repositories (GitLight), pull requests, branch policies, branch protection rules, smart HTTP clone/push, file browsing, blame, and commit graph.

## API Surface

| Endpoint | Method | Auth | Description |
|----------|--------|------|-------------|
| `/api/git/connections` | GET | User | List connections for a project |
| `/api/git/connections/{id}` | GET | User | Connection detail |
| `/api/git/connections` | POST | User | Create connection |
| `/api/git/connections/{id}` | PUT | User | Update connection |
| `/api/git/connections/{id}` | DELETE | Admin | Delete connection |
| `/api/git/connections/{id}/pull-requests` | GET | User | List external PRs |
| `/api/git/connections/{id}/pull-requests/sync` | POST | User | Sync external PR data |
| `/api/git/connections/{id}/branch-policies` | GET | User | List branch policies |
| `/api/git/branch-policies` | POST | User | Create branch policy |
| `/api/git/branch-policies/{id}` | PUT | User | Update branch policy |
| `/api/git/branch-policies/{id}` | DELETE | Admin | Delete branch policy |
| `/api/git/status-report` | POST | User | Record pipeline status locally (returns `{ recorded, forwardedToProvider }`; provider forwarding not yet wired, so `forwardedToProvider` is always `false`) |
| `/api/git/repos` | GET | User | List internal repos; optional `?projectId=` filter (omitted = every repo across the projects the caller can read) |
| `/api/git/repos` | POST | User | Create internal repo |
| `/api/git/repos/filter-values` | GET | User | Default branches offered by the repository list's checkable column filter; optional `?projectId=` (same scope as the list) |
| `/api/git/repos/{id}` | GET/PUT/DELETE | User/Admin | Read, update, or delete internal repo |
| `/api/git/repos/{id}/commits` | GET | User | Commit log (paginated); grid column filters `Message` (contains), `AuthorName`, `SourceRef` (branches walked) and `CommitDate` (range) through an allow-list, any other column or operator refused; two filters on one column sharing no value are refused (400) |
| `/api/git/repos/{id}/archive` | GET | User | Repository as a zip streamed from `git archive`; optional `?ref=` (default branch when omitted), file named `<slug>-<ref>.zip`; 404 when the ref does not exist, 400 for a ref starting with `-` |
| `/api/git/repos/{id}/filter-values` | GET | User | Authors offered by the commits and pull requests grids' Author filters |
| `/api/git/repos/{id}/commits/{sha}` | GET | User | Single commit detail + file-level diff (against the first parent, or the empty tree for a root commit) |
| `/api/git/repos/{id}/commit-messages` | POST | User | Resolve 1 to 200 commit SHAs into their messages in one request |
| `/api/git/repos/{id}/branches` | GET/POST | User | List/create branches |
| `/api/git/repos/{id}/branches/{name}` | DELETE | User | Delete branch |
| `/api/git/repos/{id}/tags` | GET/POST | User | List/create tags |
| `/api/git/repos/{id}/tags/{name}` | DELETE | User | Delete tag |
| `/api/git/repos/{id}/tree` | GET | User | File tree browser |
| `/api/git/repos/{id}/blob` | GET | User | File content |
| `/api/git/repos/{id}/blob/raw` | GET | User | Raw file download |
| `/api/git/repos/{id}/blame` | GET | User | Git blame |
| `/api/git/repos/{id}/graph` | GET | User | Commit graph (text/ASCII lanes) |
| `/api/git/repos/{id}/graph-data` | GET | User | Structured commit graph for the SVG lane view |
| `/api/git/repos/{id}/pull-requests` | GET/POST | User | Internal PRs |
| `/api/git/repos/{id}/pull-requests/{n}` | GET | User | Detail d'une pull request interne |
| `/api/git/repos/{id}/pull-requests/{n}/merge` | POST | User | Merge PR |
| `/api/git/repos/{id}/pull-requests/{n}/close` | POST | User | Close PR |
| `/api/git/repos/{id}/pull-requests/{n}/diff` | GET | User | PR diff |
| `/api/git/repos/{id}/branch-protection` | GET/POST | User/Admin | List or create protection rules |
| `/api/git/repos/{id}/branch-protection/{ruleId}` | PUT/DELETE | User/Admin | Update or delete one protection rule |
| `/git/{projectId}/{slug}.git/*` | GET/POST | BasicAuth | Smart HTTP clone/push with account credentials, owner-bound PATs, or run-scoped clone tokens |

## Key Classes

- `GitController` -- external connections, PRs, branch policies, status reports
- `GitLightController` -- internal repo CRUD, commits, branches, tags, tree, blame, PRs
- `GitSmartHttpController` -- smart HTTP transport (info/refs, upload-pack, and receive-pack with a scoped 512 MiB request limit)
- `IGitService` / `GitService` -- external connection logic
- `IGitLightService` / `GitLightService` -- internal repo logic
- `IGitSmartHttpService` / `GitSmartHttpService` -- smart HTTP protocol + streamed extraction of updated branch refs + `.pipeline/` YAML auto-sync before webhook branch-filter evaluation + real-time pipeline change notification after each synced create/update + HEAD auto-fix after push (detects default branch, updates HEAD on main/master mismatch)
- `GitUnifiedDiffParser` -- pure parser turning a `git diff --patch` unified diff into the structured `PullRequestDiffDto` (per-file hunks + add/del counts); also exposes the SHA validation and empty-tree-SHA constant used by the commit-detail endpoint
- `IGitLightCliService` / `GitLightCliService` -- shells out to `git` binary via the `GitProcessRunner` collaborator; write operations (commit/push) delegated to `GitLightCliWriter`; `CommitFileAsync` commits a file to a bare repo via a throwaway temp clone + push (used by git-strict project pipelines, ADR-015)
- `IGitRepository` / `GitRepository` -- external connection EF access
- `IGitLightRepository` / `GitLightRepository` -- internal repo EF access
- `GitBasicAuthenticationHandler` -- HTTP basic auth for smart HTTP via account credentials, owner-bound personal access tokens, or project-scoped pipeline-run clone tokens. `PatScopeEnforcementMiddleware` explicitly permits POST `git-upload-pack` for read-only PAT clone/fetch while denying `git-receive-pack` and unrelated writes.
- `GitLightMaintenanceService` (in `Services/`, outside this module) -- background: `git gc`, housekeeping
- `IExternalRepoMirrorService` / `ExternalRepoMirrorService`, `CreditedGitRunner`, `GitCredentialPayload` -- mirror mechanics for external repositories (clone, fetch, credentials), moved here from ExternalRepos so the orchestrator can reach them without depending on that module
- `IExternalMirrorRefresher` / `ExternalMirrorRefresher` -- on-demand fetch of the mirror behind a repository slug; returns null on success or for a non-mirror repository, else the fetch failure reason. Called by `PipelineWorkspaceSourceResolver` just before a run pins the commit of a pipeline whose YAML names that mirror (`source: repository:`, recette R-534)
- `IGitBranchAdvanceService` / `GitBranchAdvanceService` -- fast-forwards a branch of a project's internal repository onto a commit with `git update-ref <ref> <new> <old>` (compare-and-swap, ancestry proven first, never a history rewrite), reporting `Advanced`, `AlreadyUpToDate`, `AlreadyContains`, `NotFastForward`, `Protected`, `CommitNotFound` or `Refused` (ADR-044); called by the Pipelines `advance-branch` step
- `GitBranchWritePolicy` -- decision D13 (BACK-013): a rule's `BranchProtectionLevel` says who may move a matching branch (strictest matching rule wins). `NotProtected`: anyone with write access, and pipelines. `ProtectedExceptPipelines`: people only through a pull request (the smart HTTP push guard refuses a direct push before git runs), the `advance-branch` step may still advance it. `StrictlyProtected`: only a merged pull request, so `advance-branch` returns `Protected` and fails its step. A pull-request merge is allowed at every level; deletion and force-push stay with the rule's own switches and the pre-receive hook. Rules that required a pull request before D13 were migrated to `ProtectedExceptPipelines`, the others to `NotProtected`
- `GitArchiveService` -- zip download of a hosted repository (R2-003): resolves the ref (default branch when omitted), refuses one git would read as an option, and streams `git archive` with the slug as the top folder
- `GitCommitListQuery` / `GitCommitLogFilter` / `GitCommitLogReader` -- the commits grid's column filters (R-224, R2-004, R2-005): the allow-list turning grid filters into `git log` arguments (`--grep`, `--author`, start refs, `--since`/`--until`), and the reader that walks history for the page, its total and the author list with one shared argument builder
- `GitFilterValuesService` -- the values the git grids' checkable column filters offer (default branches across readable repositories, authors of one repository's commits and pull requests)
- `GitRunCloneToken` -- stateless HMAC-SHA256 clone credential minted per pipeline run (6 h lifetime, scoped to one project); signed with `GitLight:RunTokenKey` (fallback `Auth:EncryptionKey`) and accepted under either secret, so both blue-green colours accept what the other minted during a key change
- `GitDiffReader` -- bounded diff queries extracted from `GitLightCliService` (numstat, patch, changed paths, and `GetTreeBlobsAsync`, the full `path -> "mode sha"` tree listing used to compare the definition repository with the source repository); a truncated or failed answer is null, never a partial list
- `GitLightMapper.IsAdditionalSource` -- marks a mirror that sits beside the project's own repository (exposed as `GitLightRepoDto.IsAdditionalSource`)
- `GitRepoPathResolver` -- single source of truth for resolving an internal repo's on-disk path from `(projectId, slug)` with the path-traversal guard (OS-aware comparator: case-insensitive on Windows, case-sensitive on Linux). Replaces the three drifted copies in `GitLightService` / `GitSmartHttpService` / `ExternalRepoMirrorService` (M-git-6)

## Cross-Module Dependencies

- Depends on: Audit, Pipelines (status reporting), Auth (basic auth handler), PersonalAccessTokens, Webhooks
- Depended on by: ExternalRepos (mirror-as-internal-repo at the standard path; always on since recette R-295, ADR-018/020); Pipelines (`IExternalMirrorRefresher` and `GetTreeBlobsAsync` for the `source:` block, `IGitBranchAdvanceService` for the `advance-branch` step)
