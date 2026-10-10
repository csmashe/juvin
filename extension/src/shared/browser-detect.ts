import type { BrowserName } from "./protocol";

export interface BrowserIdentity {
  name: BrowserName;
  version: string;
}

export interface UaBrand {
  brand: string;
  version: string;
}

export interface DetectInput {
  userAgent: string;
  /** `navigator.userAgentData.brands` on Chromium browsers. */
  brands?: readonly UaBrand[];
  /** `runtime.getBrowserInfo()` on Gecko browsers. */
  browserInfo?: { name: string; version: string };
}

function uaVersion(userAgent: string, token: string): string | undefined {
  return new RegExp(`${token}/([\\d.]+)`).exec(userAgent)?.[1];
}

export function detectBrowser({ userAgent, brands, browserInfo }: DetectInput): BrowserIdentity {
  // Gecko forks (Zen, LibreWolf) report their own name here but are Firefox to the protocol.
  if (browserInfo) {
    return { name: "firefox", version: browserInfo.version };
  }

  const firefox = uaVersion(userAgent, "Firefox");
  if (firefox) {
    return { name: "firefox", version: firefox };
  }

  const edge = uaVersion(userAgent, "Edg");
  if (edge) {
    return { name: "edge", version: edge };
  }

  const chromeVersion = uaVersion(userAgent, "Chrome");
  if (!chromeVersion) {
    return { name: "unknown", version: "" };
  }

  if (brands && brands.length > 0) {
    if (brands.some((b) => b.brand === "Google Chrome")) {
      return { name: "chrome", version: chromeVersion };
    }

    if (brands.some((b) => b.brand === "Microsoft Edge")) {
      return { name: "edge", version: chromeVersion };
    }

    return { name: "chromium", version: chromeVersion };
  }

  return { name: "chrome", version: chromeVersion };
}
