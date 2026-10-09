# Codex stream spike (throwaway)

Results: `docs/spikes/codex-stream.md`.

- `prompt.txt`: the stdin prompt.
- `events.jsonl` and `stderr.txt`: captured output of `codex exec --json --ephemeral --sandbox read-only --skip-git-repo-check -m gpt-6.1-sol - < prompt.txt`.
