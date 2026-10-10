import { createHash } from "node:crypto";
import { describe, expect, it } from "vitest";
import { hashText, sha256 } from "../src/content/hash";

describe("hashText", () => {
  it("returns sha256: plus lowercase hex", async () => {
    expect(await hashText("test")).toBe("sha256:9f86d081884c7d659a2feaa0c55ad015a3bf4f1b2b0b822cd15d6c15b0f00a08");
  });

  it("hashes the empty string", async () => {
    expect(await hashText("")).toBe("sha256:e3b0c44298fc1c149afbf4c8996fb92427ae41e4649b934ca495991b7852b855");
  });

  it("matches the platform digest with the local SHA-256 at block boundaries", () => {
    for (const length of [0, 1, 55, 56, 63, 64, 65, 119, 120, 1000]) {
      const bytes = new Uint8Array(Array.from({ length }, (_, i) => (i * 31 + 7) % 256));
      const hex = Array.from(sha256(bytes), (b) => b.toString(16).padStart(2, "0")).join("");
      expect(hex).toBe(createHash("sha256").update(bytes).digest("hex"));
    }
  });

  it("hashes the UTF-8 bytes of non-ASCII text", async () => {
    const text = "Café — line\nnext \u{1F600}";
    expect(await hashText(text)).toBe(`sha256:${createHash("sha256").update(text, "utf8").digest("hex")}`);
  });
});
