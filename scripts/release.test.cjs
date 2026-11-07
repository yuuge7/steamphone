const test = require('node:test');
const assert = require('node:assert/strict');
const fs = require('node:fs');
const os = require('node:os');
const path = require('node:path');
const crypto = require('node:crypto');
const { planVersion, renderChangelog, checkedManifest, publish } = require('./release.cjs');

const sha = 'a'.repeat(40), base = 'b'.repeat(40);
function plan(overrides = {}) {
  return planVersion({ major: 1, minor: 0, runNumber: 1, runAttempt: 1, eventName: 'push', event: {},
    repository: 'owner/steamphone', actor: 'owner', sha, ...overrides });
}
test('every run and attempt has a unique increasing Android code and tag', () => {
  const first = plan(), rerun = plan({ runAttempt: 2 }), next = plan({ runNumber: 2 });
  assert.equal(first.title, 'Steamphone v1.0.101');
  assert.equal(rerun.versionCode, 102);
  assert.equal(next.versionCode, 201);
  assert.equal(new Set([first.tag, rerun.tag, next.tag]).size, 3);
  assert.throws(() => plan({ runAttempt: 100 }));
  assert.throws(() => plan({ runNumber: 21000001 }));
});
test('own PRs publish prereleases; forks and Dependabot never receive production signing', () => {
  const event = { number: 7, pull_request: { number: 7, head: { sha, repo: { full_name: 'owner/steamphone' } }, base: { sha: base } } };
  const own = plan({ eventName: 'pull_request', event });
  assert.equal(own.title, 'Steamphone v1.0.101-pr.7');
  assert.equal(own.publish, true);
  assert.equal(own.prerelease, true);
  assert.equal(plan({ eventName: 'pull_request', event, actor: 'dependabot[bot]' }).publish, false);
  event.pull_request.head.repo.full_name = 'someone/fork';
  assert.equal(plan({ eventName: 'pull_request', event }).publish, false);
});
test('manual runs off main do not publish', () => {
  assert.equal(plan({ eventName: 'workflow_dispatch', event: { ref: 'refs/heads/feature' } }).publish, false);
  assert.equal(plan({ eventName: 'workflow_dispatch', event: { ref: 'refs/heads/main' } }).publish, true);
});
test('commit subjects remain literal Markdown and are never evaluated as commands', () => {
  const notes = renderChangelog(plan(), [{ sha, subject: 'fix: $(echo secret) <script> [link](bad) `code`' }], 'owner/steamphone', 'v1.0.1');
  assert.ok(notes.includes('$(echo secret)'));
  assert.ok(notes.includes('\\<script\\>'));
  assert.ok(notes.includes('\\[link\\]'));
  assert.ok(notes.includes('/compare/v1.0.1...'));
});

function fixture(t) {
  const directory = fs.mkdtempSync(path.join(os.tmpdir(), 'steamphone-release-'));
  t.after(() => fs.rmSync(directory, { recursive: true, force: true }));
  const current = plan();
  const assets = ['arm64', 'x64'].map(architecture => {
    const name = `Steamphone-v${current.version}-${architecture}.apk`, bytes = Buffer.from(`test APK ${architecture}`);
    fs.writeFileSync(path.join(directory, name), bytes);
    return { name, architecture, bytes: bytes.length, sha256: crypto.createHash('sha256').update(bytes).digest('hex') };
  });
  fs.writeFileSync(path.join(directory, 'manifest.json'), JSON.stringify({ app: 'Steamphone', version: current.version,
    versionCode: current.versionCode, commit: sha, productionSigned: true, assets }));
  fs.writeFileSync(path.join(directory, 'SHA256SUMS.txt'), assets.map(x => `${x.sha256}  ${x.name}`).join('\n'));
  const operations = [];
  const missing = async () => { throw Object.assign(new Error('Not found'), { status: 404 }); };
  const github = {
    rest: { git: { getRef: missing }, repos: {
      getReleaseByTag: missing, listReleases: () => {},
      createRelease: async value => { operations.push(['create', value]); return { data: { id: 1 } }; },
      uploadReleaseAsset: async value => { operations.push(['upload', value.name]); return {}; },
      updateRelease: async value => { operations.push(['publish', value]); return { data: { html_url: 'https://github.com/owner/steamphone/releases/tag/v1.0.101' } }; }
    } }, paginate: async () => []
  };
  return { directory, current, assets, operations, github,
    arguments: { github, context: { repo: { owner: 'owner', repo: 'steamphone' } }, core: { info() {} }, directory,
      plan: current, gitRunner: () => `${sha}\tfeat: Add import and export` } };
}
test('artifact validation rejects tampering before any GitHub mutation', t => {
  const f = fixture(t);
  assert.equal(checkedManifest(f.directory, f.current).assets.length, 2);
  fs.writeFileSync(path.join(f.directory, f.assets[0].name), 'tampered');
  assert.throws(() => checkedManifest(f.directory, f.current), /checksum mismatch/);
});
test('release stays a draft until APKs, manifest, checksums and changelog are uploaded', async t => {
  const f = fixture(t);
  await publish(f.arguments);
  assert.equal(f.operations[0][0], 'create');
  assert.equal(f.operations[0][1].draft, true);
  assert.equal(f.operations.filter(x => x[0] === 'upload').length, 5);
  assert.equal(f.operations.at(-1)[0], 'publish');
  assert.ok(fs.readFileSync(path.join(f.directory, 'CHANGELOG.md'), 'utf8').includes('feat: Add import and export'));
});
test('an existing published release is never overwritten', async t => {
  const f = fixture(t);
  f.github.rest.repos.getReleaseByTag = async () => ({ data: { id: 99 } });
  await assert.rejects(publish(f.arguments), /already exists/);
  assert.deepEqual(f.operations, []);
});
test('an existing tag is never reused or moved', async t => {
  const f = fixture(t);
  f.github.rest.git.getRef = async () => ({ data: { object: { sha } } });
  await assert.rejects(publish(f.arguments), /will not be moved/);
  assert.deepEqual(f.operations, []);
});
test('an upload failure never exposes an incomplete published release', async t => {
  const f = fixture(t);
  f.github.rest.repos.uploadReleaseAsset = async () => { throw new Error('Upload failed'); };
  await assert.rejects(publish(f.arguments), /Upload failed/);
  assert.equal(f.operations.some(x => x[0] === 'publish'), false);
});
test('stable release notes use the previous published ancestor; exclude drafts and prereleases', async t => {
  const f = fixture(t);
  f.github.paginate = async () => [
    { tag_name: 'v1.0.999', draft: true, prerelease: false },
    { tag_name: 'v1.0.888', draft: false, prerelease: true },
    { tag_name: 'v1.0.50', draft: false, prerelease: false }
  ];
  const calls = [];
  f.arguments.gitRunner = args => { calls.push(args); return args[0] === 'log' ? `${sha}\tfix: Preserve backup history` : ''; };
  await publish(f.arguments);
  assert.ok(calls.at(-1).includes(`v1.0.50..${sha}`));
  assert.ok(f.operations[0][1].body.includes('fix: Preserve backup history'));
});
