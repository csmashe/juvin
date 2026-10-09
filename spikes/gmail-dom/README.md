# Gmail DOM spike (throwaway)

Result and findings: `docs/spikes/gmail-dom.md`. Nothing here is product code.

- `extension/`: temporary Gecko extension. On install it opens the inbox, picks the conversation with the most messages, runs the capture, and POSTs a report to `http://127.0.0.1:18765/report`. `THREAD_ONLY` in `background.js` controls whether it also opens a reply, inserts a draft, replaces, undoes, redoes, and discards. The Ctrl+Shift+Y command and the toolbar button run it on the current thread.
- Collector: `spikes/native-messaging/tools/report-server.py`.

Rerun on a copy of a signed-in Gecko profile:

```
python3 -I ../native-messaging/tools/report-server.py &
web-ext run --source-dir extension --firefox flatpak:app.zen_browser.zen \
  --firefox-profile "<path to the signed-in profile>" --no-reload --no-input
```

Reports land in `/tmp/juvin-nm-spike/reports.jsonl`. They contain counts, lengths, flags, and hash prefixes only.
