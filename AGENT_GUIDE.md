# Agent Guide for Space Station 14

Welcome, Agent. This document provides the context and instructions you need to work effectively in this repository.

## 1. Getting Oriented

- **Repository**: Space Station 14 (SS13 Remake in C#).
- **Engine**: RobustToolbox (submodule).
- **Progress Log**: `agent-progress.txt` - Read this to see what previous agents have done.
- **Feature List**: `features.json` - The source of truth for what features exist and their status.

## 2. Setting Up the Environment

Before starting work, always verify the environment is healthy.

```powershell
./agent_init.ps1
```

This script will:
- Check dependencies (dotnet, python).
- Run `RUN_THIS.py` to update submodules and engine.
- Build the entire solution.

If this script fails, your first priority is to fix the environment.

## 3. Workflow for Agents

1. **Read Task**: Understand the user's request.
2. **Check Status**: Read `agent-progress.txt` and `features.json`.
3. **Plan**: Create an `implementation_plan.md`.
4. **Implement**: Write code.
5. **Verify**:
   - Run `./verify_game.ps1` to check both Client and Server builds efficiently.
   - Run `./agent_init.ps1` if you need a full environment check (dependencies, submodules).
   - Run specific tests related to your changes.
6. **Update Documentation**:
   - Update `features.json` if you implemented or fixed a feature (set `"passes": true`).
   - Append a new session entry to `agent-progress.txt`.

## 4. Key Commands

- **Build Server**: `dotnet build Content.Server`
- **Build Client**: `dotnet build Content.Client`
- **Run Tests**: `dotnet test Content.Tests`
- **Run Server**: `dotnet run --project Content.Server --config-file server_config.toml`
- **Run Client**: `dotnet run --project Content.Client`

## 5. Project Structure

- `Content.Server/`: Server-side game logic.
- `Content.Client/`: Client-side game logic.
- `Content.Shared/`: Logic shared between client and server (networking, prediction).
- `Resources/`: YAML prototypes, textures, and other assets.

## 6. Rules

- **Do NOT delete** `agent-progress.txt` or `features.json`.
- **Keep `features.json` up to date**.
- **Always run verification** before handing off.

## 7. Troubleshooting

- **Build Errors Truncated?** If `dotnet build` errors are cut off (e.g. "Build failed with X errors"), redirect output to a file to see the full log:
  ```powershell
  dotnet build Content.Server > build.log
  ```
  Then view `build.log`.

Good luck.
