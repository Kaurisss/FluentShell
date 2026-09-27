# Project agent configuration

Read this document when maintaining agent instructions or diagnosing their loading. It is not a task-start checklist.

## Sources and design

This refactor follows the official [Rethinking skills and prompts for GPT-6 Astra](https://developers.openai.com/blog/rethinking-skills-and-prompts-for-gpt-6-astra) article and [current model guidance](https://developers.openai.com/api/docs/guides/latest-model), article fetched again on 2026-09-27. The applicable guidance is to shorten Skill descriptions, use minimal routers with progressive disclosure, remove redundant scaffolding/testing prompts, and state safe workflow authority and completion clearly.

The [official Skills documentation](https://developers.openai.com/codex/skills) explains description-based selection and on-demand loading. The [AGENTS.md guide](https://developers.openai.com/codex/guides/agents-md) explains instruction discovery and precedence. The project does not need a hard-coded model name, reasoning setting, or larger context limit to apply these practices.

## Instruction ownership

| Surface | Responsibility |
| --- | --- |
| [AGENTS.md](../AGENTS.md) | Project constraints, local authority, task routes and completion |
| [CLAUDE.md](../CLAUDE.md) | Imports AGENTS.md without copying its rules |
| [.agents/skills](../.agents/skills) | Canonical project Skills and their references/scripts |
| `.claude/skills/<name>` | Relative directory links to the canonical Skill, preserving Claude discovery |
| [Project guide](agent-project-guide.md) | Optional architecture and implementation context |
| [CONTEXT.md](../CONTEXT.md) | Optional Chinese domain terminology |
| [BACKLOG.md](../BACKLOG.md), `docs/specs/` | Feature-specific planning/history |
| [Icon brief](fluent-icon-prompt-research.md) | Prompt for product-launch artwork only |
| `winui-app/agents/openai.yaml` | Skill UI metadata and a prompt for the requested WinUI change |
| `.trae/rules/git-commit-message.md` | Commit-message format, selected only for that task |
| `.claude/settings.local.json` | Local allow entries for build, test, restore and guidance validation |

Host/system permissions remain authoritative. A Skill cannot grant access to production or real SSH targets, reinterpret test fixtures as instructions, or override the user's task. Safe local implementation, tests and fixes use the authorization stated in AGENTS.md.

The only nested AGENTS.md is the security reference maintenance note. The former generated aggregate duplicated all 28 rule categories; comparison found no unique rule content in the former aggregate. Maintain individual rules instead of rebuilding the aggregate.

## Maintenance

Keep descriptions focused on the operation that needs the Skill. For example, changing SSH trust triggers security guidance; merely running a local test does not. A WinUI binding review selects winui-code-review, while implementing a layout uses winui-app. Mentioning “server” in copy does not require an icon lookup or infrastructure audit.

Put workflow details in a named reference and link to it from the Skill router. Resolve resource links relative to the file containing the link; resolve script paths relative to the canonical Skill directory. Prefer a single source for rules shared by multiple hosts.

Claude links use relative targets such as `../../.agents/skills/run-tests` from `.claude/skills/`. If copying the workspace loses symlinks, recreate them using the host's supported directory-link mechanism and rerun the validator. Do not restore full editable copies. Local Git excludes currently hide AGENTS.md, CLAUDE.md, CONTEXT.md, `.agents/`, `.claude/`, `.trae/`, and `docs/specs/`; their disk changes still apply, but sharing them requires explicitly including those files in version control. This audit preserves those local exclude settings.

Do not infer that an already-running session replaced instructions loaded at startup. Start a new session if the host still shows an older description or AGENTS.md; no model setting needs to be changed.

## Validation

From the repository root:

```bash
python .agents/scripts/validate_guidance.py
```

The Python 3.9+ validator uses only the standard library. It checks this project's flat Skill frontmatter, unique names, relative Markdown routes, duplicate JSON keys, canonical Claude links (including a missing discovery directory), the CLAUDE import, and icon-script syntax. It discovers nested AGENTS/override files and project instruction/prompt files outside generated trees, and includes Trae rules. Lengths are reported for review, not enforced as arbitrary limits. It does not run application code or network requests.

For validator changes, run `python -B -m unittest discover -s .agents/scripts -p test_validate_guidance.py -v` as well. Fixtures are disposable and have no application or network access.

When changing YAML metadata or a bootstrap recipe, also parse it with a YAML parser and check the affected host's schema. When changing a script or command, exercise that path with disposable/local data. The test commands for this project are in [run-tests](../.agents/skills/run-tests/SKILL.md); they explicitly target the test project with x64.

For description changes, inspect representative positive and negative requests: a glyph lookup vs a logo task, a .NET runner failure vs test implementation, an SSH trust change vs a copy edit, a binding review vs a general logic review, and a WinUI startup issue vs documentation-only work. Trigger matching remains a model decision; structural validation cannot prove every future selection.

## 2026-09-27 audit

Scope: both AGENTS.md files, the Claude import and five aliases, all five canonical Skills and their supporting references/scripts/evals, WinUI metadata/bootstrap configuration, Trae commit prompt, project guide, terminology, artwork prompt, README, backlog and feature specification. Session logs, build output and installed user-global/plugin Skills are not project instructions and were left untouched.

- Shortened all five discovery descriptions to specific operations; retained task-based routers rather than adding model settings or startup reading requirements.
- Removed repeated WinUI review questionnaires and narrow decorative preferences. Profiling and sample lookup are evidence-driven; existing packaged behavior is not redesigned just to run an unpackaged check.
- Clarified safe local decision-making and completion in AGENTS.md, without granting server, publication, credential or machine-wide side effects. Unrelated baseline failures are reported rather than becoming an unlimited repair task.
- Corrected overbroad security guidance around parsing, trusted code, temporary files, crypto parameters and infrastructure exposure. Kept SSH host verification, credential protection, traversal defenses and terminal trust boundaries.
- Aligned icon evals with the actual requested artifact and explicit attribution of modified geometry; removed the incidental glyph requirement for SVG output.
- Added validator discovery/missing-directory regression checks. No application source, packaging, host permissions or model defaults changed.

Manual route review: an SSH host-key change selects security boundaries, a label edit selects terminology; a WinUI layout change selects implementation, a binding review selects review; a test-runner failure selects run-tests, writing a test alone does not; a glyph request selects icon lookup, a product logo selects the artwork brief. This is a semantic review, not an automated model-routing benchmark.

Verification: project validator passed (5 Skills, 69 guidance documents, 146 local links, 3 JSON files, 5 Claude aliases); five validator regression tests and all five skill-creator checks passed. PyYAML parsed all Skill frontmatter and the two existing WinUI YAML files. The unchanged machine-install recipe was not executed or re-certified against the remote DSC schema. Application build/runtime checks are not needed for this guidance-only change. Existing ignored configuration files remain local unless explicitly included in version control.
