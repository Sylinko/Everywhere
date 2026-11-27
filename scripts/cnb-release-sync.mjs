import crypto from "node:crypto";
import { openAsBlob } from "node:fs";
import fs from "node:fs/promises";
import os from "node:os";
import path from "node:path";

export const DEFAULT_CNB_REPO = "sylinko/Everywhere";
export const DEFAULT_CNB_API_BASE_URL = "https://api.cnb.cool";
export const DEFAULT_CNB_WEB_BASE_URL = "https://cnb.cool";

const REDIRECT_STATUS_CODES = new Set([301, 302, 303, 307, 308]);
const GITHUB_AUTH_HOSTS = new Set(["github.com", "api.github.com", "uploads.github.com"]);

/**
 * Builds the canonical anonymous CNB release asset download URL expected by download-center.
 * @param {string} tagName
 * @param {string} assetName
 * @param {{ webBaseUrl?: string, cnbRepo?: string }} [options]
 * @returns {string}
 */
export function buildCnbAssetDownloadUrl(
  tagName,
  assetName,
  { webBaseUrl = DEFAULT_CNB_WEB_BASE_URL, cnbRepo = DEFAULT_CNB_REPO } = {},
) {
  const normalizedBase = webBaseUrl.replace(/\/+$/, "");
  const normalizedRepo = cnbRepo.replace(/^\/+|\/+$/g, "");
  return `${normalizedBase}/${normalizedRepo}/-/releases/download/${encodeURIComponent(tagName)}/${encodeURIComponent(assetName)}`;
}

/**
 * Parses a SHA-256 digest string such as "sha256:<64-hex>" or "<64-hex>".
 * @param {string | null | undefined} digest
 * @returns {string | null}
 */
export function parseSha256Digest(digest) {
  if (typeof digest !== "string") {
    return null;
  }

  const trimmed = digest.trim().toLowerCase();
  const hex = trimmed.startsWith("sha256:") ? trimmed.slice("sha256:".length) : trimmed;
  return /^[0-9a-f]{64}$/.test(hex) ? hex : null;
}

function assertHttpsUrl(rawUrl, contextLabel) {
  let parsed;
  try {
    parsed = new URL(rawUrl);
  } catch {
    throw new Error(`${contextLabel} is not a valid URL: ${rawUrl}`);
  }

  if (parsed.protocol !== "https:") {
    throw new Error(`${contextLabel} must use HTTPS (got ${parsed.protocol}): ${rawUrl}`);
  }

  return parsed;
}

async function cancelBodySafely(response) {
  try {
    await response?.body?.cancel?.();
  } catch {
    // Best-effort stream cleanup.
  }
}

function parseContentRangeTotal(headerValue) {
  if (typeof headerValue !== "string") {
    return null;
  }

  const match = headerValue.trim().match(/^bytes\s+\d+-\d+\/(\d+|\*)$/i);
  if (!match || match[1] === "*") {
    return null;
  }

  const parsed = Number(match[1]);
  return Number.isFinite(parsed) ? parsed : null;
}

/**
 * Verifies that an anonymous client (no Authorization, Cookie, or Referer)
 * can fetch the CNB download URL over HTTPS-only redirects and receive binary bytes.
 * @param {string} url
 * @param {{ expectedSize?: number, fetchImpl?: typeof fetch, maxRedirects?: number }} [options]
 * @returns {Promise<{ ok: boolean, status?: number, finalUrl?: string, reason?: string }>}
 */
