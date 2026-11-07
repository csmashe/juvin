const { test } = require('node:test');
const assert = require('node:assert/strict');
const fs = require('node:fs');
const os = require('node:os');
const path = require('node:path');
const { configuration, payloadFiles, signedFiles, parseOutputs } = require('./sign.cjs');

const trusted = {
  INPUT_POLICY: 'test-signing', GITHUB_REPOSITORY: 'csmashe/juvin',
  GITHUB_REF: 'refs/heads/main', GITHUB_EVENT_NAME: 'workflow_dispatch',
  RUNNER_ENVIRONMENT: 'github-hosted',
  'INPUT_ORGANIZATION-ID': 'example', 'INPUT_PROJECT-SLUG': 'example',
  'INPUT_ARTIFACT-CONFIGURATION-SLUG': 'example',
  'INPUT_CERTIFICATE-THUMBPRINT': 'A'.repeat(40), 'INPUT_API-TOKEN': 'test-placeholder',
};
test('reads GitHub action output blocks and rejects incomplete or ambiguous output', () => {
  assert.deepEqual(parseOutputs('artifact-id<<delimiter\r\n12345\r\ndelimiter\r\nurl=https://example.com/a=b\r\n'),
    { 'artifact-id': '12345', url: 'https://example.com/a=b' });
  assert.throws(() => parseOutputs('artifact-id<<missing\n123'), /Incomplete/);
  assert.throws(() => parseOutputs('artifact-id=1\nartifact-id=2'), /Duplicate/);
  assert.throws(() => parseOutputs('invalid'), /Invalid/);
});
test('unsigned builds need no signing credentials', () => {
  assert.deepEqual(configuration({}), { policy: 'none' });
});
test('untrusted signing contexts are rejected', () => {
  for (const override of [
    { GITHUB_REF: 'refs/pull/10/merge' }, { GITHUB_REF: 'refs/tags/v1.2.3' },
    { GITHUB_REF: 'refs/heads/feature' }, { GITHUB_EVENT_NAME: 'pull_request' },
    { GITHUB_REPOSITORY: 'someone/juvin' }, { RUNNER_ENVIRONMENT: 'self-hosted' },
  ]) assert.throws(() => configuration({ ...trusted, ...override }), /manual main-branch/);
});
test('missing credentials and invalid certificate pins fail closed', () => {
  for (const name of Object.keys(trusted).filter(x => x.startsWith('INPUT_') && x !== 'INPUT_POLICY')) {
    const env = { ...trusted }; delete env[name];
    assert.throws(() => configuration(env), /Missing/);
  }
  assert.throws(() => configuration({ ...trusted, 'INPUT_CERTIFICATE-THUMBPRINT': '*' }), /thumbprint/);
  assert.throws(() => configuration({ ...trusted, INPUT_POLICY: 'custom' }), /Invalid/);
  assert.equal(configuration(trusted).thumbprint, 'A'.repeat(40));
});
test('payload selection excludes upstream code and requires all launchers', () => {
  const dir = fs.mkdtempSync(path.join(os.tmpdir(), 'juvin-selection-'));
  try {
    const own = ['juvin.exe', 'juvin-host.exe', 'juvin-cli.exe', 'juvin.dll', 'Juvin.Core.dll'];
    for (const name of [...own, 'Avalonia.dll', 'System.Runtime.dll', 'libSkiaSharp.dll', 'LICENSE']) fs.writeFileSync(path.join(dir, name), 'fixture');
    assert.deepEqual(payloadFiles(dir), own.sort());
    fs.unlinkSync(path.join(dir, 'juvin-host.exe'));
    assert.throws(() => payloadFiles(dir), /Missing/);
  } finally { fs.rmSync(dir, { recursive: true, force: true }); }
});
test('signed results cannot add or omit files', () => {
  const dir = fs.mkdtempSync(path.join(os.tmpdir(), 'juvin-result-'));
  try {
    fs.writeFileSync(path.join(dir, 'juvin.exe'), 'fixture');
    signedFiles(dir, ['juvin.exe']);
    assert.throws(() => signedFiles(dir, ['juvin.exe', 'juvin-host.exe']), /Unexpected/);
    fs.mkdirSync(path.join(dir, 'extra'));
    assert.throws(() => signedFiles(dir, ['juvin.exe']), /Unexpected/);
    assert.throws(() => signedFiles(dir, ['juvin.exe', 'extra']), /ordinary files/);
  } finally { fs.rmSync(dir, { recursive: true, force: true }); }
});
