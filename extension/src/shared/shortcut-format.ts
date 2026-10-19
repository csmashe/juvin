// The app's canonical modifiers on macOS: Ctrl is Command, Alt is Option, and Super is the Control key.
const MAC_NAMES: Record<string, string> = { Ctrl: "Cmd", Alt: "Option", Shift: "Shift", Super: "Ctrl" };
const MAC_ORDER = ["Super", "Ctrl", "Alt", "Shift"];

/** The app's canonical shortcut, such as `Ctrl+Shift+F`, as the keyboard labels it; unchanged off macOS. */
export function formatShortcut(canonical: string, isMac: boolean): string {
  if (!isMac) {
    return canonical;
  }

  const parts = canonical.split("+");
  const key = parts.pop() ?? "";
  if (!parts.every((part) => part in MAC_NAMES)) {
    return canonical;
  }

  return [...MAC_ORDER.filter((name) => parts.includes(name)).map((name) => MAC_NAMES[name]), key].join("+");
}
