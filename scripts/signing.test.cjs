const test = require('node:test');
const assert = require('node:assert/strict');
const vm = require('node:vm');
const fs = require('node:fs');
const path = require('node:path');
const crypto = require('node:crypto');

// Exercise the actual CI restore function with synthetic bytes and an in-memory
// filesystem. Tests never load, copy, or log the maintainer's private signing key.
function fixture({ missing = false, mismatch = false } = {}) {
  const bytes = Buffer.alloc(200, 0x42);
  const certificate = Buffer.from('synthetic public certificate');
  const sha256 = crypto.createHash('sha256').update(certificate).digest('hex').toUpperCase();
  const writes = [], invocations = [], messages = [];
  const environment = { RUNNER_TEMP: 'C:/runner-temp', JAVA_HOME: 'C:/jdk-21',
    ANDROID_KEYSTORE_BASE64: bytes.toString('base64'), ANDROID_KEY_ALIAS: 'test-alias',
    ANDROID_KEYSTORE_PASSWORD: 'fixture-store-password', ANDROID_KEY_PASSWORD: 'fixture-key-password' };
  if (missing) delete environment.ANDROID_KEY_PASSWORD;
  const module = { exports: {} };
  const libraries = {
    'node:fs': {
      readFileSync: () => JSON.stringify({ sha256: mismatch ? '0'.repeat(64) : sha256 }),
      writeFileSync: (file, data, options) => writes.push({ file, data, options })
    },
    'node:path': path,
    'node:crypto': crypto,
    'node:child_process': { spawnSync: (file, args, options) => {
      invocations.push({ file, args, options });
      return { status: 0, stdout: certificate, stderr: Buffer.alloc(0) };
    } }
  };
  function requireFixture(name) { if (!(name in libraries)) throw new Error('Unexpected dependency: ' + name); return libraries[name]; }
  requireFixture.main = {};
  const source = fs.readFileSync(path.join(__dirname, 'signing.cjs'), 'utf8') + '\nmodule.exports.testRestoreCi = restoreCi;';
  vm.runInNewContext(source, { require: requireFixture, module, __dirname, Buffer,
    process: { env: environment, platform: 'win32' }, console: { log: value => messages.push(value) } });
  return { restore: module.exports.testRestoreCi, bytes, writes, invocations, messages };
}
test('CI restore decodes the keystore and verifies its public identity without logging passwords', () => {
  const f = fixture();
  f.restore();
  assert.equal(f.writes.length, 1);
  assert.deepEqual(f.writes[0].data, f.bytes);
  assert.equal(f.writes[0].options.mode, 0o600);
  assert.ok(f.invocations[0].args.includes('-storepass:env'));
  assert.ok(!f.invocations[0].args.includes('fixture-store-password'));
  assert.ok(!f.messages.join('').includes('fixture-store-password'));
});
test('missing CI signing secrets stop before a keystore file is written', () => {
  const f = fixture({ missing: true });
  assert.throws(f.restore, /Missing GitHub Actions secret ANDROID_KEY_PASSWORD/);
  assert.equal(f.writes.length, 0);
});
test('a different CI signing identity is rejected', () => {
  const f = fixture({ mismatch: true });
  assert.throws(f.restore, /does not match/);
});
