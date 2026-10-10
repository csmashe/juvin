// Bundles the extension and writes one output per browser: dist/chrome and dist/firefox.
import { context, build } from "esbuild";
import { cp, mkdir, readFile, rm, writeFile } from "node:fs/promises";
import { dirname, join } from "node:path";
import { fileURLToPath } from "node:url";

const root = dirname(fileURLToPath(import.meta.url));
const brandPng = join(root, "..", "assets", "brand", "png");
const watch = process.argv.includes("--watch");

// Pinned so the native host manifest can allowlist the ID before the extension is ever loaded.
const CHROME_KEY =
  "MIIBIjANBgkqhkiG9w0BAQEFAAOCAQ8AMIIBCgKCAQEA6o8eio0tKg93I46ARWN7g1cOOBFVSFsXzHqvNnY2DsMKlejtRHSGHhXk4gCctjV7Fk2QYs6nRUNG31wytHpJlr0PljZdzYuXU2KP0n2o87XbhNM5fdpb/ztieN+BK4mWawOh8DJ8Kf/LWm7UU2FmULdHHAhCvZwCmHQ/bs/QKFIzUiIEH7XuPgaYJnDdkS0QbfdKl1SSjcTHj9oqYhqFnBhT7U+wDbI2dQ8F9TL0QdNZ/rF7WMzgKCqcbgbqfDWdSVUycD328ieYZ5AJwzH8FRYQhEBMx6zhNqVsJSQvBy1r36cS5vUqb1SIzsYw7aAoG/FlC+azAEA+5KqAwVCqZwIDAQAB";

const targets = {
  chrome: (manifest) => ({ ...manifest, key: CHROME_KEY, background: { service_worker: "background.js" } }),
  firefox: (manifest) => ({
    ...manifest,
    background: { scripts: ["background.js"] },
    browser_specific_settings: {
      gecko: {
        id: "extension@juvin.app",
        strict_min_version: "142.0",
        // Content goes only to the local Juvin app over native messaging, never to a server run by the extension.
        data_collection_permissions: { required: ["none"] },
      },
    },
  }),
};

const entryPoints = {
  background: "src/background/index.ts",
  content: "src/content/index.ts",
  popup: "src/popup/popup.ts",
  "test-page": "src/test-page/test-page.ts",
};

const staticFiles = ["src/popup/popup.html", "src/popup/popup.css", "src/test-page/test-page.html", "src/test-page/test-page.css"];

const ICON_SIZES = [16, 32, 48, 128];

async function writeStatic(outdir, target) {
  const manifest = JSON.parse(await readFile(join(root, "src", "manifest.json"), "utf8"));
  await writeFile(join(outdir, "manifest.json"), JSON.stringify(targets[target](manifest), null, 2) + "\n");
  for (const file of staticFiles) {
    await cp(join(root, file), join(outdir, file.split("/").pop()));
  }

  await mkdir(join(outdir, "icons"), { recursive: true });
  for (const size of ICON_SIZES) {
    await cp(join(brandPng, `juvin-mark-color-${size}.png`), join(outdir, "icons", `icon-${size}.png`));
  }
}

function options(outdir) {
  return {
    absWorkingDir: root,
    entryPoints,
    outdir,
    bundle: true,
    format: "iife",
    target: ["chrome120", "firefox142"],
    logLevel: "warning",
  };
}

for (const target of Object.keys(targets)) {
  const outdir = join(root, "dist", target);
  await rm(outdir, { recursive: true, force: true });
  await mkdir(outdir, { recursive: true });
  await writeStatic(outdir, target);
  if (watch) {
    const ctx = await context(options(outdir));
    await ctx.watch();
  } else {
    await build(options(outdir));
  }
}

console.log(watch ? "Watching for changes (static files are copied once)." : "Built dist/chrome and dist/firefox.");
