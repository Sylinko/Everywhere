import crypto from "node:crypto";
import fs from "node:fs/promises";
import path from "node:path";
import { fileURLToPath } from "node:url";
import { syncReleaseAssetsToCnb } from "./cnb-release-sync.mjs";
import { parseReleaseTag } from "./release-tag.mjs";

export const DEFAULT_GITHUB_REPOSITORY = "Sylinko/Everywhere";
export const DEFAULT_GITHUB_API_BASE_URL = "https://api.github.com";

function parseCliArgs(argv) {
  let tag;
  let skipUpdateServer = false;
  let resolveOnly = false;

  for (let i = 0; i < argv.length; i++) {
    const arg = argv[i];
    if (arg === "--tag" && i + 1 < argv.length) {
      tag = argv[++i];
    } else if (arg.startsWith("--tag=")) {
      tag = arg.slice("--tag=".length);
    } else if (arg === "--skip-update-server") {
      skipUpdateServer = true;
    } else if (arg === "--resolve-only") {
      resolveOnly = true;
    } else if (arg && !arg.startsWith("-") && !tag) {
      tag = arg;
    }
  }

  return { tag, skipUpdateServer, resolveOnly };
}

function isFullCommitSha(value) {
  return typeof value === "string" && /^[0-9a-f]{40}$/i.test(value.trim());
}

async function resolveGitHubTagCommit({ githubApiBase, githubRepo, tagName, githubToken, fetchImpl }) {
  const refUrl = `${githubApiBase}/repos/${githubRepo}/git/ref/tags/${encodeURIComponent(tagName)}`;
  const ref = await fetchGitHubJson(refUrl, { githubToken, fetchImpl });

  let object = ref?.object;
  for (let depth = 0; depth < 5 && object?.type === "tag" && object?.sha; depth++) {
    const tagUrl = `${githubApiBase}/repos/${githubRepo}/git/tags/${encodeURIComponent(object.sha)}`;
    const tagObject = await fetchGitHubJson(tagUrl, { githubToken, fetchImpl });
    object = tagObject?.object;
  }

  if (object?.type !== "commit" || !isFullCommitSha(object?.sha)) {
    throw new Error(`Could not resolve GitHub tag '${tagName}' to a commit SHA.`);
  }

  return object.sha.trim().toLowerCase();
}

function createGitHubHeaders(githubToken) {
  const headers = {
    "Accept": "application/vnd.github+json",
    "User-Agent": "Everywhere-Update-Publisher/1.0",
    "X-GitHub-Api-Version": "2022-11-28",
  };

  if (githubToken) {
    headers.Authorization = `Bearer ${githubToken}`;
  }

  return headers;
}

async function fetchGitHubJson(url, { githubToken, fetchImpl }) {
  const response = await fetchImpl(url, {
    headers: createGitHubHeaders(githubToken),
  });
  if (!response.ok) {
    const text = await response.text();
    throw new Error(`GitHub API request failed: ${response.status} ${text}`);
  }

  return response.json();
}

/**
 * Resolves a GitHub release (from release.published event payload or explicit tag backfill),
 * syncs assets to the CNB mirror, attaches verified `mirrors.cn`, and publishes metadata
 * to download-center.
 */
