# Local model spike (throwaway)

Results: `docs/spikes/local-model.md`.

- `sample-thread.json`: the synthetic thread and rough draft.
- `run.py`: `python3 -I run.py <server base url> native|openai <model>...`. `native` uses Ollama's `/api/chat` with `think: false`; `openai` uses `/v1/chat/completions`. Prints one JSON line per model and writes each rewrite to `results/<mode>-<model>.md`.
- `results/`: the outputs from October 8, 2026 against the Unraid Ollama server.
