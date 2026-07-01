import crypto from "node:crypto";
import fs from "node:fs/promises";
import { parseReleaseTag } from "./release-tag.mjs";

const requiredEnv = ["UPDATE_SERVER_URL", "RELEASE_ADMIN_SECRET", "GITHUB_EVENT_PATH", "GITHUB_TOKEN"];
for (const name of requiredEnv) {
  if (!process.env[name]) {
    throw new Error(`${name} is required.`);
  }
}

const event = JSON.parse(await fs.readFile(process.env.GITHUB_EVENT_PATH, "utf8"));
const eventRelease = event.release;
if (!eventRelease?.url) {
  throw new Error("release.published event payload is missing release.url.");
}

const githubHeaders = {
  "Accept": "application/vnd.github+json",
  "Authorization": `Bearer ${process.env.GITHUB_TOKEN}`,
  "X-GitHub-Api-Version": "2022-11-28",
};

async function fetchJson(url) {
  const response = await fetch(url, { headers: githubHeaders });
  if (!response.ok) {
    const text = await response.text();
    throw new Error(`GitHub API request failed: ${response.status} ${text}`);
  }

  return response.json();
}

const release = await fetchJson(eventRelease.url);
const parsedTag = parseReleaseTag(release.tag_name);

if (Boolean(release.prerelease) !== parsedTag.prerelease) {
  throw new Error(`Release prerelease flag does not match tag channel '${parsedTag.channel}'.`);
}

const assetsUrl = release.assets_url ?? eventRelease.assets_url;
if (!assetsUrl) {
  throw new Error("Release payload is missing assets_url.");
}

const assets = await fetchJson(`${assetsUrl}?per_page=100`);
if (!Array.isArray(assets) || assets.length === 0) {
  throw new Error("Published release must contain at least one asset.");
}

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
  assets: assets.map((asset) => ({
    id: asset.id,
    name: asset.name,
    label: asset.label,
    content_type: asset.content_type,
    state: asset.state,
    size: asset.size,
    digest: asset.digest,
    browser_download_url: asset.browser_download_url,
  })),
};

const method = "PUT";
const pathname = `/admin/releases/${parsedTag.channel}/latest`;
const timestamp = new Date().toISOString();
const body = JSON.stringify(metadata);
const signedPayload = [method, pathname, timestamp, body].join("\n");
const signature = crypto
  .createHmac("sha256", process.env.RELEASE_ADMIN_SECRET)
  .update(signedPayload)
  .digest("hex");

const updateServerUrl = new URL(pathname, process.env.UPDATE_SERVER_URL);
const response = await fetch(updateServerUrl, {
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

console.log(`Published ${parsedTag.channel} latest metadata for ${parsedTag.version}.`);
