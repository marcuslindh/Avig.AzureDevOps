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
Both consume the package from nuget.org (https://www.nuget.org/packages/Avig.AzureDevOps).

The package knows about neither of them. The consumers have their own ports and thin adapters
that translate the models.

## Commands

```powershell
dotnet build Avig.AzureDevOps.slnx
dotnet test Avig.AzureDevOps.slnx
./publish.ps1             # tests, packs and shape-checks; changes and copies nothing
./release.ps1 -DryRun     # the next release's version, nothing changed
./release.ps1 -Push       # set the version, verify, commit, tag and push: publish.yml publishes to nuget.org
```

`version.ps1` holds the version scheme, used by `release.ps1`. `.github/workflows/publish.yml`
runs on a `v*` tag and pushes through Trusted Publishing; the nuget.org policy names the file
`publish.yml`, so it must not be renamed. The workflow needs the repository secret `NUGET_USER`
(the nuget.org profile name).

## Rules

- **A released version must never be replaced.** nuget.org refuses, and `release.ps1` refuses a
  tag that already exists. The package is not put in `G:\LocalNugetRepo`; it was removed from
  there on 2026-10-31, when AI Code Review and Splice moved to nuget.org.
- **Versions count like an odometer, every digit 0-9:** 1.0.0, 1.0.1 ... 1.0.9, 1.1.0 ... 1.9.9,
  2.0.0. `release.ps1` raises one step per release, and nothing else changes the version; a
  breaking change gets no jump of its own. The first version on nuget.org is 1.0.1 (the tag
  v1.0.0 points at a commit that did not build on the runner and was never published).
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
- **No throw.** Every method answers with `Result`/`Result<T>`, all the way down: an argument is
  checked with an `if` that returns `ResultStatus.Invalid` with a message saying what is missing,
  and a failed call is passed on with `failed.WithoutValue().AsFailure<T>()` (`ResultFailure.cs`).
  `AzureDevOpsHttp` turns network errors and timeouts into `Unavailable` and HTTP status codes
  into statuses. Only constructors and `AddAvigAzureDevOps`, which cannot return a result, throw
  `ArgumentNullException`, and the caller's own cancellation passes through as an exception.
- **No magic strings in the public surface.** A value the API has a fixed set of names for is
  an enum, translated with `ApiEnum`; unknown values fall back to a neutral member. Only what
  the project defines itself (work item type, state, board column) stays a string.
- No magic numbers or strings: the field names are in `WorkItems/FieldNames.cs`, the limits as
  constants in the clients.
- File-scoped namespaces, braces always, CRLF.
- **Every method has XML documentation, public and private, and never `<inheritdoc/>`.** Every
  parameter has a `<param>`, in the signature's order. A class that implements an interface
  repeats the interface's documentation in full, and the two are kept in step when either changes.
- **Everything in the repo is in English**, in both `src` and `tests`: XML documentation (`///`),
  `//` comments, error messages, log messages and scripts. The XML documentation ships with the
  package and is shown in consumers' editors, and the error messages are shown to the user as
  they are. Sample data inside `<c>` may be in Swedish when that is what the code actually
  handles.
- `README.md`, `<Description>` and `<PackageReleaseNotes>` are in English too.