export async function verifyAnonymousCnbAssetUrl(
  url,
  { expectedSize, fetchImpl = fetch, maxRedirects = 5 } = {},
) {
  let currentUrl;
  try {
    currentUrl = assertHttpsUrl(url, "CNB asset URL").toString();
  } catch (error) {
    return { ok: false, reason: error.message };
  }

  for (let hop = 0; hop <= maxRedirects; hop++) {
    let response;
    try {
      response = await fetchImpl(currentUrl, {
        method: "GET",
        headers: {
          "Range": "bytes=0-1023",
        },
        redirect: "manual",
      });
    } catch (error) {
      return { ok: false, reason: `Request failed: ${error.message}` };
    }

    if (REDIRECT_STATUS_CODES.has(response.status)) {
      const location = response.headers?.get?.("location");
      await cancelBodySafely(response);

      if (!location) {
        return { ok: false, status: response.status, reason: "Redirect response is missing Location header." };
      }

      if (hop === maxRedirects) {
        return { ok: false, status: response.status, reason: `Exceeded ${maxRedirects} redirects.` };
      }

      let nextUrl;
      try {
        const resolved = new URL(location, currentUrl).toString();
        nextUrl = assertHttpsUrl(resolved, "CNB redirect target").toString();
      } catch (error) {
        return { ok: false, status: response.status, reason: error.message };
      }

      currentUrl = nextUrl;
      continue;
    }

    const contentType = String(response.headers?.get?.("content-type") ?? "").toLowerCase();
    const contentRange = response.headers?.get?.("content-range");
    const contentLengthHeader = response.headers?.get?.("content-length");
    await cancelBodySafely(response);

    if (response.status !== 206 && response.status !== 200) {
      return {
        ok: false,
        status: response.status,
        finalUrl: currentUrl,
        reason: `Unexpected HTTP status ${response.status}.`,
      };
    }

    if (contentType.includes("text/html") || contentType.includes("application/xhtml+xml")) {
      return {
        ok: false,
        status: response.status,
        finalUrl: currentUrl,
        reason: `Unexpected HTML response (${contentType}) instead of binary asset.`,
      };
    }

    if (typeof expectedSize === "number" && expectedSize > 0) {
      if (response.status === 206 && contentRange) {
        const total = parseContentRangeTotal(contentRange);
        if (total !== null && total !== expectedSize) {
          return {
            ok: false,
            status: response.status,
            finalUrl: currentUrl,
            reason: `Content-Range total ${total} does not match expected size ${expectedSize}.`,
          };
        }
      } else if (response.status === 200 && contentLengthHeader !== null && contentLengthHeader !== undefined) {
        const actualLength = Number(contentLengthHeader);
        if (Number.isFinite(actualLength) && actualLength !== expectedSize) {
          return {
            ok: false,
            status: response.status,
            finalUrl: currentUrl,
            reason: `Content-Length ${actualLength} does not match expected size ${expectedSize}.`,
          };
        }
      }
    }

    return { ok: true, status: response.status, finalUrl: currentUrl };
  }

  return { ok: false, reason: "Redirect loop terminated unexpectedly." };
}

function buildCnbApiHeaders(token, extra = {}) {
  return {
    "Accept": "application/vnd.cnb.api+json",
    "Authorization": `Bearer ${token}`,
    ...extra,
  };
}

async function fetchCnbReleaseByTag({ apiBaseUrl, cnbRepo, tagName, cnbToken, fetchImpl }) {
  const url = `${apiBaseUrl}/${cnbRepo}/-/releases/tags/${encodeURIComponent(tagName)}`;
  const response = await fetchImpl(url, {
    method: "GET",
    headers: buildCnbApiHeaders(cnbToken),
  });

  if (response.status === 404) {
    await cancelBodySafely(response);
    return null;
  }

  if (!response.ok) {
    const text = await response.text();
    throw new Error(`CNB release lookup failed (${response.status}): ${text}`);
  }

  return response.json();
}

async function createCnbRelease({ apiBaseUrl, cnbRepo, release, cnbToken, fetchImpl }) {
  const url = `${apiBaseUrl}/${cnbRepo}/-/releases`;
  const payload = {
    tag_name: release.tag_name,
    name: release.name || release.tag_name,
    body: release.body ?? "",
    draft: false,
    prerelease: Boolean(release.prerelease),
    make_latest: release.prerelease ? "false" : "true",
    ...(release.target_commitish ? { target_commitish: release.target_commitish } : {}),
  };

  const response = await fetchImpl(url, {
    method: "POST",
    headers: buildCnbApiHeaders(cnbToken, {
      "Content-Type": "application/json",
    }),
    body: JSON.stringify(payload),
  });

  if (!response.ok) {
    const text = await response.text();
    throw new Error(`CNB release create failed (${response.status}): ${text}`);
  }

  return response.json();
}

