import { describe, expect, it } from "vitest";
import { formatShortcut } from "../src/shared/shortcut-format";

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
