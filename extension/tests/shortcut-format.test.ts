import { describe, expect, it } from "vitest";
import { formatShortcut, isMacShortcut } from "../src/shared/shortcut-format";

describe("formatShortcut", () => {
  it("leaves the canonical form unchanged off macOS", () => {
    expect(formatShortcut("Ctrl+Alt+Super+K", false)).toBe("Ctrl+Alt+Super+K");
  });

  it("shows the macOS default as the Control key", () => {
    expect(formatShortcut("Super+Shift+F", true)).toBe("Ctrl+Shift+F");
  });

  it("names Command and Option on macOS, Control first", () => {
    expect(formatShortcut("Ctrl+Shift+F", true)).toBe("Cmd+Shift+F");
    expect(formatShortcut("Ctrl+Alt+Super+K", true)).toBe("Ctrl+Cmd+Option+K");
  });

  it("leaves text it does not recognize unchanged", () => {
    expect(formatShortcut("Hyper+F", true)).toBe("Hyper+F");
  });
});

describe("isMacShortcut", () => {
  const press = (code: string, mods: Partial<Record<"metaKey" | "altKey" | "shiftKey" | "ctrlKey", boolean>> = {}) => ({
    code,
    metaKey: false,
    altKey: false,
    shiftKey: false,
    ctrlKey: false,
    ...mods,
  });

  it("matches the macOS default with the Control key", () => {
    expect(isMacShortcut("Super+Shift+F", press("KeyF", { ctrlKey: true, shiftKey: true }))).toBe(true);
  });

  it("needs exactly the chord's modifiers", () => {
    expect(isMacShortcut("Super+Shift+F", press("KeyF", { ctrlKey: true }))).toBe(false);
    expect(isMacShortcut("Super+Shift+F", press("KeyF", { ctrlKey: true, shiftKey: true, metaKey: true }))).toBe(false);
    expect(isMacShortcut("Super+Shift+F", press("KeyF", { metaKey: true, shiftKey: true }))).toBe(false);
  });

  it("matches the physical key", () => {
    expect(isMacShortcut("Super+Shift+F", press("KeyG", { ctrlKey: true, shiftKey: true }))).toBe(false);
    expect(isMacShortcut("Ctrl+Alt+7", press("Digit7", { metaKey: true, altKey: true }))).toBe(true);
    expect(isMacShortcut("Super+Up", press("ArrowUp", { ctrlKey: true }))).toBe(true);
    expect(isMacShortcut("Super+Shift+Comma", press("Comma", { ctrlKey: true, shiftKey: true }))).toBe(true);
    expect(isMacShortcut("Super+F5", press("F5", { ctrlKey: true }))).toBe(true);
  });

  it("matches the key position the layout moved the key to", () => {
    expect(isMacShortcut("Super+Shift+Z", press("KeyY", { ctrlKey: true, shiftKey: true }), "Y")).toBe(true);
    expect(isMacShortcut("Super+Shift+Z", press("KeyZ", { ctrlKey: true, shiftKey: true }), "Y")).toBe(false);
  });

  it("matches nothing for text it does not recognize", () => {
    expect(isMacShortcut("Hyper+F", press("KeyF"))).toBe(false);
    expect(isMacShortcut("", press(""))).toBe(false);
  });
});
