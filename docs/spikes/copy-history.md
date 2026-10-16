# Spike: origin check of files slated for copying

Date: October 8, 2026. Phase 0 item 7 of `docs/mvp-scope.md`. Result: **89 of 95 files confirmed original to the Linux repository; 6 files trace back to upstream Windows code and must not be copied.**

The check script and the raw list live in `spikes/copy-history/`.

## Method

The reference repository's history starts with the upstream Windows application (the initial commit of February 15, 2026 is the upstream Windows application: 101 files, WPF views under the Windows project). The Linux port begins on April 21, 2026 ("Port plugin loader + core Linux services"). For every file matching the reuse list in section 2.7, `git log --follow --diff-filter=A` finds the commit that first added it, following renames. Any file whose history reaches a commit before April 21, 2026 was carried over from the Windows code, whatever its current name.

Correction on October 9, 2026: the first run searched for `Views/MainWindow`, but the reference keeps `MainWindow.axaml` at the project root, so the main window was missing from the inventory while the scope claimed it was checked. The pattern is now `MainWindow`, which adds `MainWindow.axaml`, its code-behind, and `MainWindowViewModel.cs`; all three were first added by the Linux port on April 21, 2026, and the Windows project has no main window of that name, so the verdict is unchanged.

A plain file-name comparison, which is what section 2.7 relied on, misses exactly these cases: git detected the files as renames of Windows files because more than half of their content survived the port.

## Flagged files

| File | Traces to | Reason |
| --- | --- | --- |
| `PluginSDK/ILlmProviderRole.cs` | `PluginSDK/ILlmProviderPlugin.cs`, February 22, 2026 | the plugin SDK project predates the Linux port |
| `Linux/Views/Sections/GeneralSection.axaml.cs` | `Windows/Views/Sections/GeneralSection.xaml.cs`, initial commit | renamed from the WPF code-behind |
| `Linux/Views/Sections/HistorySection.axaml.cs` | `Windows/Views/Sections/HistorySection.xaml.cs`, initial commit | renamed from the WPF code-behind |
| `Linux/Views/Sections/AppearanceSection.axaml.cs` | `Windows/Views/Sections/AppearanceSection.xaml.cs`, April 11, 2026 | renamed from the WPF code-behind |
| `Linux/Views/Sections/ShortcutsSection.axaml.cs` | `Windows/Views/Sections/ShortcutsSection.xaml.cs`, April 11, 2026 | renamed from the WPF code-behind |
| `Linux/Views/Sections/AdvancedSection.axaml.cs` | `Windows/Views/Sections/AdvancedSection.xaml.cs`, April 8, 2026 | renamed from the WPF code-behind |

The matching `.axaml` files for those sections were created in the Linux repository (April 21 and 22, 2026) and are not flagged; only the code-behind files are. In practice the code-behinds are small, so writing Juvin's fresh costs little.

`ILlmProviderRole` matters more: it is the shape the provider interface was going to follow. Juvin defines its own provider interface from scratch. Using the same idea is fine; copying the file is not.

Two files were added by a commit titled "Port plugin loader + core Linux services" (`ApiKeyProtection.cs`) and "Add PromptAction and LLM provider support" (`PromptProcessingService.cs`). Both were new files in that commit with no rename source, and `ApiKeyProtection` describes itself as the Linux counterpart of DPAPI, so they count as Linux originals; the word "port" in the commit title refers to the plugin loader.

## Consequences

- Section 2.7 of the scope and the "Safe to copy" list in `CLAUDE.md` are updated to exclude the six files.
- The copy step in Phase 1 should run `spikes/copy-history/check.sh` again on the exact file set before copying, since the list will grow as views are added.

## Full list

