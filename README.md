# Claude Manager

Claude Manager is an unofficial local Windows API gateway manager for Claude Code. It keeps API connections, keys, model mapping, quotas and local Gateway routing in one desktop app.

It is not affiliated with Anthropic.

## What is supported

- Anthropic Official and Anthropic-compatible providers, including Aerolink-style APIs.
- API connection setup with URL, auth scheme, custom safe headers, model list and default model.
- Local API key storage via Windows DPAPI.
- Local Gateway Mode: a loopback proxy on `127.0.0.1` for per-request routing, usage tracking and readable route stories.
- English, Ukrainian and Russian UI resources.
- Managed `claude` wrapper command that preserves cwd, arguments, console I/O and exit code.

OpenAI-compatible and Responses-compatible providers are labelled honestly as not supported yet unless a real tested adapter exists. The app does not claim OpenAI-to-Anthropic conversion, OAuth/account automation, cloud sync, MCP/A2A management, token compression, MITM certificates or remote VPS mode.

## Easiest way to run

Use the published folder as a whole:

```text
release\ClaudeManager.exe
```

Keep these files together in the same folder:

```text
ClaudeManager.exe
ClaudeManager.Wrapper.exe
ClaudeManager.Gateway.exe
```

First run:

1. Start `ClaudeManager.exe`.
2. Add an API connection and paste your API key.
3. Pick or add a model.
4. Let the app detect Claude Code, or select the real Claude Code path.
5. Start Gateway once.
6. Enable the managed `claude` command.
7. Restart your terminal and run `claude` from any project folder.

Claude Code talks to the local Gateway, so keys can rotate between requests without restarting Claude Code.

## Gateway routing and route stories

Gateway Mode keeps the real upstream key inside the local gateway. Claude Code only receives a local loopback URL and a local auth token.

The gateway records route decisions in SQLite as safe summaries:

- selected provider id;
- selected key id;
- requested/resolved/upstream model values;
- skipped candidates and reasons;
- retry policy, including the “no retry after streamed bytes” boundary.

Route traces do not store API keys, gateway tokens, Authorization headers, custom header values, prompt bodies or response bodies. Monitoring shows readable route stories instead of raw JSON.

## Model mapping

Models have separate fields:

- display name: friendly UI text;
- model value: actual upstream model id.

Display names are never sent upstream as model ids when a matching model record exists. Gateway routing resolves to `ModelValue`.

## Build and test

Requirements: Windows 10/11 x64 and .NET 8 SDK.

```powershell
dotnet restore AerolinkManager.sln
dotnet build AerolinkManager.sln --configuration Debug
dotnet test AerolinkManager.sln --configuration Debug
dotnet build AerolinkManager.sln --configuration Release
dotnet test AerolinkManager.sln --configuration Release
```

Publish a self-contained Windows build:

```powershell
.\scripts\publish.ps1
```

The default output is:

```text
release\ClaudeManager.exe
```

If `dotnet` is not in PATH:

```powershell
.\scripts\publish.ps1 -DotNetPath C:\path\to\dotnet.exe
```

The publish script also runs a headless smoke test for the published app.

## Local data

Default local data folder:

```text
%APPDATA%\ClaudeManager\
```

Typical files:

```text
config.json
state.json
usage.db
logs\
bin\claude.cmd
```

Do not commit local config, state, logs, database files or release folders. `.gitignore` is prepared for source/docs/assets/scripts/tests-only pushes.
