# CLAUDE.md

Knowledge project: ai-code-review
Documentation, decisions and the agent log are in Knowledge — see ~/.claude/CLAUDE.md. The
package has no project of its own there; the decision behind it is "Splice — Beslut: Delat
NuGet-paket för Azure DevOps med AI Code Review".

Guidance for Claude Code (claude.ai/code) in this repo.

## What this is

The NuGet package `Avig.AzureDevOps`: clients for the Azure DevOps REST API for pull requests,
work items, builds and the wiki, and sign-in with a personal access token that the application
supplies through a function. The code was broken
out of AI Code Review (`src/AiCodeReview.Infrastructure`) so that Splice can use it too.
Both consume the package from `G:\LocalNugetRepo`.

The package knows about neither of them. The consumers have their own ports and thin adapters
that translate the models.

## Commands

```powershell
dotnet build Avig.AzureDevOps.slnx
dotnet test Avig.AzureDevOps.slnx
./publish.ps1             # raises the version one step, tests, packs, shape-checks, copies to G:\LocalNugetRepo
./publish.ps1 -NoBump     # the same, without raising the version
./release.ps1 -DryRun     # the next release's version, nothing changed
./release.ps1 -Push       # set the version, verify, commit, tag and push: publish.yml publishes to nuget.org
```

`version.ps1` holds the version scheme, shared by both scripts. `.github/workflows/publish.yml`
runs on a `v*` tag and pushes through Trusted Publishing; the nuget.org policy names the file
`publish.yml`, so it must not be renamed. The workflow needs the repository secret `NUGET_USER`
(the nuget.org profile name).

## Rules

- **A package in the feed must never be replaced under the same number.** `publish.ps1` refuses.
- **Versions count like an odometer, every digit 0-9:** 1.0.0, 1.0.1 ... 1.0.9, 1.1.0 ... 1.9.9,
  2.0.0. `publish.ps1` raises one step per publish; a breaking change gets no jump of its own.
  The numbering started over at 1.0.0 on 2026-10-31, before the first publish to nuget.org;
  the earlier local 1.0.0-4.0.0 were removed from the feed and the NuGet cache.
- **The dependencies are an allow-list.** `publish.ps1` fails on a dependency that is not in
  `$allowedDependencies`. Analyzers must have `PrivateAssets=all`.
- **The package never stores the token.** The application passes a function that
  returns it (or its own `IAzureDevOpsCredential`); where it lives is the application's business.
  Nothing here touches the file system or encryption, and `ProtectedData` is not a dependency.
- **The logs' EventIds** are the same as when the code lived in AI Code Review (2600, 2900, 3000).

## Conventions

The set in `G:\OneDrive\SkunkWorks\Dev\2026\Medusa\docs\code-quality.md` applies:
Medusa's analyzers and versions in `Directory.Build.props` and all AVIG rules at `error`
(including AVIG0006 CRLF and AVIG0024 conditional expressions), with Avig.Analyzers from
nuget.org. Medusa.Analyzers is not used: it exists only in the local feed, and every package
must come from nuget.org so that the build runs on a GitHub runner. `.editorconfig` comes from
AI Code Review, where the code was written.

- Explicit types — never `var`. Explicit bool comparisons are allowed (`== false`).
- No `?:` — an `if` statement, a default followed by an `if`, or a small helper method.
  `dotnet format analyzers --diagnostics AVIG0024` does most of it but adds `else`
  after `return` and turns lambdas into block lambdas; clean up afterwards.
- Early return, at most two levels of nesting.
- **No throw in the public API.** Every public client method goes through `AzureDevOpsCall` and
  answers with `Result`/`Result<T>`; `AzureDevOpsException` is internal and only carries a failure
  to that boundary. Only the caller's own cancellation passes through as an exception.
- **No magic strings in the public surface.** A value the API has a fixed set of names for is
  an enum, translated with `ApiEnum`; unknown values fall back to a neutral member. Only what
  the project defines itself (work item type, state, board column) stays a string.
- No magic numbers or strings: the field names are in `WorkItems/FieldNames.cs`, the limits as
  constants in the clients.
- File-scoped namespaces, braces always, CRLF.
- **Everything in the repo is in English**, in both `src` and `tests`: XML documentation (`///`),
  `//` comments, error messages, log messages and scripts. The XML documentation ships with the
  package and is shown in consumers' editors, and the error messages are shown to the user as
  they are. Sample data inside `<c>` may be in Swedish when that is what the code actually
  handles.
- `README.md`, `<Description>` and `<PackageReleaseNotes>` are in English too.
