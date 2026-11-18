# Loom community modules

Modules anyone can add to [Loom](https://github.com/antopilo/Loom), the desktop workspace for AI coding agents.
Install them in Loom from **Settings › Modules › Community**.

Each module here is reviewed in a pull request before it is merged. A module still runs with your rights, like any
program you install, so install only what you trust.

## How it works

```text
modules/<id>/module.json + *.cs ──► pull request ──► CI: rules, compile against Loom, id ──► review ──► merge
                                                                                                 │
Loom ◄── index.json (versions + SHA-256 of every file, at one commit) ◄── Publish workflow ◄─────┘
  │
  └─ Install / update: download the files at that commit, check each hash,
     write them to %LOCALAPPDATA%\Lokn\community\<id>\, compile against the running Loom, load.
```

- Modules are **source**. Loom compiles them itself, so you can read exactly what runs. A module that uses API
  your Loom does not have does not compile, and does not load.
- Installed modules **update automatically** when a higher `version` is merged. Turn this off in
  Settings › Modules › Community modules.

## Write a module

A module is one folder with a description and C# files:

```text
modules/
  pomodoro/
    module.json
    PomodoroModule.cs
    TimerContent.cs
    README.md        (optional)
```

```json
{
	"id": "pomodoro",
	"name": "Pomodoro",
	"author": "your-github-name",
	"description": "A focus timer in the sidebar.",
	"version": "1.0.0"
}
```

The code is a Loom source module: exactly one class that implements `ILoknModule` with a parameterless constructor,
whose `Id` is the folder name. It can use .NET, [Goo](https://github.com/obselate/goo) and the public API of
`Lokn.Core`; see Loom's [module guide](https://github.com/antopilo/Loom/blob/main/docs/modules.md).
[`modules/nowplaying`](modules/nowplaying) is a complete example.

To develop it with live reload, add this repository's `modules` folder to **Settings › Shell › Module folders** in
Loom. Loom compiles each module each time you save, and shows compile errors in its notice bar.

## Rules

| Rule | Why |
|---|---|
| Folder name = `id` = the module's `Id`: lowercase letters, digits, dashes | Loom installs into a folder of that name |
| Only `module.json`, `README.md` and `.cs` files, no subfolders, each under 1 MB | Reviewers see all the code; no binaries |
| Raise `version` with every change | Installed copies update only to a higher version |
| No hidden network use, no programs started, no code downloaded, unless the description says so | Users install on trust |
| One feature per module | Users turn modules on and off one by one |

Check your module the way CI does, with a Loom checkout next to this repository:

```powershell
pwsh ci/check.ps1 -Loom ..\Loom
```

## Maintainers

- `Check` runs `ci/check.ps1` on every pull request, against the latest Loom `main`.
- `Publish index` runs `ci/index.ps1` on every push to `main` and uploads `index.json` to the release tagged
  `index`. Do not delete that release.
- Review every line of a pull request: CI proves that a module compiles, not that it is safe.
