# Store listings

What to enter for the Chrome Web Store and Firefox Add-ons (addons.mozilla.org). Build the packages with `scripts/build-extension-packages.sh` from a commit on `main`; it writes `dist/juvin-chrome-<version>.zip`, `dist/juvin-firefox-<version>.zip`, and `dist/juvin-extension-source-<version>.zip`. The images are in `assets/store/`.

The extension is useless without the Juvin desktop app, so both listings say so first, and the reviewer notes explain how to test with the app and the bundled test page instead of a Gmail account.

## Shared text

**Name:** Juvin

**Summary** (Chrome allows 132 characters, Firefox 250; this is the manifest's description):

> Rewrites the selected email draft with the Juvin desktop app, using the whole conversation as context.

**Description:**

> Juvin rewrites the email you are drafting in Gmail, using the conversation it replies to, with a model on your own computer.
>
> This extension is the browser half of Juvin. It needs the free Juvin desktop app for Linux, macOS, or Windows, which you can download from https://github.com/csmashe/juvin/releases. The app's setup connects the two.
>
> How it works:
> - Select your draft, or part of it, and press Ctrl+Shift+F (Control+Shift+F on a Mac).
> - A menu opens beside the draft with five actions on keys 1 to 5: What am I missing?, Soften, Make clearer, Make shorter, and Fix grammar. More actions and your own are a right-click away, and you can type a request of your own.
> - The rewrite replaces your selection in place. Ctrl+Z (Cmd+Z on a Mac) puts the original back. Review opens the result in the desktop app first, so you can read it before applying it.
>
> Juvin reads the whole conversation, including collapsed messages, so the rewrite keeps the deadlines, names, and commitments already in the thread. It changes only your draft, and it refuses to apply a result if you edited the draft while it was working.
>
> Private by design:
> - The extension reads a page only when you press the shortcut, and only on Gmail unless you allow another site.
> - It keeps no settings and no history, and talks to no server. It hands what it read to the Juvin app on your computer.
> - By default the app sends it to a model server on your own computer or network (Ollama, LM Studio, or llama.cpp). If you choose Codex mode instead, it goes to OpenAI under your own Codex sign-in.
> - No telemetry and no analytics. Juvin is open source under the GPL-3.0.
>
> Privacy policy: https://github.com/csmashe/juvin/blob/main/docs/PRIVACY.md
> Source code: https://github.com/csmashe/juvin

**Privacy policy URL:** https://github.com/csmashe/juvin/blob/main/docs/PRIVACY.md

**Homepage and support URL:** https://github.com/csmashe/juvin (support through its Issues page)

**Images** (`assets/store/`): `screenshot-1-menu.png` (the action menu beside a selected draft), `screenshot-2-applied.png` (the rewrite applied in place, with the undo note), `screenshot-3-more.png` (the right-click list, with the thread status and Review), `screenshot-4-review.png` (Review in the desktop panel), all 1280x800; `promo-small-440x280.png` and `promo-marquee-1400x560.png` for Chrome.

## Chrome Web Store

Developer Dashboard, new item, upload `juvin-chrome-<version>.zip`.

The store gives the item its own ID; the package carries no `key`. Before publishing, copy the item ID from the dashboard so the app's native host manifest can allow it (`NativeHost` in `src/Juvin.Platform`), and ship that app version first: without it the store build cannot reach the app.

**Store listing tab:**
- Category: Productivity, Communication
- Language: English
- Store icon: `assets/brand/png/juvin-mark-color-128.png` (128x128).
- Screenshots: the four above. Small promo tile: `promo-small-440x280.png`. Marquee: `promo-marquee-1400x560.png`.
- Official URL: none. Homepage URL and support URL: as above.

**Privacy tab:**
- Single purpose: "Rewrite the email draft the user selects, in place, using the Juvin desktop app on the same computer, with the conversation as context."
- Permission justifications:
  - `nativeMessaging`: "Sends the selected draft and its conversation to the Juvin desktop app on the user's computer, and receives the rewrite. This is the extension's only way to reach the app; it talks to no server."
  - `activeTab`: "Lets the user rewrite a draft on a site other than Gmail by pressing the shortcut there, without granting access to every site."
  - `scripting`: "Injects the content script into a Gmail tab that was already open when the extension was installed, or into the tab the user pressed the shortcut in, so the draft can be read and the rewrite applied."
  - Host permission `https://mail.google.com/*`: "Gmail is the supported email site. The content script reads the selected draft and the conversation only when the user presses the shortcut, and writes the rewrite back into the draft."
  - Optional host permission `<all_urls>`: "Requested only when the user presses 'Allow Juvin on all sites' in the popup, to let the shortcut work in email editors on other sites. Not granted by default."
- Remote code: No. All scripts are in the package.
- Data usage: check "Personally identifiable information" (names and addresses in the conversation), "Personal communications" (the draft and the email conversation), "Website content" (on other sites, the page's text), and "Web history" (the address of the page the draft is on, origin and path only, sent with each capture). Then certify all three statements: not sold to third parties, not used or transferred for purposes unrelated to the single purpose, not used for creditworthiness or lending. The data goes only to the Juvin app on the user's computer, and from there to the model server or Codex account the user chose.
- Privacy policy URL: as above.

**Distribution tab:** Public, all regions.

**Test instructions** (Account tab, or the notes field the dashboard offers):

> The extension needs the Juvin desktop app, which is free and open source. To test without a Gmail account:
> 1. Install Juvin from https://github.com/csmashe/juvin/releases (Windows installer, macOS dmg, or Linux AppImage) and start it. The setup wizard opens.
> 2. In the wizard, choose Local mode with an Ollama or LM Studio server, or Codex mode, then on the browser step press Register for Chrome. Note that the item ID must match the one the app allows; the store release of the app includes it.
> 3. Click the Juvin toolbar icon. The popup should say "Connected to Juvin". Press "Open test page".
> 4. Select a line of the reply draft at the bottom of the test page and press Ctrl+Shift+F (Control+Shift+F on a Mac). The action menu opens beside it; press 3 for Make clearer. The line is rewritten in place, and Ctrl+Z restores it.
>
> Without the app running, the popup says "Not connected", and the shortcut does nothing on the page.

## Firefox Add-ons

Developer Hub, submit a new add-on, "On this site", upload `juvin-firefox-<version>.zip`. The ID `extension@juvin.app` is in the manifest, and the app already allows it.

- Compatibility: Firefox only, not Firefox for Android; the extension needs the desktop app.
- Source code: yes, upload `juvin-extension-source-<version>.zip`. The build steps are in its `extension/README.md`, under "Rebuilding the store package": Node 24 and npm 11, `npm ci`, `npm run build`, and `dist/firefox` is the package. The bundle is not minified.
- Categories: Social & Communication, and Productivity if offered. Otherwise Other.
- License: GNU General Public License v3.0
- Privacy policy: paste the text of `docs/PRIVACY.md`, or link to it.
- Data collection: the manifest declares `data_collection_permissions` as required `personalCommunications`, `personallyIdentifyingInfo`, `websiteContent`, and `browsingActivity`. Mozilla counts data sent to a native application as transmission, and the extension passes the draft, the conversation with its names and addresses, on other sites the page's text, and the page's address (origin and path, without the query or fragment) to the desktop app. Firefox shows these at install; the extension cannot work without them, so none is optional.
- After submitting, Manage Listing, Images: upload `assets/brand/png/juvin-mark-color-128.png` as the custom icon (otherwise the listing shows Mozilla's default) and the four screenshots, captioned:
  1. Select a draft and press the shortcut: the action menu opens beside it.
  2. The rewrite replaces the selection in place, and Ctrl+Z puts the original back.
  3. Right-click for more actions, the thread status, and Review.
  4. Review shows the rewrite in the desktop app before you apply it.

**Notes to reviewer:**

> Juvin is the browser half of an open-source desktop app (https://github.com/csmashe/juvin). The extension reads the selected email draft and its conversation only when the user presses Ctrl+Shift+F, and passes them over native messaging to the Juvin app on the same computer (host `com.juvin.host`). It talks to no server and keeps no data. Because native messaging counts as transmission, the manifest declares personal communications, personally identifying information, website content, and browsing activity (the address of the page the draft is on) as required data. The app rewrites the draft with a model the user chose, and it keeps no history unless the user turns that on. The app uses a local model server by default and sends the result back, which the extension applies in place.
>
> To test without a Gmail account: install the app from https://github.com/csmashe/juvin/releases and start it; in its setup wizard choose a model server and press Register for Firefox on the browser step. Then open the extension popup, check that it says "Connected to Juvin", and press "Open test page". Select a line of the reply draft at the bottom, press Ctrl+Shift+F (on macOS, Juvin's desktop shortcut handles it), and press 3 (Make clearer) in the menu that opens. The line is rewritten in place and Ctrl+Z restores it.
>
> The source package rebuilds the submitted package exactly with `npm ci` and `npm run build` in `extension/` (Node 24); see `extension/README.md`.

## After publishing

- Record the store IDs and listing URLs here and in `extension/README.md`.
- The Chrome store ID goes into the app's native host allowlist before the listing is public, as above.
- Each new extension version: bump `version` in `extension/src/manifest.json`, rebuild with the script, and upload both packages (and the new source package to Firefox).
