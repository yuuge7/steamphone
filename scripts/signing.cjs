const fs = require('node:fs');
const path = require('node:path');
const crypto = require('node:crypto');
const { spawnSync } = require('node:child_process');

const root = path.resolve(__dirname, '..');
const localPath = path.join(root, 'signing.local.json');
const certificatePath = path.join(root, 'signing-certificate.json');

function keytool() {
  const javaHome = process.env.JAVA_HOME || (process.platform === 'win32'
    ? path.join(process.env.LOCALAPPDATA || '', 'Microsoft', 'jdk-21') : '');
  const executable = process.platform === 'win32' ? 'keytool.exe' : 'keytool';
  return javaHome ? path.join(javaHome, 'bin', executable) : executable;
}
function runKeytool(args, passwords) {
  const result = spawnSync(keytool(), args, {
    env: { ...process.env, STEAMPHONE_STORE_PASSWORD: passwords.storePassword, STEAMPHONE_KEY_PASSWORD: passwords.keyPassword },
    maxBuffer: 1024 * 1024
  });
  if (result.error) throw result.error;
  if (result.status !== 0) throw new Error(`keytool failed: ${result.stderr.toString().trim()}`);
  return result.stdout;
}
function fingerprint(store, alias, passwords) {
  const certificate = runKeytool(['-exportcert', '-keystore', store, '-alias', alias,
    '-storepass:env', 'STEAMPHONE_STORE_PASSWORD'], passwords);
  return crypto.createHash('sha256').update(certificate).digest('hex').toUpperCase();
}
function readLocal() {
  const config = JSON.parse(fs.readFileSync(localPath, 'utf8'));
  const store = path.resolve(root, config.keystore);
  if (path.dirname(store) !== root) throw new Error('The local release keystore must be in the project root.');
  const expected = JSON.parse(fs.readFileSync(certificatePath, 'utf8'));
  if (fingerprint(store, config.alias, config) !== expected.sha256)
    throw new Error('This keystore does not match signing-certificate.json. Restore the original signing identity.');
  return { ...config, store };
}
function create() {
  const store = path.join(root, 'steamphone-release.keystore');
  if ([store, localPath, certificatePath].some(file => fs.existsSync(file)))
    throw new Error('Signing files already exist. They will not be replaced. Copy the original key to a new machine.');
  const password = crypto.randomBytes(32).toString('base64url');
  const config = { keystore: path.basename(store), alias: 'steamphone', storePassword: password, keyPassword: password };
  runKeytool(['-genkeypair', '-noprompt', '-storetype', 'JKS', '-keystore', store,
    '-alias', config.alias, '-keyalg', 'RSA', '-keysize', '4096', '-sigalg', 'SHA256withRSA',
    '-validity', '36500', '-dname', 'CN=Steamphone Release',
    '-storepass:env', 'STEAMPHONE_STORE_PASSWORD', '-keypass:env', 'STEAMPHONE_KEY_PASSWORD'], config);
  fs.writeFileSync(localPath, JSON.stringify(config, null, 2) + '\n', { mode: 0o600, flag: 'wx' });
  const sha256 = fingerprint(store, config.alias, config);
  fs.writeFileSync(certificatePath, JSON.stringify({ alias: config.alias, sha256, algorithm: 'RSA-4096 / SHA256' }, null, 2) + '\n', { flag: 'wx' });
  console.log('Created steamphone-release.keystore and signing.local.json. Back up both privately.');
  console.log(`Public certificate SHA-256: ${sha256}`);
}
function restoreCi() {
  const required = ['ANDROID_KEYSTORE_BASE64', 'ANDROID_KEY_ALIAS', 'ANDROID_KEYSTORE_PASSWORD', 'ANDROID_KEY_PASSWORD'];
  for (const name of required) if (!process.env[name])
    throw new Error(`Missing GitHub Actions secret ${name}. Run: node scripts/signing.cjs github OWNER/REPOSITORY`);
  if (!process.env.RUNNER_TEMP) throw new Error('RUNNER_TEMP is not set. Use this command only inside GitHub Actions.');
  const store = path.join(process.env.RUNNER_TEMP, 'steamphone-release.keystore');
  const bytes = Buffer.from(process.env.ANDROID_KEYSTORE_BASE64, 'base64');
  if (bytes.length < 100 || bytes.length > 32 * 1024) throw new Error('Invalid keystore secret.');
  fs.writeFileSync(store, bytes, { mode: 0o600 });
  const expected = JSON.parse(fs.readFileSync(certificatePath, 'utf8'));
  const passwords = { storePassword: process.env.ANDROID_KEYSTORE_PASSWORD, keyPassword: process.env.ANDROID_KEY_PASSWORD };
  if (fingerprint(store, process.env.ANDROID_KEY_ALIAS, passwords) !== expected.sha256)
    throw new Error('CI signing certificate does not match the public certificate in this repository.');
  console.log('Restored and verified the production signing identity.');
}
function configureGithub(repository) {
  if (!/^[A-Za-z0-9_.-]+\/[A-Za-z0-9_.-]+$/.test(repository || '')) throw new Error('Use: node scripts/signing.cjs github OWNER/REPOSITORY');
  const config = readLocal();
  const secrets = {
    ANDROID_KEYSTORE_BASE64: fs.readFileSync(config.store).toString('base64'),
    ANDROID_KEY_ALIAS: config.alias,
    ANDROID_KEYSTORE_PASSWORD: config.storePassword,
    ANDROID_KEY_PASSWORD: config.keyPassword
  };
  for (const [name, value] of Object.entries(secrets)) {
    const result = spawnSync('gh', ['secret', 'set', name, '--app', 'actions', '--repo', repository],
      { input: value, encoding: 'utf8', stdio: ['pipe', 'pipe', 'pipe'] });
    if (result.error) throw result.error;
    if (result.status !== 0) throw new Error(`Could not set ${name}: ${result.stderr.trim()}`);
    console.log(`Configured ${name}`);
  }
  console.log('All four signing secrets are ready. No commits or pushes were made.');
}
if (require.main === module) {
  try {
    if (process.argv[2] === 'create') create();
    else if (process.argv[2] === 'verify') { readLocal(); console.log('Local signing identity verified.'); }
    else if (process.argv[2] === 'restore-ci') restoreCi();
    else if (process.argv[2] === 'github') configureGithub(process.argv[3]);
    else throw new Error('Commands: create | verify | restore-ci | github OWNER/REPOSITORY');
  } catch (error) { console.error(error.message); process.exitCode = 1; }
}
module.exports = { fingerprint, readLocal };
