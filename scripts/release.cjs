const fs = require('node:fs');
const path = require('node:path');
const crypto = require('node:crypto');
const { execFileSync } = require('node:child_process');

function planVersion({ major, minor, runNumber, runAttempt, eventName, event, repository, actor, sha }) {
  for (const value of [major, minor, runNumber, runAttempt])
    if (!Number.isSafeInteger(value) || value < 0) throw new Error('Version inputs must be nonnegative integers.');
  if (runNumber < 1 || runAttempt < 1 || runAttempt > 99) throw new Error('A run supports attempts 1–99. Start a new workflow run.');
  const code = runNumber * 100 + runAttempt;
  if (code > 2100000000) throw new Error('Android versionCode limit exceeded.');
  const pr = eventName === 'pull_request';
  const number = pr ? event.pull_request?.number ?? event.number : null;
  if (pr && (!Number.isSafeInteger(number) || number < 1)) throw new Error('Invalid pull request event.');
  const version = `${major}.${minor}.${code}${pr ? `-pr.${number}` : ''}`;
  const publish = pr
    ? event.pull_request?.head?.repo?.full_name === repository && actor !== 'dependabot[bot]'
    : eventName === 'push' || (eventName === 'workflow_dispatch' && event.ref === 'refs/heads/main');
  const commit = pr ? event.pull_request?.head?.sha : sha;
  if (!/^[a-f0-9]{40}$/.test(commit || '')) throw new Error('A full source commit SHA is required.');
  return { version, versionCode: code, tag: `v${version}`, title: `Steamphone v${version}`, prerelease: pr,
    publish, commit, prNumber: number, baseSha: pr ? event.pull_request.base.sha : null };
}
function readPrefix(root) {
  const xml = fs.readFileSync(path.join(root, 'Version.props'), 'utf8');
  const major = xml.match(/<SteamphoneVersionMajor>(\d+)<\/SteamphoneVersionMajor>/);
  const minor = xml.match(/<SteamphoneVersionMinor>(\d+)<\/SteamphoneVersionMinor>/);
  if (!major || !minor) throw new Error('Version.props must declare numeric major/minor values.');
  return { major: Number(major[1]), minor: Number(minor[1]) };
}
function planFromActions({ context, core }) {
  const root = process.env.GITHUB_WORKSPACE || path.resolve(__dirname, '..');
  const plan = planVersion({ ...readPrefix(root), runNumber: Number(process.env.GITHUB_RUN_NUMBER),
    runAttempt: Number(process.env.GITHUB_RUN_ATTEMPT), eventName: context.eventName,
    event: { ...context.payload, ref: process.env.GITHUB_REF }, repository: `${context.repo.owner}/${context.repo.repo}`,
    actor: context.actor, sha: context.sha });
  fs.mkdirSync(path.join(root, 'artifacts'), { recursive: true });
  fs.writeFileSync(path.join(root, 'artifacts', 'release-plan.json'), JSON.stringify(plan, null, 2));
  for (const [name, value] of Object.entries(plan)) core.setOutput(name, String(value ?? ''));
  core.info(`${plan.title}; Android versionCode ${plan.versionCode}; publish=${plan.publish}`);
  return plan;
}
function escapeMarkdown(text) {
  return text.replace(/[\u0000-\u001f\u007f]/g, ' ').slice(0, 400).replace(/[\\`*_{}\[\]<>]/g, '\\$&');
}
function renderChangelog(plan, commits, repository, previousTag) {
  const url = `https://github.com/${repository}`;
  const rows = commits.slice(0, 200).map(commit =>
    `- ${escapeMarkdown(commit.subject)} ([${commit.sha.slice(0, 7)}](${url}/commit/${commit.sha}))`);
  const changes = rows.length ? rows.join('\n') : 'No new commits since the previous release; this is a new build.';
  const compare = previousTag ? `\n[Full comparison](${url}/compare/${encodeURIComponent(previousTag)}...${plan.commit})\n` : '';
  return `# ${plan.title}\n\n## Changes\n\n${changes}\n${commits.length > 200 ? '\nAdditional commits are included in the full comparison.\n' : ''}${compare}\n## Installation\n\n` +
    `Choose the **arm64** APK for most Android phones, or **x64** for an Android emulator.\n` +
    `Android 8.0 or newer is required. APKs use the project's permanent signing certificate.\n\n` +
    `Settings → Backup și transfer can export/import a JSON backup. Steam credentials are excluded.\n\n` +
    `Android versionCode: **${plan.versionCode}**. Source: [${plan.commit.slice(0, 7)}](${url}/commit/${plan.commit}).\n`;
}
function checkedManifest(directory, plan) {
  const manifest = JSON.parse(fs.readFileSync(path.join(directory, 'manifest.json'), 'utf8').replace(/^\uFEFF/, ''));
  if (manifest.app !== 'Steamphone' || manifest.version !== plan.version || manifest.versionCode !== plan.versionCode ||
      manifest.commit !== plan.commit || manifest.productionSigned !== true || !Array.isArray(manifest.assets) || manifest.assets.length !== 2)
    throw new Error('Release manifest does not match this workflow or is not production-signed.');
  const architectures = new Set();
  for (const asset of manifest.assets) {
    if (!['arm64', 'x64'].includes(asset.architecture) || architectures.has(asset.architecture) ||
        asset.name !== `Steamphone-v${plan.version}-${asset.architecture}.apk`)
      throw new Error('Unexpected or duplicate release asset.');
    architectures.add(asset.architecture);
    const bytes = fs.readFileSync(path.join(directory, asset.name));
    if (bytes.length !== asset.bytes || crypto.createHash('sha256').update(bytes).digest('hex') !== asset.sha256)
      throw new Error(`Artifact checksum mismatch: ${asset.name}`);
  }
  return manifest;
}
function git(args, root) {
  return execFileSync('git', args, { cwd: root, encoding: 'utf8', maxBuffer: 16 * 1024 * 1024 }).trim();
}
async function publish({ github, context, core, directory, plan, gitRunner = git, root = process.cwd() }) {
  if (!plan.publish) throw new Error('This event is not authorized to publish a release.');
  const manifest = checkedManifest(directory, plan);
  const repository = `${context.repo.owner}/${context.repo.repo}`;
  const repo = context.repo;
  try {
    await github.rest.repos.getReleaseByTag({ ...repo, tag: plan.tag });
    throw new Error(`Release ${plan.tag} already exists. Re-run all jobs for a new version; existing releases are never overwritten.`);
  } catch (error) { if (error.status !== 404) throw error; }
  try {
    await github.rest.git.getRef({ ...repo, ref: `tags/${plan.tag}` });
    throw new Error(`Tag ${plan.tag} already exists and will not be moved.`);
  } catch (error) { if (error.status !== 404) throw error; }
  let previousTag = null;
  let range;
  if (plan.prerelease) {
    range = `${plan.baseSha}..${plan.commit}`;
  } else {
    const releases = await github.paginate(github.rest.repos.listReleases, { ...repo, per_page: 100 });
    const stable = releases.filter(x => !x.draft && !x.prerelease && /^v\d+\.\d+\.\d+$/.test(x.tag_name))
      .sort((a, b) => {
        const av = a.tag_name.slice(1).split('.').map(Number), bv = b.tag_name.slice(1).split('.').map(Number);
        return bv[0] - av[0] || bv[1] - av[1] || bv[2] - av[2];
      });
    for (const candidate of stable) {
      try { gitRunner(['merge-base', '--is-ancestor', candidate.tag_name, plan.commit], root); previousTag = candidate.tag_name; break; }
      catch { /* Ignore releases on unrelated history. */ }
    }
    range = previousTag ? `${previousTag}..${plan.commit}` : plan.commit;
  }
  const log = gitRunner(['log', '--max-count=201', '--format=%H%x09%s', range], root);
  const commits = log.split(/\r?\n/).filter(Boolean).map(row => {
    const tab = row.indexOf('\t');
    const sha = row.slice(0, tab);
    if (tab < 0 || !/^[a-f0-9]{40}$/.test(sha)) throw new Error('Unexpected git log output.');
    return { sha, subject: row.slice(tab + 1) };
  });
  const notes = renderChangelog(plan, commits, repository, previousTag);
  fs.writeFileSync(path.join(directory, 'CHANGELOG.md'), notes);
  const created = await github.rest.repos.createRelease({ ...repo, tag_name: plan.tag, target_commitish: plan.commit,
    name: plan.title, body: notes, draft: true, prerelease: plan.prerelease });
  const releaseId = created.data.id;
  for (const name of [...manifest.assets.map(x => x.name), 'manifest.json', 'SHA256SUMS.txt', 'CHANGELOG.md']) {
    const bytes = fs.readFileSync(path.join(directory, name));
    await github.rest.repos.uploadReleaseAsset({ ...repo, release_id: releaseId, name, data: bytes,
      headers: { 'content-type': name.endsWith('.apk') ? 'application/vnd.android.package-archive' : 'application/octet-stream', 'content-length': bytes.length } });
  }
  // Only publish after every asset was uploaded. There is no replace/clobber operation.
  const result = await github.rest.repos.updateRelease({ ...repo, release_id: releaseId, draft: false,
    make_latest: plan.prerelease ? 'false' : 'legacy' });
  core.info(`Published ${result.data.html_url}`);
  return result.data;
}
module.exports = { planVersion, readPrefix, planFromActions, escapeMarkdown, renderChangelog, checkedManifest, publish };
