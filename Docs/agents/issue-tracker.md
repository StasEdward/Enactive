# Issue tracker: GitHub

Issues and specs live in GitHub Issues for StasEdward/Enactive.
Use the gh CLI from this repository.

- Read: gh issue view <number> --json number,title,body,labels,comments
- List: gh issue list --state open --json number,title,body,labels
- Create: gh issue create --title "<title>" --body-file <file>
- Comment: gh issue comment <number> --body-file <file>
- Label: gh issue edit <number> --add-label "<label>"
- Remove label: gh issue edit <number> --remove-label "<label>"
- Close: gh issue close <number>

Use a UTF-8 file for multiline bodies to preserve formatting and avoid
shell interpretation.

## Pull requests as a triage surface

PRs as a request surface: no.

GitHub issues and PRs share a number space. Resolve ambiguous references
with gh pr view <number>, then gh issue view <number>.

## Skill operations

“Publish to the issue tracker” means create a GitHub issue.
“Fetch the relevant ticket” means read its body, labels, and comments.