function isCnbAssetMatching(cnbAsset, ghAsset) {
  if (!cnbAsset || cnbAsset.name !== ghAsset.name) {
    return false;
  }

  if (
    typeof ghAsset.size === "number"
    && typeof cnbAsset.size === "number"
    && cnbAsset.size !== ghAsset.size
  ) {
    return false;
  }

  const expectedSha256 = parseSha256Digest(ghAsset.digest);
  if (expectedSha256) {
    const actualAlgo = String(cnbAsset.hash_algo ?? "").toLowerCase();
    const actualHash = String(cnbAsset.hash_value ?? "").toLowerCase();
    return actualAlgo === "sha256" && actualHash === expectedSha256;
  }

  return typeof ghAsset.size === "number" && typeof cnbAsset.size === "number";
}

async function fetchWithGitHubRedirectIsolation(
  initialUrl,
  { githubToken, fetchImpl, maxRedirects = 5 },
) {
  let currentUrl = assertHttpsUrl(initialUrl, "GitHub asset download URL").toString();

  for (let hop = 0; hop <= maxRedirects; hop++) {
    const parsed = new URL(currentUrl);
    const headers = GITHUB_AUTH_HOSTS.has(parsed.hostname.toLowerCase()) && githubToken
      ? {
          "Accept": "application/octet-stream",
          "Authorization": `Bearer ${githubToken}`,
          "User-Agent": "Everywhere-Release-Sync/1.0",
        }
      : {
          "Accept": "application/octet-stream",
          "User-Agent": "Everywhere-Release-Sync/1.0",
        };

    const response = await fetchImpl(currentUrl, {
      method: "GET",
      headers,
      redirect: "manual",
    });

    if (REDIRECT_STATUS_CODES.has(response.status)) {
      const location = response.headers?.get?.("location");
      await cancelBodySafely(response);

      if (!location) {
        throw new Error(`GitHub asset download redirect (${response.status}) is missing Location header.`);
      }

      if (hop === maxRedirects) {
        throw new Error(`GitHub asset download exceeded ${maxRedirects} redirects.`);
      }

      const resolved = new URL(location, currentUrl).toString();
      currentUrl = assertHttpsUrl(resolved, "GitHub asset redirect target").toString();
      continue;
    }

    if (!response.ok) {
      const text = await response.text();
      throw new Error(`GitHub asset download failed (${response.status}): ${text}`);
    }

    return response;
  }

  throw new Error("GitHub asset download redirect loop terminated unexpectedly.");
}

async function downloadAssetToTempFile({ asset, tempDir, githubToken, fetchImpl }) {
  const downloadUrl = asset.browser_download_url || asset.url;
  if (!downloadUrl) {
    throw new Error(`GitHub asset '${asset.name}' is missing download URL.`);
  }

  const response = await fetchWithGitHubRedirectIsolation(downloadUrl, { githubToken, fetchImpl });
  const filePath = path.join(tempDir, path.basename(asset.name));
  const hash = crypto.createHash("sha256");
  let downloadedBytes = 0;

  const fileHandle = await fs.open(filePath, "w");
  try {
    if (response.body) {
      for await (const chunk of response.body) {
        const buffer = Buffer.isBuffer(chunk) ? chunk : Buffer.from(chunk);
        hash.update(buffer);
        downloadedBytes += buffer.byteLength;
        await fileHandle.write(buffer);
      }
    } else {
      const buffer = Buffer.from(await response.arrayBuffer());
      hash.update(buffer);
      downloadedBytes = buffer.byteLength;
      await fileHandle.write(buffer);
    }
  } finally {
    await fileHandle.close();
  }

  if (typeof asset.size === "number" && asset.size > 0 && downloadedBytes !== asset.size) {
    throw new Error(
      `Downloaded size mismatch for '${asset.name}': expected ${asset.size} bytes, got ${downloadedBytes} bytes.`,
    );
  }

  const expectedSha256 = parseSha256Digest(asset.digest);
  const actualSha256 = hash.digest("hex");
  if (expectedSha256 && actualSha256 !== expectedSha256) {
    throw new Error(
      `Downloaded SHA-256 mismatch for '${asset.name}': expected ${expectedSha256}, got ${actualSha256}.`,
    );
  }

  return { filePath, size: downloadedBytes, sha256: actualSha256 };
}

