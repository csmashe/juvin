const { spawnSync } = require('node:child_process');
const path = require('node:path');
const { configuration } = require('../../../scripts/windows/signpath/sign.cjs');

try {
  const config = configuration(process.env);
  const root = process.env.GITHUB_WORKSPACE;
  const env = { ...process.env, JUVIN_SIGNPATH_POLICY: config.policy };
  if (config.policy !== 'none') {
    env.JUVIN_SIGNPATH_NODE = process.execPath;
    env.JUVIN_SIGNPATH_HELPER = path.join(root, 'scripts/windows/signpath/sign.cjs');
  }
  const result = spawnSync('pwsh', ['-NoLogo', '-NoProfile', '-File',
    path.join(root, 'scripts/build-windows-installer.ps1'),
    process.env.INPUT_VERSION, process.env['INPUT_OUTPUT-DIRECTORY'] || 'dist'],
  { env, cwd: root, stdio: 'inherit' });
  if (result.error) throw result.error;
  process.exitCode = result.status ?? 1;
} catch (error) {
  console.error(error.message);
  process.exitCode = 1;
}
