const fs = require('node:fs');
const os = require('node:os');
const path = require('node:path');
const crypto = require('node:crypto');
const { spawnSync } = require('node:child_process');

function configuration(env) {
  const policy = env.INPUT_POLICY || 'none';
  if (!['none', 'test-signing', 'release-signing'].includes(policy)) throw new Error('Invalid signing policy');
  if (policy === 'none') return { policy };
  if (env.GITHUB_REPOSITORY !== 'csmashe/juvin' || env.GITHUB_REF !== 'refs/heads/main' ||
      env.GITHUB_EVENT_NAME !== 'workflow_dispatch' || env.RUNNER_ENVIRONMENT !== 'github-hosted') {
    throw new Error('Signing requires a manual main-branch run on a GitHub-hosted runner');
  }
  for (const name of ['ORGANIZATION-ID', 'PROJECT-SLUG', 'ARTIFACT-CONFIGURATION-SLUG', 'CERTIFICATE-THUMBPRINT', 'API-TOKEN']) {
    if (!env[`INPUT_${name}`]) throw new Error(`Missing SignPath input: ${name}`);
  }
  const thumbprint = env['INPUT_CERTIFICATE-THUMBPRINT'].replace(/\s/g, '').toUpperCase();
  if (!/^[A-F0-9]{40}$/.test(thumbprint)) throw new Error('Expected a 40-character certificate thumbprint');
  return { policy, thumbprint };
}

function payloadFiles(directory) {
  const files = fs.readdirSync(directory).filter(name =>
    ['juvin.exe', 'juvin-host.exe', 'juvin-cli.exe', 'juvin.dll'].includes(name) || /^Juvin(?:\.[A-Za-z0-9]+)*\.dll$/.test(name));
  for (const name of ['juvin.exe', 'juvin-host.exe', 'juvin-cli.exe', 'juvin.dll', 'Juvin.Core.dll']) {
    if (!files.includes(name)) throw new Error(`Missing owned payload file: ${name}`);
  }
  return files.sort();
}

function signedFiles(directory, expected) {
  const actual = fs.readdirSync(directory).sort();
  if (JSON.stringify(actual) !== JSON.stringify([...expected].sort())) throw new Error('Unexpected signed artifact contents');
  for (const name of actual) {
    const stat = fs.lstatSync(path.join(directory, name));
    if (!stat.isFile() || stat.isSymbolicLink()) throw new Error('Signed artifact must contain ordinary files only');
  }
}

function run(command, args, env = process.env) {
  const result = spawnSync(command, args, { env, stdio: 'inherit' });
  if (result.error) throw result.error;
  if (result.status !== 0) throw new Error(`${path.basename(command)} failed (${result.status})`);
}

function parseOutputs(text) {
  const lines = text.split(/\r?\n/);
  const result = {};
  for (let i = 0; i < lines.length; i++) {
    if (!lines[i]) continue;
    const block = /^([A-Za-z0-9_-]+)<<(.+)$/.exec(lines[i]);
    let name, value;
    if (block) {
      name = block[1];
      const parts = [];
      while (++i < lines.length && lines[i] !== block[2]) parts.push(lines[i]);
      if (i === lines.length) throw new Error('Incomplete action output block');
      value = parts.join('\n');
    } else {
      const pair = /^([A-Za-z0-9_-]+)=(.*)$/.exec(lines[i]);
      if (!pair) throw new Error('Invalid action output');
      [, name, value] = pair;
    }
    if (Object.hasOwn(result, name)) throw new Error('Duplicate action output');
    result[name] = value;
  }
  return result;
}

function invokeAction(entry, inputs, directory) {
  const output = path.join(directory, `outputs-${crypto.randomUUID()}.txt`);
  fs.writeFileSync(output, '');
  const env = { ...process.env, GITHUB_OUTPUT: output };
  for (const [name, value] of Object.entries(inputs)) env[`INPUT_${name.toUpperCase()}`] = String(value);
  run(process.execPath, [entry], env);
  return parseOutputs(fs.readFileSync(output, 'utf8'));
}

