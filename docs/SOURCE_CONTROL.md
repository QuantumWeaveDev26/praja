# Source control

Repository: https://github.com/QuantumWeaveDev26/praja

Track application source, migrations, synthetic automated tests, package lock files,
scripts and reviewed documentation. Runtime data belongs only in the local workspace
or a separately managed backup.

## Before each commit

1. Run `git status --short` and review the intended changes.
2. Run `./test.ps1` for application changes and record the actual result.
3. Stage explicit source paths, then inspect `git diff --cached --stat` and
   `git diff --cached`. Verify that configuration contains placeholders, not secrets.
4. Commit a cohesive change with a descriptive message; push without force.

Never commit `App_Data`, databases, protection keys, OAuth credentials, API keys,
personal screenshots, local handoff notes, generated reports or SDK/dependency
caches. `.gitignore` excludes these locations. It does not remove files already
tracked, so inspect the staged change every time. Do not force-add ignored files.

The local `output` directory remains available for detailed test reports and
workspace preservation checks. Publish a reviewed, data-free summary under `docs`
when shared documentation is needed. Links to `output` in older local documentation
refer to local artifacts and are not included in GitHub.

## Collaborating

Use branches for new work. Fetch before integrating another contributor's changes.
Resolve conflicts with tests, preserve existing remote history and avoid force pushes.
Keep unrelated changes in separate commits. GitHub repository visibility and branch
protection should be selected by the repository owner.

## Fresh checkout

Install the .NET SDK selected by `global.json` and Node.js for browser-unit tests,
then run `./run.ps1` and `./test.ps1` from PowerShell. Scripts fall back to an installed
.NET SDK when the optional local `.tools` SDK is absent. First startup creates a new
local workspace; existing operators, cases and credentials are intentionally not
distributed in source control. Configure integration credentials through the local
application settings or the deployment's managed secrets.
