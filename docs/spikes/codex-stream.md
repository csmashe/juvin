# Spike: Codex CLI JSONL stream

Date: October 8, 2026. Phase 0 item 6 of `docs/mvp-scope.md`. Result: **go for the Codex provider, no for live preview**: `codex exec --json` emits the agent message as one completed item, with no per-token deltas.

Throwaway files live in `spikes/codex-stream/` (prompt, captured events, stderr).

## Setup

- Codex CLI 0.162.0, signed in, default model `gpt-6.1-sol` from the user's config.
- Command exactly as section 2.5 specifies, prompt on stdin:

```
codex exec --json --ephemeral --sandbox read-only --skip-git-repo-check -m gpt-6.1-sol -
```

- Same rough draft as the local model spike, without the thread, to keep the prompt short.

## Results

| Measure | Value |
| --- | --- |
| Wall clock, process start to exit | 13.3 s |
| JSONL lines | 4: `thread.started`, `turn.started`, `item.completed` (type `agent_message`), `turn.completed` |
| Delta events | none |
| Output tokens reported | 145, plus 45 reasoning tokens |
| Input tokens reported | 14,490, of which 12,288 cached (Codex's own instructions, not the prompt) |
| Quality | good, faithful; ended with "Best," and no name because the prompt had none |

`codex features list` shows no flag that adds message deltas to the exec output; the only streaming-related feature (`apply_patch_streaming_events`) concerns patches.

## Findings

- **The final text arrives whole.** The `item.completed` event carries the entire message in `item.text`. Juvin reads that one event and ignores the rest, which is simpler than the SSE path but means the panel shows a spinner, not streaming words, in Codex mode.
- **Latency is comparable to the local path.** 13 s against 10 to 15 s for the 26B local models, with no local GPU involved. Codex's own instruction prefix dominates the input tokens; the user's thread will add to that.
- **`turn.completed` carries usage**, which is enough for the provenance record (model, tokens, duration).
- **Errors go to stderr, not the JSONL.** The provider must capture stderr and the exit code and surface them when no `item.completed` arrives.

## Consequences for the design

- The Codex provider implements the provider interface with a non-streaming result: it raises one "completed" event. The panel's preview area shows a progress state for providers that do not stream, and the provider interface exposes a `SupportsStreaming` flag so the UI can choose.
- Keep the command from section 2.5; it worked unchanged.
- Section 2.5's open question is closed: no usable deltas for live preview.

## Not covered

- Long threads and the effect on Codex latency.
- `--image` for the later screen workflow.
- Behaviour when the CLI is signed out (the wizard check), and when the subscription quota is exhausted.
