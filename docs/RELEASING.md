# Branches, versions and releases

## Branches

| Branch | What it holds | Build version |
|---|---|---|
| `main` | Released code. Every release is a `v*` tag on `main`. | `1.0.0` at the tag |
| `develop` | The next release, integrated. Feature branches merge here. | `1.0.2` (2 commits after `v1.0.0`) |
| anything else | A feature in progress, branched from `develop`. | `1.0.2-SNAPSHOT` |

`main` and `develop` only change through pull requests, and a pull request can't merge until the **Test** check
(the Build workflow's test job) passes. A GitHub ruleset enforces this with no bypass, so push a branch and open a
pull request instead of pushing to `main` or `develop`. Tags aren't affected.

## Where the version comes from

Nothing to edit by hand: `Directory.Build.targets` works the version out from git when you build.

- **Base:** the newest tag that looks like `v1.0.0` or `v1.0` in the commit's history. Before the first tag it's
  `VersionFallback` (0.1.0), with every commit counted.
- **Patch:** the tag's patch number plus how many commits came after the tag. Every new commit on `develop` gets a
  higher number than the one before.
- **Suffix:** `-SNAPSHOT` on any branch other than `main` and `develop`.
- **Build metadata:** the branch and commit after a `+` (`1.0.2-SNAPSHOT+fly-this-mission.0dc323a`). The About box
  shows the whole thing. Builds that aren't releases also put the branch and version at the end of the window title
  (`[develop 1.0.2]`).

A version is worked out per commit, so two machines building the same commit get the same number. To see what
the version would be without building:

```
dotnet msbuild -t:PrintVersion -nologo src/KspFlightPlanner.App
```

CI checkouts have no branch checked out, so there the branch is read from GitHub's variables. Pass
`-p:VersionBranch=name` to choose the branch yourself.

## Local builds

`pwsh tools/publish.ps1` publishes into `publish/<branch>/` (or `publish/v1.0.0/` at a release tag), so builds from
different branches sit side by side.

## Making a release

1. Open a pull request from `develop` into `main` and merge it once **Test** passes.
2. Tag the merged `main` and push the tag:
   ```
   git switch main && git pull
   git tag v1.1.0
   git push origin v1.1.0
   ```
3. The **Release** workflow (`.github/workflows/release.yml`) tests and builds that commit, then publishes a GitHub
   Release with `KspFlightPlanner-1.1.0.exe` and notes generated from the commits. A tag with a dash
   (`v1.1.0-rc.1`) is published as a pre-release. A tag that isn't on `main` is refused.
4. Open a pull request from `main` into `develop` and merge it, so `develop` counts from the new tag (`1.1.1`, `1.1.2`, …).

Choosing the number: bump the minor version (`v1.1.0`) for new features and the major version (`v2.0.0`) for big
changes. A fix released straight from `main` can take the next patch number (`v1.1.1`).

## CI builds

The **Build** workflow (`.github/workflows/build.yml`) runs on pushes to `main`/`develop` and on pull requests:
the **Test** job builds the whole solution and runs the tests (results are summarized on the run page), then the
**Publish** job keeps the exe as a workflow artifact named after its version (`KspFlightPlanner-1.0.2.exe`, not zipped).
