# Claude Code tooling for Morph

Budget: 5 or fewer project plugins/MCPs. Everything below runs locally except the Learn MCP's doc queries.

| Tool | Kind / scope | Why | What its hooks run | Remove |
|---|---|---|---|---|
| Microsoft Learn MCP (`microsoft_docs_mcp`) | HTTP MCP, project (`.mcp.json`) | Official docs for Win32, WPF, .NET, MSIX, WinRT; ask it before guessing an API | none (sends only search queries to learn.microsoft.com) | `claude mcp remove microsoft_docs_mcp -s project` |
| Ponytail 5.3.3 (DietrichGebert/ponytail, MIT) | Plugin, project | Pushes toward minimal code: stdlib first, no unrequested abstractions | SessionStart: injects ruleset + a file map built from `git ls-files`. SubagentStart: same rules for subagents. UserPromptSubmit: tracks mode, writes a local CSV ledger in `~/.config/ponytail/`. Optional webhook is **left empty**, so nothing leaves the machine | `claude plugin uninstall ponytail@ponytail --scope project` |
| caveman 4.0.1 (JuliusBrussee/caveman, Apache-2.0) | Plugin, project | Shorter agent replies, fewer output tokens | SessionStart/SubagentStart: injects terse-style rules. UserPromptSubmit: tracks `/caveman` mode. SessionEnd: writes local token stats. All local Node scripts, no network | `claude plugin uninstall caveman@caveman --scope project` |
| FlaUI-MCP 0.2.0 (shanselman/FlaUI-MCP, MIT) | stdio MCP, local scope | Drives and screenshots the wheel/editors through UI Automation; same FlaUI stack as `tests/Morph.UiTests` | none (local exe) | `claude mcp remove flaui -s local`, delete `%LOCALAPPDATA%\Tools\FlaUI-MCP` |

## Considered and skipped
- **claude-mem**: runs a background worker (Bun, Chroma) and sends session transcripts to the model for compression. It duplicates the built-in memory, `CLAUDE.md` and `docs/PLAN.md`, and adds per-turn overhead.
- **Windows-MCP (CursorTouch)**: broader than we need (shell, registry); FlaUI-MCP covers UI testing.

## User-level items (outside this project's budget)
- Disabled for this machine: `linkedin-skills`, `ui-ux-pro-max`. `linkedin-skills@synced` still loads from claude.ai; disable it at claude.ai → Settings if unused.
- Still loading: `claude-code-setup` plugin and the claude.ai connectors (Notion, Gmail, Drive, Calendar, HyperFrames, Firecrawl, Claude Docs). Disable unused connectors at claude.ai → Settings → Connectors.

## Context overhead
Run `/context` in a fresh session and record the numbers here. If a plugin isn't used in a phase, disable it with `claude plugin disable <name> --scope project`.
