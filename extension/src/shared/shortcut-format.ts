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

const MAC_FLAGS: Record<string, "metaKey" | "altKey" | "shiftKey" | "ctrlKey"> = { Ctrl: "metaKey", Alt: "altKey", Shift: "shiftKey", Super: "ctrlKey" };

/** The DOM `code` of the app's key name: its names match `KeyboardEvent.code` except letters, digits, and arrows. */
function keyCode(key: string): string {
  if (/^[A-Z]$/.test(key)) {
    return `Key${key}`;
  }

  if (/^[0-9]$/.test(key)) {
    return `Digit${key}`;
  }

  return ["Up", "Down", "Left", "Right"].includes(key) ? `Arrow${key}` : key;
}

/**
 * Whether a key press on macOS is the app's canonical shortcut, by physical key as the app's hook matches it:
 * `physicalKey`, the US key position, when the user's layout moved the chord's key.
 */
export function isMacShortcut(
  canonical: string,
  event: Pick<KeyboardEvent, "code" | "metaKey" | "altKey" | "shiftKey" | "ctrlKey">,
  physicalKey?: string,
): boolean {
  const parts = canonical.split("+");
  const key = parts.pop() ?? "";
  if (key === "" || !parts.every((part) => part in MAC_FLAGS)) {
    return false;
  }

  return event.code === keyCode(physicalKey ?? key) && Object.entries(MAC_FLAGS).every(([name, flag]) => event[flag] === parts.includes(name));
}
