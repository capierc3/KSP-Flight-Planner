# Branches, versions and releases

## Branches

| Branch | What it holds |
|---|---|
| `main` | Released code. Every release is a `v*` tag on `main`. |
| `develop` | The next release, integrated. Feature branches merge here. |
| anything else | A feature in progress, branched from `develop`. |

`main` and `develop` only change through pull requests, and a pull request can't merge until the **Test** check
(the Build workflow's test job) passes. A GitHub ruleset enforces this with no bypass, so push a branch and open a
pull request instead of pushing to `main` or `develop`. Tags aren't affected.

## The version

The app's version is `<Version>` in `Directory.Build.props` (the .NET equivalent of a pom's `<version>`), shared by
every project. Every build of a commit has that version; the .NET SDK adds the commit after a `+`
(`0.2.0+0dc323a…`), which the About box shows.

- **Starting the next version:** raise `<Version>` in the branch that earns it (a new feature: `0.2.0` → `0.3.0`; a
  fix: `0.2.0` → `0.2.1`). `develop` has the new number once that branch merges.
- **Checking it:** `dotnet msbuild src/KspFlightPlanner.App -getProperty:Version`

## Local builds

`pwsh tools/publish.ps1` publishes into `publish/<branch>/`, so builds from different branches sit side by side.

## Making a release

1. Open a pull request from `develop` into `main` and merge it once **Test** passes.
2. Tag the merged `main` with its `<Version>` and push the tag:
   ```
   git switch main && git pull
   git tag v0.2.0
   git push origin v0.2.0
   ```
3. The **Release** workflow (`.github/workflows/release.yml`) tests and builds that commit, then publishes a GitHub
   Release with `KspFlightPlanner-0.2.0.exe` and notes generated from the commits. A tag that isn't on `main`, or
   doesn't match `<Version>`, is refused. For a pre-release, set `<Version>` to e.g. `0.2.0-rc.1` and tag `v0.2.0-rc.1`.
4. Open a pull request from `main` into `develop` and merge it, then raise `<Version>` for the next release.

Choosing the number: raise the minor version (`0.3.0`) for new features and the major version (`1.0.0`) for big
changes; a fix released straight from `main` can take the next patch number (`0.2.1`).

## CI builds

The **Build** workflow (`.github/workflows/build.yml`) runs on pushes to `main`/`develop` and on pull requests:
the **Test** job builds the whole solution and runs the tests (results are summarized on the run page), then the
**Publish** job keeps the exe as a workflow artifact named after its version (`KspFlightPlanner-0.2.0.exe`, not zipped).