| File | Added by | Date | Verdict |
| --- | --- | --- | --- |
| `Core/Models/LlmCallProvenance.cs` | 01e967b7 | 2026-07-08 | Linux original |
| `Core/Services/AtomicJsonStore.cs` | 3e228d69 | 2026-07-28 | Linux original |
| `Linux/MainWindow.axaml` | 4d1dbc6b | 2026-04-21 | Linux original |
| `Linux/MainWindow.axaml.cs` | 4d1dbc6b | 2026-04-21 | Linux original |
| `Linux/Services/ActiveWindow/AtSpiEventClient.cs` | 47ad0ab3 | 2026-07-06 | Linux original |
| `Linux/Services/ActiveWindow/AtSpiEventClientExtensions.cs` | f83d77f5 | 2026-09-11 | Linux original |
| `Linux/Services/ActiveWindow/AtSpiUrlExtractor.cs` | cd6c055e | 2026-05-28 | Linux original |
| `Linux/Services/ActiveWindow/IAtSpiEventClient.cs` | 47ad0ab3 | 2026-07-06 | Linux original |
| `Linux/Services/ApiKeyProtection.cs` | b7ab2cd5 | 2026-04-21 | Linux original |
| `Linux/Services/Hotkey/BackendSelector.cs` | cd6c055e | 2026-05-28 | Linux original |
| `Linux/Services/Hotkey/DeSetup/AtomicFileWriter.cs` | cd6c055e | 2026-05-28 | Linux original |
| `Linux/Services/Hotkey/DeSetup/DesktopDetector.cs` | cd6c055e | 2026-05-28 | Linux original |
| `Linux/Services/Hotkey/DeSetup/DictationShortcutSpecFactory.cs` | 997ed6b7 | 2026-06-08 | Linux original |
| `Linux/Services/Hotkey/DeSetup/GnomeShortcutWriter.cs` | cd6c055e | 2026-05-28 | Linux original |
| `Linux/Services/Hotkey/DeSetup/HyprlandShortcutWriter.cs` | cd6c055e | 2026-05-28 | Linux original |
| `Linux/Services/Hotkey/DeSetup/IDeShortcutWriter.cs` | cd6c055e | 2026-05-28 | Linux original |
| `Linux/Services/Hotkey/DeSetup/KdeShortcutWriter.cs` | cd6c055e | 2026-05-28 | Linux original |
| `Linux/Services/Hotkey/DeSetup/SentinelBlock.cs` | cd6c055e | 2026-05-28 | Linux original |
| `Linux/Services/Hotkey/DeSetup/SwayShortcutWriter.cs` | cd6c055e | 2026-05-28 | Linux original |
| `Linux/Services/Hotkey/Evdev/EvdevDeviceReader.cs` | cd6c055e | 2026-05-28 | Linux original |
| `Linux/Services/Hotkey/Evdev/EvdevGlobalShortcutBackend.cs` | cd6c055e | 2026-05-28 | Linux original |
| `Linux/Services/Hotkey/Evdev/IEvdevDeviceReader.cs` | e4d12a60 | 2026-07-13 | Linux original |
| `Linux/Services/Hotkey/Evdev/IEvdevInputDevice.cs` | b9fcde6d | 2026-07-18 | Linux original |
| `Linux/Services/Hotkey/Evdev/InputAccessSetupHelper.cs` | f9400272 | 2026-06-23 | Linux original |
| `Linux/Services/Hotkey/Evdev/InputDeviceAccessCheck.cs` | f9400272 | 2026-06-23 | Linux original |
| `Linux/Services/Hotkey/Evdev/InputEventStruct.cs` | cd6c055e | 2026-05-28 | Linux original |
| `Linux/Services/Hotkey/Evdev/InputGroupCheck.cs` | cd6c055e | 2026-05-28 | Linux original |
| `Linux/Services/Hotkey/Evdev/ISessionActivityMonitor.cs` | e4d12a60 | 2026-07-13 | Linux original |
| `Linux/Services/Hotkey/Evdev/KeyboardDeviceDiscovery.cs` | cd6c055e | 2026-05-28 | Linux original |
| `Linux/Services/Hotkey/Evdev/LinuxKeyMap.cs` | cd6c055e | 2026-05-28 | Linux original |
| `Linux/Services/Hotkey/Evdev/LogindSessionActivityMonitor.cs` | e4d12a60 | 2026-07-13 | Linux original |
| `Linux/Services/Hotkey/GlobalShortcutRegistrationResult.cs` | cd6c055e | 2026-05-28 | Linux original |
| `Linux/Services/Hotkey/GlobalShortcutSet.cs` | cd6c055e | 2026-05-28 | Linux original |
| `Linux/Services/Hotkey/IGlobalShortcutBackend.cs` | cd6c055e | 2026-05-28 | Linux original |
| `Linux/Services/Hotkey/Portal/XdgPortalGlobalShortcutsBackend.cs` | cd6c055e | 2026-05-28 | Linux original |
| `Linux/Services/Hotkey/SharpHookGlobalShortcutBackend.cs` | cd6c055e | 2026-05-28 | Linux original |
| `Linux/Services/Hotkey/ShortcutDispatcher.cs` | cd6c055e | 2026-05-28 | Linux original |
| `Linux/Services/Hotkey/ShortcutMatcher.cs` | cd6c055e | 2026-05-28 | Linux original |
| `Linux/Services/Insertion/AtSpiPasteConfirmation.cs` | 5e07b299 | 2026-07-09 | Linux original |
| `Linux/Services/Ipc/ApiSocketOwnership.cs` | 0eef077b | 2026-07-19 | Linux original |
| `Linux/Services/Ipc/ControlSocketClient.cs` | cd6c055e | 2026-05-28 | Linux original |
| `Linux/Services/Ipc/ControlSocketOwnership.cs` | 0eef077b | 2026-07-19 | Linux original |
| `Linux/Services/Ipc/ControlSocketServer.cs` | cd6c055e | 2026-05-28 | Linux original |
| `Linux/Services/Ipc/JsonControlProtocol.cs` | cd6c055e | 2026-05-28 | Linux original |
| `Linux/Services/Ipc/SocketPathResolver.cs` | cd6c055e | 2026-05-28 | Linux original |
| `Linux/Services/Ipc/UnixPeerCredentials.cs` | f0215b25 | 2026-07-27 | Linux original |
| `Linux/Services/LlmStreamPump.cs` | e1c94ee2 | 2026-06-03 | Linux original |
| `Linux/Services/PromptProcessingService.cs` | ae9ff826 | 2026-04-21 | Linux original |
| `Linux/Services/Setup/ActiveWindowSetupTask.cs` | 997ed6b7 | 2026-06-08 | Linux original |
| `Linux/Services/Setup/AutoPasteSetupTask.cs` | 997ed6b7 | 2026-06-08 | Linux original |
| `Linux/Services/Setup/ClipboardSetupTask.cs` | 997ed6b7 | 2026-06-08 | Linux original |
| `Linux/Services/Setup/FfmpegSetupTask.cs` | 997ed6b7 | 2026-06-08 | Linux original |
| `Linux/Services/Setup/GlobalHotkeySetupTask.cs` | 997ed6b7 | 2026-06-08 | Linux original |
| `Linux/Services/Setup/ISetupTask.cs` | 997ed6b7 | 2026-06-08 | Linux original |
| `Linux/Services/Setup/KwinActiveWindowSetupTask.cs` | 8479e7b2 | 2026-06-08 | Linux original |
| `Linux/Services/Setup/PackageInstaller.cs` | 997ed6b7 | 2026-06-08 | Linux original |
| `Linux/Services/WaylandSessionDetector.cs` | b4b7b20f | 2026-07-17 | Linux original |
| `Linux/Services/X11StickyWindow.cs` | be88b7e9 | 2026-06-15 | Linux original |
| `Linux/ViewModels/MainWindowViewModel.cs` | b7ab2cd5 | 2026-04-21 | Linux original |
| `Linux/Views/Sections/AboutSection.axaml` | 4f3a75df | 2026-04-21 | Linux original |
| `Linux/Views/Sections/AboutSection.axaml.cs` | 4f3a75df | 2026-04-21 | Linux original |
| `Linux/Views/Sections/AdvancedSection.axaml` | 613f8d6c | 2026-04-22 | Linux original |
| `Linux/Views/Sections/AdvancedSection.axaml.cs` | 8f5fd7a5 | 2026-04-08 | **adapted from the upstream Windows file, do not copy** |
| `Linux/Views/Sections/AppearanceSection.axaml` | 613f8d6c | 2026-04-22 | Linux original |
| `Linux/Views/Sections/AppearanceSection.axaml.cs` | 8d5bb057 | 2026-04-11 | **adapted from the upstream Windows file, do not copy** |
| `Linux/Views/Sections/DashboardSection.axaml` | 4f3a75df | 2026-04-21 | Linux original |
| `Linux/Views/Sections/DashboardSection.axaml.cs` | 4f3a75df | 2026-04-21 | Linux original |
| `Linux/Views/Sections/DictationSection.axaml` | 4f3a75df | 2026-04-21 | Linux original |
| `Linux/Views/Sections/DictationSection.axaml.cs` | 4f3a75df | 2026-04-21 | Linux original |
| `Linux/Views/Sections/DictionarySection.axaml` | 583ed6a3 | 2026-04-21 | Linux original |
| `Linux/Views/Sections/DictionarySection.axaml.cs` | 583ed6a3 | 2026-04-21 | Linux original |
| `Linux/Views/Sections/FileTranscriptionSection.axaml` | afe74857 | 2026-04-22 | Linux original |
| `Linux/Views/Sections/FileTranscriptionSection.axaml.cs` | afe74857 | 2026-04-22 | Linux original |
| `Linux/Views/Sections/GeneralSection.axaml` | 583ed6a3 | 2026-04-21 | Linux original |
| `Linux/Views/Sections/GeneralSection.axaml.cs` | bc56f4b7 | 2026-02-15 | **adapted from the upstream Windows file, do not copy** |
| `Linux/Views/Sections/HistorySection.axaml` | 583ed6a3 | 2026-04-21 | Linux original |
| `Linux/Views/Sections/HistorySection.axaml.cs` | bc56f4b7 | 2026-02-15 | **adapted from the upstream Windows file, do not copy** |
| `Linux/Views/Sections/PluginsSection.axaml` | 583ed6a3 | 2026-04-21 | Linux original |
| `Linux/Views/Sections/PluginsSection.axaml.cs` | 583ed6a3 | 2026-04-21 | Linux original |
| `Linux/Views/Sections/ProfilesSection.axaml` | 583ed6a3 | 2026-04-21 | Linux original |
| `Linux/Views/Sections/ProfilesSection.axaml.cs` | 583ed6a3 | 2026-04-21 | Linux original |
| `Linux/Views/Sections/PromptsSection.axaml` | 583ed6a3 | 2026-04-21 | Linux original |
| `Linux/Views/Sections/PromptsSection.axaml.cs` | 583ed6a3 | 2026-04-21 | Linux original |
| `Linux/Views/Sections/RecorderSection.axaml` | afe74857 | 2026-04-22 | Linux original |
| `Linux/Views/Sections/RecorderSection.axaml.cs` | afe74857 | 2026-04-22 | Linux original |
| `Linux/Views/Sections/ShortcutsSection.axaml` | 583ed6a3 | 2026-04-21 | Linux original |
| `Linux/Views/Sections/ShortcutsSection.axaml.cs` | 8d5bb057 | 2026-04-11 | **adapted from the upstream Windows file, do not copy** |
| `Linux/Views/Sections/SnippetsSection.axaml` | 583ed6a3 | 2026-04-21 | Linux original |
| `Linux/Views/Sections/SnippetsSection.axaml.cs` | 583ed6a3 | 2026-04-21 | Linux original |
| `Linux/Views/Sections/TextInsertionSection.axaml` | cd6c055e | 2026-05-28 | Linux original |
| `Linux/Views/Sections/TextInsertionSection.axaml.cs` | cd6c055e | 2026-05-28 | Linux original |
| `Linux/Views/WelcomeWizard.axaml` | 4f3a75df | 2026-04-21 | Linux original |
| `Linux/Views/WelcomeWizard.axaml.cs` | 4f3a75df | 2026-04-21 | Linux original |
| `PluginSDK/Helpers/SseEventDecoder.cs` | e573f0ca | 2026-08-02 | Linux original |
| `PluginSDK/Helpers/ThinkingBlockFilter.cs` | 6e3b2c04 | 2026-09-07 | Linux original |
| `PluginSDK/ILlmProviderRole.cs` | 7e7a8423 | 2026-02-22 | **adapted from the upstream Windows file, do not copy** |

