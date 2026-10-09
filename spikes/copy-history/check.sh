#!/usr/bin/env bash
# Throwaway Phase 0 check: for every reference-project file slated for copying, find the commit that added it
# (following renames) and flag any whose history reaches the upstream Windows era (before 2026-04-21).
# Usage: check.sh <reference repo path>   Writes files.txt: path|first-commit|date
set -u
repo="$1"
cd "$repo" || exit 1
for pat in "Hotkey/" "ShortcutMatcher" "ShortcutDispatcher" "DeSetup/" "ISetupTask" "Services/Setup/" "Ipc/" "AtomicJsonStore" "ApiKeyProtection" "SseEventDecoder" "ThinkingBlockFilter" "LlmStreamPump" "ILlmProviderRole" "PromptProcessingService" "LlmCallProvenance" "AtSpi" "Wayland" "MainWindow" "Views/WelcomeWizard" "Views/Sections/"; do
  git ls-files "src/*${pat}*" | grep -v -E "obj/|bin/"
done | sort -u | while read -r f; do
  first=$(git log --diff-filter=A --follow --format="%h %ad" --date=short -- "$f" | tail -1)
  echo "$f|${first% *}|${first#* }"
done | sort
