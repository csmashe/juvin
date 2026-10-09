#!/usr/bin/env bash
# Removes everything the native messaging spike registered on this machine.
set -u
rm -f ~/.mozilla/native-messaging-hosts/com.juvin.spike.json
rm -f ~/.config/google-chrome/NativeMessagingHosts/com.juvin.spike.json
for app in org.mozilla.firefox app.zen_browser.zen; do
    rm -f ~/.var/app/$app/.mozilla/native-messaging-hosts/com.juvin.spike.json
    rm -rf ~/.var/app/$app/juvin
done
rm -rf /tmp/juvin-nm-spike /tmp/juvin-chrome-profile
echo "Left in place: the Zen override (flatpak override --user --persist=.mozilla app.zen_browser.zen) and the Flatpak Firefox install."