## Crash reporting files (October 16, 2026)

For Phase 6 slice 2 the reference repository's Sentry telemetry files were checked the same way, with `git log --follow --diff-filter=A`. All ten first appear in commit `9e6957e9` of September 12, 2026 ("Add opt-in Sentry crash and performance reporting"), five months into the Linux port, with no rename source, so they are Linux originals and may be copied. Juvin copied the consent gate, the options factory, the error facts, the scrubber, and the service, renamed them, and cut them down to crash reports only: no transactions, spans, startup timing, install kind, or breadcrumbs, and a scrubber that drops every free-text field instead of pattern-matching it. The tests were rewritten for that smaller surface.

| File | Added by | Date | Verdict | Juvin file |
| --- | --- | --- | --- | --- |
| `Linux/Services/Telemetry/TelemetryConsentGate.cs` | 9e6957e9 | 2026-09-12 | Linux original | `CrashReportConsentGate.cs` |
| `Linux/Services/Telemetry/SentryOptionsFactory.cs` | 9e6957e9 | 2026-09-12 | Linux original | `SentryOptionsFactory.cs`, `CrashReportBuildInfo.cs` |
| `Linux/Services/Telemetry/TelemetryErrorFacts.cs` | 9e6957e9 | 2026-09-12 | Linux original | `CrashReportErrorFacts.cs` |
| `Linux/Services/Telemetry/TelemetryScrubber.cs` | 9e6957e9 | 2026-09-12 | Linux original | `CrashReportScrubber.cs` |
| `Linux/Services/Telemetry/SentryTelemetryService.cs` | 9e6957e9 | 2026-09-12 | Linux original | `CrashReportingService.cs` |
| `Linux.Tests/Telemetry/SentryOptionsFactoryTests.cs` | 9e6957e9 | 2026-09-12 | Linux original | `SentryOptionsFactoryTests.cs` |
| `Linux.Tests/Telemetry/SentryPipelineTests.cs` | 9e6957e9 | 2026-09-12 | Linux original | folded into `CrashReportingServiceTests.cs` |
| `Linux.Tests/Telemetry/SentryTelemetryServiceTests.cs` | 9e6957e9 | 2026-09-12 | Linux original | `CrashReportingServiceTests.cs`, `CrashReportConsentGateTests.cs` |
| `Linux.Tests/Telemetry/TelemetryScrubberTests.cs` | 9e6957e9 | 2026-09-12 | Linux original | `CrashReportScrubberTests.cs` |
| `Linux.Tests/Telemetry/TelemetryTestSupport.cs` | 9e6957e9 | 2026-09-12 | Linux original | `CrashReportTestSupport.cs` |

The Juvin files live in `src/Juvin.App/Services/CrashReporting/` and `tests/Juvin.App.Tests/CrashReporting/`.
