---
name: architect
description: Record an architecture decision as an ADR in docs/adr, with alternatives and consequences. Use when introducing a component, changing a dependency or making a trade-off.
---

# Write an ADR

1. Find the next number: list `docs/adr/` and add one to the highest.
2. Copy `docs/adr/0000-template.md` to `docs/adr/NNNN-short-title.md`.
3. Fill in Context (the forces, with facts and links), Decision (one or two sentences first), Alternatives (a table: option, why not) and Consequences (what gets easier, harder, and what must now be done).
4. Set Status to `proposed`. Only the owner changes it to `accepted`.
5. If the decision changes the architecture, update `docs/architecture.md` in the same PR, including the Mermaid diagram.
6. If it supersedes an ADR, set the old one's status to `superseded by NNNN`.

Keep an ADR under one page. A decision you can't explain in a page is two decisions.
