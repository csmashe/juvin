import { describe, expect, it } from "vitest";
import { detectBrowser } from "../src/shared/browser-detect";

const chromeUa = "Mozilla/5.0 (X11; Linux x86_64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/155.0.7100.12 Safari/537.36";
const edgeUa = `${chromeUa} Edg/154.0.4258.62`;
const firefoxUa = "Mozilla/5.0 (X11; Linux x86_64; rv:157.0) Gecko/20100101 Firefox/157.0";

describe("detectBrowser", () => {
  it("uses getBrowserInfo on Gecko browsers, including forks", () => {
    expect(detectBrowser({ userAgent: firefoxUa, browserInfo: { name: "Zen", version: "1.22.3b" } })).toEqual({ name: "firefox", version: "1.22.3b" });
  });

  it("falls back to the Firefox user agent token", () => {
    expect(detectBrowser({ userAgent: firefoxUa })).toEqual({ name: "firefox", version: "157.0" });
  });

  it("detects Edge from its user agent token", () => {
    expect(detectBrowser({ userAgent: edgeUa })).toEqual({ name: "edge", version: "154.0.4258.62" });
  });

  it("tells Chrome from Chromium by brand", () => {
    const chrome = detectBrowser({ userAgent: chromeUa, brands: [{ brand: "Google Chrome", version: "155" }, { brand: "Chromium", version: "155" }] });
    const chromium = detectBrowser({ userAgent: chromeUa, brands: [{ brand: "Chromium", version: "155" }, { brand: "Not=A?Brand", version: "24" }] });
    expect(chrome).toEqual({ name: "chrome", version: "155.0.7100.12" });
    expect(chromium).toEqual({ name: "chromium", version: "155.0.7100.12" });
  });

  it("assumes Chrome when brands are unavailable", () => {
    expect(detectBrowser({ userAgent: chromeUa })).toEqual({ name: "chrome", version: "155.0.7100.12" });
  });

  it("reports unknown otherwise", () => {
    expect(detectBrowser({ userAgent: "Mozilla/5.0 (Macintosh) AppleWebKit/605.1.15 Version/18.0 Safari/605.1.15" })).toEqual({ name: "unknown", version: "" });
  });
});
