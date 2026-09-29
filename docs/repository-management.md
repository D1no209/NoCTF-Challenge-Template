# Repository management

This repository is managed through NoCTF GitOps.

Only `gitops.noctf.dev/v2` manifests are accepted. Definition and Rules map to
the NoCTF 0.3.0 typed contracts; legacy schema-version JSON is unsupported.

## Initialize

1. Create a Bot in NoCTF, grant it `Organizer`, and add it to the competition as
   a Manager.
2. Configure repository variable `NOCTF_API_URL`.
3. Configure repository secret `NOCTF_BOT_TOKEN`.
4. Open the **Initialize competition** Issue Form.
5. Review and merge the generated `initialize/competition` Draft PR.

The initialization Action checks the API URL, JWT authentication, competition
visibility, Competition ID, and Mode. If it fails, it updates a diagnostic
comment on the Issue. Fix the configuration and comment `/retry`, edit/reopen
the Issue, or dispatch the workflow with the Issue number.

The connectivity check is read-only. The Bot must retain Manager permission;
NoCTF enforces mutation permission again during Apply.

## Create challenges

Open the **Create challenge** Issue Form. The Action creates a
`<direction>/<slug>` branch, stable UUIDs, a challenge directory, a
`competition.yml` entry, and a Draft PR linked to the Issue.

After a PR is merged, Challenge CI validates and applies the desired state.
The README workflow rebuilds the challenge table and direction statistics from
`competition.yml` and each `challenge.yml`.

## Local commands

```bash
dotnet build .github/scripts/repository.cs
dotnet run --file .github/scripts/repository.cs -- validate
dotnet run --file .github/scripts/repository.cs -- self-test
dotnet run --file .github/scripts/repository.cs -- readme
dotnet run --file .github/scripts/repository.cs -- discover --base origin/main --head HEAD
dotnet run --file .github/scripts/repository.cs -- plan --base origin/main --head HEAD
```

`README.md` is generated after initialization. Edit competition metadata in
`competition.yml` and challenge metadata in the corresponding
`challenge.yml`; do not hand-edit the generated challenge table.

Main deployments use a non-canceling queue so every adjacent push diff is built
and applied. Do not force-push `main`.

## Branch protection

Protect `main`, require Challenge CI validation, and prohibit force pushes.
If direct GitHub Actions pushes are blocked by the repository ruleset, allow
the repository's `GITHUB_TOKEN` workflow to update only `README.md`, or merge
the generated README commit through the repository's normal review policy.