export async function runUpdateServerPublish({
  env = process.env,
  argv = [],
  fetchImpl = fetch,
  logger = console,
  retryDelayMs = 500,
} = {}) {
  const cli = parseCliArgs(argv);
  const explicitTag = (cli.tag || env.RELEASE_TAG || "").trim();
  const skipUpdateServer = cli.skipUpdateServer || env.SKIP_UPDATE_SERVER === "true";
  const resolveOnly = cli.resolveOnly;

  const requiredEnv = ["GITHUB_TOKEN"];
  if (!skipUpdateServer && !resolveOnly) {
    requiredEnv.push("UPDATE_SERVER_URL", "RELEASE_ADMIN_SECRET");
  }
  if (!explicitTag) {
    requiredEnv.push("GITHUB_EVENT_PATH");
  }

  for (const name of requiredEnv) {
    if (!env[name]) {
      throw new Error(`${name} is required.`);
    }
  }

  const githubToken = env.GITHUB_TOKEN;
  const githubRepo = (env.GITHUB_REPOSITORY || DEFAULT_GITHUB_REPOSITORY).replace(/^\/+|\/+$/g, "");
  const githubApiBase = (env.GITHUB_API_BASE_URL || DEFAULT_GITHUB_API_BASE_URL).replace(/\/+$/, "");

  let releaseUrl;
  let fallbackAssetsUrl;
  if (explicitTag) {
    releaseUrl = `${githubApiBase}/repos/${githubRepo}/releases/tags/${encodeURIComponent(explicitTag)}`;
  } else {
    const event = JSON.parse(await fs.readFile(env.GITHUB_EVENT_PATH, "utf8"));
    const eventRelease = event.release;
    if (!eventRelease?.url) {
      throw new Error("release.published event payload is missing release.url.");
    }
    releaseUrl = eventRelease.url;
    fallbackAssetsUrl = eventRelease.assets_url;
  }

  const release = await fetchGitHubJson(releaseUrl, { githubToken, fetchImpl });
  if (release.draft === true) {
    throw new Error(`Refusing to publish draft release '${release.tag_name}'.`);
  }

  const parsedTag = parseReleaseTag(release.tag_name);
  if (Boolean(release.prerelease) !== parsedTag.prerelease) {
    throw new Error(`Release prerelease flag does not match tag channel '${parsedTag.channel}'.`);
  }

  const assetsUrl = release.assets_url ?? fallbackAssetsUrl;
  if (!assetsUrl) {
    throw new Error("Release payload is missing assets_url.");
  }

  const assets = await fetchGitHubJson(`${assetsUrl}?per_page=100`, { githubToken, fetchImpl });
  if (!Array.isArray(assets) || assets.length === 0) {
    throw new Error("Published release must contain at least one asset.");
  }

  const expectedTagCommit = isFullCommitSha(env.EXPECTED_TAG_COMMIT)
    ? env.EXPECTED_TAG_COMMIT.trim().toLowerCase()
    : await resolveGitHubTagCommit({
        githubApiBase,
        githubRepo,
        tagName: release.tag_name,
        githubToken,
        fetchImpl,
      });

  if (resolveOnly) {
    if (env.GITHUB_OUTPUT) {
      await fs.appendFile(
        env.GITHUB_OUTPUT,
        `tag_name=${release.tag_name}\ntag_commit=${expectedTagCommit}\n`,
        "utf8",
      );
    }

    logger.log(
      `Validated GitHub release '${release.tag_name}' (${parsedTag.channel} ${parsedTag.version}, commit ${expectedTagCommit}).`,
    );
    return {
      resolvedOnly: true,
      tagName: release.tag_name,
      tagCommit: expectedTagCommit,
      channel: parsedTag.channel,
      version: parsedTag.version,
    };
  }

  const cnbSyncResult = await syncReleaseAssetsToCnb({
    release,
    assets,
    cnbToken: env.CNB_TOKEN,
    githubToken,
    expectedTagCommit,
    cnbTagCommit: env.CNB_TAG_COMMIT,
    makeLatest: !skipUpdateServer && !release.prerelease,
    timeoutMs: Number(env.CNB_SYNC_TIMEOUT_MS) > 0 ? Number(env.CNB_SYNC_TIMEOUT_MS) : undefined,
    cnbRepo: env.CNB_REPO,
    apiBaseUrl: env.CNB_API_BASE_URL,
    webBaseUrl: env.CNB_WEB_BASE_URL,
    fetchImpl,
    retryDelayMs,
    logger,
  });

  const metadata = {
    channel: parsedTag.channel,
    version: parsedTag.version,
    releaseId: release.id,
    tagName: release.tag_name,
    publishedAt: release.published_at,
    releaseNotes: release.body,
    id: release.id,
    tag_name: release.tag_name,
    name: release.name,
    body: release.body,
    published_at: release.published_at,
    html_url: release.html_url,
    prerelease: release.prerelease,
    draft: release.draft,
    assets: assets.map((asset) => {
      const verifiedMirrorUrl = cnbSyncResult.verifiedMirrors.get(asset.name);
      return {
        id: asset.id,
        name: asset.name,
        label: asset.label,
        content_type: asset.content_type,
        state: asset.state,
        size: asset.size,
        digest: asset.digest,
        browser_download_url: asset.browser_download_url,
        ...(verifiedMirrorUrl ? { mirrors: { cn: verifiedMirrorUrl } } : {}),
      };
    }),
  };

  if (skipUpdateServer) {
    if (cnbSyncResult.verifiedMirrors.size === 0) {
      const detail = cnbSyncResult.error || cnbSyncResult.reason || "no assets verified";
      throw new Error(`Mirror-only CNB publish failed for '${release.tag_name}': ${detail}.`);
    }

    logger.log(
      `Skipped update server publish for ${parsedTag.channel} ${parsedTag.version} (${cnbSyncResult.verifiedMirrors.size} verified CNB mirrors).`,
    );
    return { metadata, cnbSyncResult, skippedUpdateServer: true };
  }

  const method = "PUT";
  const pathname = `/admin/releases/${parsedTag.channel}/latest`;
  const timestamp = new Date().toISOString();
  const body = JSON.stringify(metadata);
  const signedPayload = [method, pathname, timestamp, body].join("\n");
  const signature = crypto
    .createHmac("sha256", env.RELEASE_ADMIN_SECRET)
    .update(signedPayload)
    .digest("hex");

  const updateServerUrl = new URL(pathname, env.UPDATE_SERVER_URL);
  const response = await fetchImpl(updateServerUrl.toString(), {
    method,
    headers: {
      "Content-Type": "application/json",
      "X-Everywhere-Timestamp": timestamp,
      "X-Everywhere-Signature": `v1=${signature}`,
    },
    body,
  });

  if (!response.ok) {
    const text = await response.text();
    throw new Error(`Update server publish failed: ${response.status} ${text}`);
  }

  logger.log(
    `Published ${parsedTag.channel} latest metadata for ${parsedTag.version} (${cnbSyncResult.verifiedMirrors.size}/${assets.length} CNB mirrors attached).`,
  );

  return { metadata, cnbSyncResult, skippedUpdateServer: false };
}

function isDirectExecution() {
  if (!process.argv[1]) {
    return false;
  }

  return path.resolve(process.argv[1]) === fileURLToPath(import.meta.url);
}

if (isDirectExecution()) {
  await runUpdateServerPublish({ argv: process.argv.slice(2) });
}
