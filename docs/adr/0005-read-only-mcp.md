# ADR 0005: The MCP server exposes read-only tools only

Status: Accepted

## Context
AI assistants (Copilot, chatbots) should be able to answer "what is the state of case X" and "which proposals still need a decision". Letting a model call tools that change state means a prompt injection or a wrong guess can alter business data under the user's identity.

## Decision
The MCP server exposes exactly two tools: `get_case(caseId)` and `get_open_proposals(caseId)`. It is built on `ICaseReader`, not `ICaseStore`, so it cannot write by construction (a test asserts this and the read-only tool attributes). Every call is authenticated and the same ownership rule as the API applies (`CaseAccess.CanRead`): a case the caller may not read looks the same as a case that does not exist. Shadow proposals are never returned. State changes (answers, take over) go only through the Case API with the proper role.

## Consequences
- The assistant looks things up; the API, authorised by role, decides and writes.
- A compromised or confused assistant cannot confirm, change or reject a value.
- Assistants cannot complete a task end to end; a person uses the reviewer app for actions.
- Adding a write tool later is a deliberate change that needs its own ADR, role and audit event.