async function uploadFileToCnbRelease({
  apiBaseUrl,
  cnbRepo,
  cnbReleaseId,
  asset,
  filePath,
  size,
  cnbToken,
  fetchImpl,
}) {
  const uploadUrlEndpoint = `${apiBaseUrl}/${cnbRepo}/-/releases/${encodeURIComponent(cnbReleaseId)}/asset-upload-url`;
  const initResponse = await fetchImpl(uploadUrlEndpoint, {
    method: "POST",
    headers: buildCnbApiHeaders(cnbToken, {
      "Content-Type": "application/json",
    }),
    body: JSON.stringify({
      asset_name: asset.name,
      size,
      overwrite: true,
      ttl: 0,
    }),
  });

  if (!initResponse.ok) {
    const text = await initResponse.text();
    throw new Error(`CNB asset-upload-url failed (${initResponse.status}): ${text}`);
  }

  const { upload_url: uploadUrl, verify_url: verifyUrl } = await initResponse.json();
  if (!uploadUrl || !verifyUrl) {
    throw new Error("CNB asset-upload-url response is missing upload_url or verify_url.");
  }

  // Upload bytes directly to the signed COS URL without forwarding the CNB bearer token.
  const fileBlob = await openAsBlob(filePath, {
    type: asset.content_type || "application/octet-stream",
  });
  const putResponse = await fetchImpl(uploadUrl, {
    method: "PUT",
    headers: {
      "Content-Type": asset.content_type || "application/octet-stream",
      "Content-Length": String(size),
    },
    body: fileBlob,
  });

  if (!putResponse.ok) {
    const text = await putResponse.text();
    throw new Error(`CNB COS PUT upload failed (${putResponse.status}): ${text}`);
  }
  await cancelBodySafely(putResponse);

  const resolvedVerifyUrl = new URL(verifyUrl, apiBaseUrl);
  resolvedVerifyUrl.searchParams.set("ttl", "0");

  const verifyResponse = await fetchImpl(resolvedVerifyUrl.toString(), {
    method: "POST",
    headers: buildCnbApiHeaders(cnbToken),
  });

  if (!verifyResponse.ok) {
    const text = await verifyResponse.text();
    throw new Error(`CNB asset verify failed (${verifyResponse.status}): ${text}`);
  }
  await cancelBodySafely(verifyResponse);
}

async function verifyCnbAssetWithRetry(url, { expectedSize, fetchImpl, retryDelayMs }) {
  const firstAttempt = await verifyAnonymousCnbAssetUrl(url, { expectedSize, fetchImpl });
  if (firstAttempt.ok) {
    return firstAttempt;
  }

  if (retryDelayMs > 0) {
    await new Promise((resolve) => setTimeout(resolve, retryDelayMs));
  }

  return verifyAnonymousCnbAssetUrl(url, { expectedSize, fetchImpl });
}

/**
 * Mirrors GitHub release assets to CNB Release and verifies anonymous HTTPS accessibility.
 * All CNB network/API errors are fault-isolated so download-center publishing never aborts.
 *
 * @param {{
 *   release: { tag_name: string, name?: string, body?: string, prerelease?: boolean, target_commitish?: string },
 *   assets: Array<{ name: string, size?: number, digest?: string, content_type?: string, browser_download_url?: string, url?: string }>,
 *   cnbToken?: string,
 *   githubToken?: string,
 *   cnbRepo?: string,
 *   apiBaseUrl?: string,
 *   webBaseUrl?: string,
 *   fetchImpl?: typeof fetch,
 *   retryDelayMs?: number,
 *   logger?: Pick<Console, "log" | "warn">,
 * }} params
 * @returns {Promise<{
 *   skipped: boolean,
 *   reason?: string,
 *   error?: string,
 *   releaseId?: string,
 *   verifiedMirrors: Map<string, string>,
 *   assets: Array<{ name: string, status: "uploaded" | "skipped-existing" | "failed", mirrorUrl?: string, error?: string }>,
 * }>}
 */