function verify(directory, files, config, metadataOnly = false) {
  const args = ['-NoLogo', '-NoProfile', '-File', path.join(__dirname, 'verify.ps1'),
    '-Directory', directory, '-ProductVersion', process.env.JUVIN_SIGNPATH_PRODUCT_VERSION,
    '-Policy', config.policy, '-Thumbprint', config.thumbprint];
  if (metadataOnly) args.push('-MetadataOnly');
  run('pwsh', args);
  signedFiles(directory, files);
}

function sign(kind, target) {
  const config = configuration(process.env);
  if (config.policy === 'none') throw new Error('Signing callback cannot run in unsigned mode');
  const absolute = fs.realpathSync(target);
  let files;
  let names;
  if (kind === 'payload') {
    files = payloadFiles(absolute).map(name => path.join(absolute, name));
    names = files.map(file => path.basename(file));
  } else if (kind === 'inno') {
    const setup = path.resolve(process.env.JUVIN_SIGNPATH_INSTALLER);
    const uninstallers = fs.realpathSync(process.env.JUVIN_SIGNPATH_UNINSTALLERS);
    if (absolute.toLowerCase() === setup.toLowerCase()) names = ['setup.exe'];
    else if (path.dirname(absolute).toLowerCase() === uninstallers.toLowerCase()) names = ['uninstaller.exe'];
    else throw new Error('Unexpected Inno signing target');
    files = [absolute];
  } else throw new Error('Invalid signing callback kind');

  const work = fs.mkdtempSync(path.join(os.tmpdir(), 'juvin-signpath-'));
  const input = path.join(work, 'input');
  const output = path.join(work, 'signed');
  fs.mkdirSync(input);
  try {
    files.forEach((file, i) => fs.copyFileSync(file, path.join(input, names[i])));
    verify(input, names, config, true);
    const tools = process.env.JUVIN_SIGNPATH_TOOLS;
    if (!tools) throw new Error('Missing pinned SignPath action checkout');
    const uploaded = invokeAction(path.join(tools, 'upload/dist/upload/index.js'), {
      name: `juvin-unsigned-${kind}-${crypto.randomUUID()}`, path: input,
      'if-no-files-found': 'error', 'retention-days': 7, 'compression-level': 0,
      overwrite: false, 'include-hidden-files': false, archive: true,
    }, work);
    if (!/^\d+$/.test(uploaded['artifact-id'] || '')) throw new Error('Artifact upload did not return an ID');
    const result = invokeAction(path.join(tools, 'submit/index.js'), {
      'connector-url': 'https://pipelineconnector.connectors.signpath.io/GitHubActions/GitHubCom',
      'api-token': process.env['INPUT_API-TOKEN'],
      'organization-id': process.env['INPUT_ORGANIZATION-ID'],
      'project-slug': process.env['INPUT_PROJECT-SLUG'],
      'signing-policy-slug': config.policy,
      'artifact-configuration-slug': process.env['INPUT_ARTIFACT-CONFIGURATION-SLUG'],
      'github-artifact-id': uploaded['artifact-id'],
      'github-token': process.env.GITHUB_TOKEN,
      'wait-for-completion': true, 'wait-for-completion-timeout-in-seconds': 1800,
      'service-unavailable-timeout-in-seconds': 600,
      'download-signed-artifact-timeout-in-seconds': 300, 'skip-decompress': false,
      'output-artifact-directory': output,
      parameters: `version: ${JSON.stringify(process.env.JUVIN_SIGNPATH_PRODUCT_VERSION)}`,
    }, work);
    signedFiles(output, names);
    verify(output, names, config);
    files.forEach((file, i) => fs.copyFileSync(path.join(output, names[i]), file));
    if (result['signing-request-web-url'] && process.env.GITHUB_STEP_SUMMARY) {
      const url = new URL(result['signing-request-web-url']);
      if (url.protocol === 'https:' && url.hostname === 'app.signpath.io' && !url.search && !url.hash) {
        fs.appendFileSync(process.env.GITHUB_STEP_SUMMARY, `\n${kind}: ${url.href}\n`);
      }
    }
  } finally { fs.rmSync(work, { recursive: true, force: true }); }
}

if (require.main === module) {
  try { sign(process.argv[2], process.argv[3]); }
  catch (error) { console.error(error.message); process.exitCode = 1; }
}
module.exports = { configuration, payloadFiles, signedFiles, parseOutputs };