export async function syncReleaseAssetsToCnb({
  release,
  assets,
  cnbToken = process.env.CNB_TOKEN,
  githubToken = process.env.GITHUB_TOKEN,
  cnbRepo = process.env.CNB_REPO || DEFAULT_CNB_REPO,
  apiBaseUrl = process.env.CNB_API_BASE_URL || DEFAULT_CNB_API_BASE_URL,
  webBaseUrl = process.env.CNB_WEB_BASE_URL || DEFAULT_CNB_WEB_BASE_URL,
  fetchImpl = fetch,
  retryDelayMs = 500,
  logger = console,
}) {
  const normalizedApiBase = apiBaseUrl.replace(/\/+$/, "");
  const normalizedWebBase = webBaseUrl.replace(/\/+$/, "");
  const normalizedRepo = cnbRepo.replace(/^\/+|\/+$/g, "");
  const verifiedMirrors = new Map();
  const assetResults = [];

  if (!cnbToken || !String(cnbToken).trim()) {
    logger.warn("CNB_TOKEN is not configured; skipping CNB mirror sync and publishing GitHub-only metadata.");
    return {
      skipped: true,
      reason: "missing-token",
      verifiedMirrors,
      assets: assetResults,
    };
  }

  let cnbRelease;
  try {
    cnbRelease = await fetchCnbReleaseByTag({
      apiBaseUrl: normalizedApiBase,
      cnbRepo: normalizedRepo,
      tagName: release.tag_name,
      cnbToken,
      fetchImpl,
    });

    if (!cnbRelease) {
      cnbRelease = await createCnbRelease({
        apiBaseUrl: normalizedApiBase,
        cnbRepo: normalizedRepo,
        release,
        cnbToken,
        fetchImpl,
      });
    }
  } catch (error) {
    logger.warn(`CNB release preparation failed for '${release.tag_name}'; falling back to proxy mode: ${error.message}`);
    return {
      skipped: false,
      error: error.message,
      verifiedMirrors,
      assets: assetResults,
    };
  }

  const existingCnbAssets = new Map(
    Array.isArray(cnbRelease.assets)
      ? cnbRelease.assets.map((item) => [item.name, item])
      : [],
  );

  const tempDir = await fs.mkdtemp(path.join(os.tmpdir(), "cnb-release-sync-"));
  try {
    for (const asset of assets) {
      const expectedMirrorUrl = buildCnbAssetDownloadUrl(release.tag_name, asset.name, {
        webBaseUrl: normalizedWebBase,
        cnbRepo: normalizedRepo,
      });

      try {
        const existingAsset = existingCnbAssets.get(asset.name);
        if (existingAsset && isCnbAssetMatching(existingAsset, asset)) {
          const existingVerification = await verifyCnbAssetWithRetry(expectedMirrorUrl, {
            expectedSize: asset.size,
            fetchImpl,
            retryDelayMs,
          });
          if (existingVerification.ok) {
            verifiedMirrors.set(asset.name, expectedMirrorUrl);
            assetResults.push({
              name: asset.name,
              status: "skipped-existing",
              mirrorUrl: expectedMirrorUrl,
            });
            logger.log(`CNB mirror asset already up-to-date and verified: ${asset.name}`);
            continue;
          }

          logger.warn(
            `Existing CNB asset '${asset.name}' failed anonymous check (${existingVerification.reason}); re-uploading.`,
          );
        }

        const downloaded = await downloadAssetToTempFile({
          asset,
          tempDir,
          githubToken,
          fetchImpl,
        });

        try {
          await uploadFileToCnbRelease({
            apiBaseUrl: normalizedApiBase,
            cnbRepo: normalizedRepo,
            cnbReleaseId: cnbRelease.id,
            asset,
            filePath: downloaded.filePath,
            size: downloaded.size,
            cnbToken,
            fetchImpl,
          });
        } finally {
          await fs.rm(downloaded.filePath, { force: true });
        }

        const verification = await verifyCnbAssetWithRetry(expectedMirrorUrl, {
          expectedSize: downloaded.size,
          fetchImpl,
          retryDelayMs,
        });
        if (!verification.ok) {
          throw new Error(`Anonymous CNB mirror verification failed: ${verification.reason}`);
        }

        verifiedMirrors.set(asset.name, expectedMirrorUrl);
        assetResults.push({
          name: asset.name,
          status: "uploaded",
          mirrorUrl: expectedMirrorUrl,
        });
        logger.log(`Uploaded and verified CNB mirror asset: ${asset.name}`);
      } catch (error) {
        logger.warn(`CNB mirror sync failed for asset '${asset.name}': ${error.message}`);
        assetResults.push({
          name: asset.name,
          status: "failed",
          error: error.message,
        });
      }
    }
  } finally {
    await fs.rm(tempDir, { recursive: true, force: true });
  }

  return {
    skipped: false,
    releaseId: cnbRelease.id,
    verifiedMirrors,
    assets: assetResults,
  };
}
