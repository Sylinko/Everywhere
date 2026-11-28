import crypto from "node:crypto";
import { openAsBlob } from "node:fs";
import fs from "node:fs/promises";
import os from "node:os";
import path from "node:path";

export const DEFAULT_CNB_REPO = "sylinko/Everywhere";
export const DEFAULT_CNB_API_BASE_URL = "https://api.cnb.cool";
export const DEFAULT_CNB_WEB_BASE_URL = "https://cnb.cool";
export const DEFAULT_CNB_SYNC_TIMEOUT_MS = 10 * 60 * 1000;
const DEFAULT_CNB_REQUEST_TIMEOUT_MS = 60 * 1000;

const REDIRECT_STATUS_CODES = new Set([301, 302, 303, 307, 308]);
const GITHUB_AUTH_HOSTS = new Set(["github.com", "api.github.com", "uploads.github.com"]);
const FULL_COMMIT_SHA_PATTERN = /^[0-9a-f]{40}$/i;

class CnbDeadlineExceededError extends Error {
  constructor(message = "CNB mirror sync deadline exceeded.") {
    super(message);
    this.name = "CnbDeadlineExceededError";
  }
}

function isFullCommitSha(value) {
  return typeof value === "string" && FULL_COMMIT_SHA_PATTERN.test(value.trim());
}

function createOperationSignal(deadlineMs, maxOperationMs) {
  if (!Number.isFinite(deadlineMs)) {
    if (!Number.isFinite(maxOperationMs) || maxOperationMs <= 0) {
      return { signal: undefined, cleanup() {} };
    }

    const controller = new AbortController();
    const timer = setTimeout(
      () => controller.abort(new CnbDeadlineExceededError(`CNB request timed out after ${maxOperationMs}ms.`)),
      maxOperationMs,
    );
    return {
      signal: controller.signal,
      cleanup() {
        clearTimeout(timer);
      },
    };
  }

  const remainingMs = deadlineMs - Date.now();
  if (remainingMs <= 0) {
    throw new CnbDeadlineExceededError();
  }

  const effectiveMs = Number.isFinite(maxOperationMs) && maxOperationMs > 0
    ? Math.min(remainingMs, maxOperationMs)
    : remainingMs;

  const controller = new AbortController();
  const reason = new CnbDeadlineExceededError(
    effectiveMs === remainingMs
      ? "CNB mirror sync deadline exceeded."
      : `CNB request timed out after ${effectiveMs}ms.`,
  );
  const timer = setTimeout(() => controller.abort(reason), effectiveMs);

  return {
    signal: controller.signal,
    cleanup() {
      clearTimeout(timer);
    },
  };
}

function rethrowIfDeadlineExceeded(error, signal) {
  if (error instanceof CnbDeadlineExceededError) {
    throw error;
  }

  if (signal?.aborted) {
    if (signal.reason instanceof CnbDeadlineExceededError) {
      throw signal.reason;
    }
    throw new CnbDeadlineExceededError(
      signal.reason instanceof Error ? signal.reason.message : "CNB mirror sync deadline exceeded.",
    );
  }
}

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
  {
    expectedSize,
    fetchImpl = fetch,
    maxRedirects = 5,
    deadlineMs = Infinity,
    requestTimeoutMs = DEFAULT_CNB_REQUEST_TIMEOUT_MS,
  } = {},
) {
  let currentUrl;
  try {
    currentUrl = assertHttpsUrl(url, "CNB asset URL").toString();
  } catch (error) {
    return { ok: false, reason: error.message };
  }

  for (let hop = 0; hop <= maxRedirects; hop++) {
    let op;
    try {
      op = createOperationSignal(deadlineMs, requestTimeoutMs);
    } catch (error) {
      return {
        ok: false,
        deadlineExceeded: error instanceof CnbDeadlineExceededError,
        reason: error.message,
      };
    }

    let response;
    try {
      response = await fetchImpl(currentUrl, {
        method: "GET",
        headers: {
          "Range": "bytes=0-1023",
        },
        redirect: "manual",
        ...(op.signal ? { signal: op.signal } : {}),
      });
    } catch (error) {
      op.cleanup();
      return {
        ok: false,
        deadlineExceeded: error instanceof CnbDeadlineExceededError || op.signal?.aborted === true,
        reason: `Request failed: ${error.message}`,
      };
    }

    if (REDIRECT_STATUS_CODES.has(response.status)) {
      const location = response.headers?.get?.("location");
      await cancelBodySafely(response);
      op.cleanup();

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

    if (response.status !== 206 && response.status !== 200) {
      await cancelBodySafely(response);
      op.cleanup();
      return {
        ok: false,
        status: response.status,
        finalUrl: currentUrl,
        reason: `Unexpected HTTP status ${response.status}.`,
      };
    }

    if (contentType.includes("text/html") || contentType.includes("application/xhtml+xml")) {
      await cancelBodySafely(response);
      op.cleanup();
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
          await cancelBodySafely(response);
          op.cleanup();
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
          await cancelBodySafely(response);
          op.cleanup();
          return {
            ok: false,
            status: response.status,
            finalUrl: currentUrl,
            reason: `Content-Length ${actualLength} does not match expected size ${expectedSize}.`,
          };
        }
      }
    }

    // Read a bounded non-empty sample from the stream before canceling so empty or broken
    // responses are caught without buffering the entire asset.
    try {
      let sampleBytes = 0;
      if (response.body?.getReader) {
        const reader = response.body.getReader();
        try {
          while (sampleBytes === 0) {
            const { done, value } = await reader.read();
            if (done) {
              break;
            }
            sampleBytes += value?.byteLength ?? 0;
          }
        } finally {
          try {
            await reader.cancel();
          } catch {
            // Best-effort stream cleanup.
          }
        }
      } else if (typeof response.arrayBuffer === "function") {
        const buffer = await response.arrayBuffer();
        sampleBytes = buffer.byteLength;
      }

      if (sampleBytes === 0) {
        return {
          ok: false,
          status: response.status,
          finalUrl: currentUrl,
          reason: "CNB asset response body was empty.",
        };
      }
    } catch (error) {
      return {
        ok: false,
        status: response.status,
        finalUrl: currentUrl,
        deadlineExceeded: error instanceof CnbDeadlineExceededError || op.signal?.aborted === true,
        reason: `Failed to read asset body sample: ${error.message}`,
      };
    } finally {
      op.cleanup();
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

async function fetchCnbTagCommit({ apiBaseUrl, cnbRepo, tagName, cnbToken, fetchImpl, deadlineMs, requestTimeoutMs }) {
  const url = `${apiBaseUrl}/${cnbRepo}/-/git/tags/${encodeURIComponent(tagName)}`;
  const op = createOperationSignal(deadlineMs, requestTimeoutMs);
  try {
    const response = await fetchImpl(url, {
      method: "GET",
      headers: buildCnbApiHeaders(cnbToken),
      ...(op.signal ? { signal: op.signal } : {}),
    });

    if (response.status === 404) {
      await cancelBodySafely(response);
      return null;
    }

    if (!response.ok) {
      const text = await response.text();
      throw new Error(`CNB tag lookup failed (${response.status}): ${text}`);
    }

    const payload = await response.json();
    const candidates = [
      payload?.commit?.sha,
      payload?.target?.commit?.sha,
      payload?.target?.sha,
      payload?.target,
      payload?.sha,
    ];
    for (const candidate of candidates) {
      if (isFullCommitSha(candidate)) {
        return candidate.trim().toLowerCase();
      }
    }

    throw new Error(`CNB tag '${tagName}' response did not include a commit SHA.`);
  } catch (error) {
    rethrowIfDeadlineExceeded(error, op.signal);
    throw error;
  } finally {
    op.cleanup();
  }
}

async function fetchCnbReleaseByTag({ apiBaseUrl, cnbRepo, tagName, cnbToken, fetchImpl, deadlineMs, requestTimeoutMs }) {
  const url = `${apiBaseUrl}/${cnbRepo}/-/releases/tags/${encodeURIComponent(tagName)}`;
  const op = createOperationSignal(deadlineMs, requestTimeoutMs);
  try {
    const response = await fetchImpl(url, {
      method: "GET",
      headers: buildCnbApiHeaders(cnbToken),
      ...(op.signal ? { signal: op.signal } : {}),
    });

    if (response.status === 404) {
      await cancelBodySafely(response);
      return null;
    }

    if (!response.ok) {
      const text = await response.text();
      throw new Error(`CNB release lookup failed (${response.status}): ${text}`);
    }

    return await response.json();
  } catch (error) {
    rethrowIfDeadlineExceeded(error, op.signal);
    throw error;
  } finally {
    op.cleanup();
  }
}

async function createCnbRelease({
  apiBaseUrl,
  cnbRepo,
  release,
  makeLatest = !release.prerelease,
  cnbToken,
  fetchImpl,
  deadlineMs,
  requestTimeoutMs,
}) {
  const url = `${apiBaseUrl}/${cnbRepo}/-/releases`;
  const payload = {
    tag_name: release.tag_name,
    name: release.name || release.tag_name,
    body: release.body ?? "",
    draft: false,
    prerelease: Boolean(release.prerelease),
    make_latest: makeLatest ? "true" : "false",
  };

  const op = createOperationSignal(deadlineMs, requestTimeoutMs);
  try {
    const response = await fetchImpl(url, {
      method: "POST",
      headers: buildCnbApiHeaders(cnbToken, {
        "Content-Type": "application/json",
      }),
      body: JSON.stringify(payload),
      ...(op.signal ? { signal: op.signal } : {}),
    });

    if (!response.ok) {
      const text = await response.text();
      throw new Error(`CNB release create failed (${response.status}): ${text}`);
    }

    return await response.json();
  } catch (error) {
    rethrowIfDeadlineExceeded(error, op.signal);
    throw error;
  } finally {
    op.cleanup();
  }
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

async function writeAll(fileHandle, buffer) {
  let offset = 0;
  while (offset < buffer.byteLength) {
    const { bytesWritten } = await fileHandle.write(buffer, offset, buffer.byteLength - offset);
    if (!bytesWritten || bytesWritten <= 0) {
      throw new Error(`Short write to staging file: wrote ${offset} of ${buffer.byteLength} bytes.`);
    }
    offset += bytesWritten;
  }
}

async function fetchWithGitHubRedirectIsolation(
  initialUrl,
  { githubToken, fetchImpl, maxRedirects = 5, signal },
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
      ...(signal ? { signal } : {}),
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

async function downloadAssetToTempFile({ asset, tempDir, githubToken, fetchImpl, deadlineMs }) {
  const downloadUrl = asset.browser_download_url || asset.url;
  if (!downloadUrl) {
    throw new Error(`GitHub asset '${asset.name}' is missing download URL.`);
  }

  const filePath = path.join(tempDir, path.basename(asset.name));
  const hash = crypto.createHash("sha256");
  let writtenBytes = 0;
  const op = createOperationSignal(deadlineMs);

  try {
    const response = await fetchWithGitHubRedirectIsolation(downloadUrl, {
      githubToken,
      fetchImpl,
      signal: op.signal,
    });

    const fileHandle = await fs.open(filePath, "w");
    try {
      if (response.body) {
        for await (const chunk of response.body) {
          const buffer = Buffer.isBuffer(chunk) ? chunk : Buffer.from(chunk);
          await writeAll(fileHandle, buffer);
          hash.update(buffer);
          writtenBytes += buffer.byteLength;
        }
      } else {
        const buffer = Buffer.from(await response.arrayBuffer());
        await writeAll(fileHandle, buffer);
        hash.update(buffer);
        writtenBytes = buffer.byteLength;
      }
    } finally {
      await fileHandle.close();
    }
  } catch (error) {
    rethrowIfDeadlineExceeded(error, op.signal);
    throw error;
  } finally {
    op.cleanup();
  }

  if (typeof asset.size === "number" && asset.size > 0 && writtenBytes !== asset.size) {
    throw new Error(
      `Downloaded size mismatch for '${asset.name}': expected ${asset.size} bytes, got ${writtenBytes} bytes.`,
    );
  }

  const expectedSha256 = parseSha256Digest(asset.digest);
  const actualSha256 = hash.digest("hex");
  if (expectedSha256 && actualSha256 !== expectedSha256) {
    throw new Error(
      `Downloaded SHA-256 mismatch for '${asset.name}': expected ${expectedSha256}, got ${actualSha256}.`,
    );
  }

  return { filePath, size: writtenBytes, sha256: actualSha256 };
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
  deadlineMs,
  requestTimeoutMs,
}) {
  const uploadUrlEndpoint = `${apiBaseUrl}/${cnbRepo}/-/releases/${encodeURIComponent(cnbReleaseId)}/asset-upload-url`;
  let uploadUrl;
  let verifyUrl;

  const initOp = createOperationSignal(deadlineMs, requestTimeoutMs);
  try {
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
      ...(initOp.signal ? { signal: initOp.signal } : {}),
    });

    if (!initResponse.ok) {
      const text = await initResponse.text();
      throw new Error(`CNB asset-upload-url failed (${initResponse.status}): ${text}`);
    }

    ({ upload_url: uploadUrl, verify_url: verifyUrl } = await initResponse.json());
    if (!uploadUrl || !verifyUrl) {
      throw new Error("CNB asset-upload-url response is missing upload_url or verify_url.");
    }
  } catch (error) {
    rethrowIfDeadlineExceeded(error, initOp.signal);
    throw error;
  } finally {
    initOp.cleanup();
  }

  // Upload bytes directly to the signed COS URL without forwarding the CNB bearer token.
  const fileBlob = await openAsBlob(filePath, {
    type: asset.content_type || "application/octet-stream",
  });
  const putOp = createOperationSignal(deadlineMs);
  try {
    const putResponse = await fetchImpl(uploadUrl, {
      method: "PUT",
      headers: {
        "Content-Type": asset.content_type || "application/octet-stream",
        "Content-Length": String(size),
      },
      body: fileBlob,
      ...(putOp.signal ? { signal: putOp.signal } : {}),
    });

    if (!putResponse.ok) {
      const text = await putResponse.text();
      throw new Error(`CNB COS PUT upload failed (${putResponse.status}): ${text}`);
    }
    await cancelBodySafely(putResponse);
  } catch (error) {
    rethrowIfDeadlineExceeded(error, putOp.signal);
    throw error;
  } finally {
    putOp.cleanup();
  }

  const resolvedVerifyUrl = new URL(verifyUrl, apiBaseUrl);
  resolvedVerifyUrl.searchParams.set("ttl", "0");

  const verifyOp = createOperationSignal(deadlineMs, requestTimeoutMs);
  try {
    const verifyResponse = await fetchImpl(resolvedVerifyUrl.toString(), {
      method: "POST",
      headers: buildCnbApiHeaders(cnbToken),
      ...(verifyOp.signal ? { signal: verifyOp.signal } : {}),
    });

    if (!verifyResponse.ok) {
      const text = await verifyResponse.text();
      throw new Error(`CNB asset verify failed (${verifyResponse.status}): ${text}`);
    }
    await cancelBodySafely(verifyResponse);
  } catch (error) {
    rethrowIfDeadlineExceeded(error, verifyOp.signal);
    throw error;
  } finally {
    verifyOp.cleanup();
  }
}

async function verifyCnbAssetWithRetry(url, { expectedSize, fetchImpl, retryDelayMs, deadlineMs, requestTimeoutMs }) {
  const firstAttempt = await verifyAnonymousCnbAssetUrl(url, {
    expectedSize,
    fetchImpl,
    deadlineMs,
    requestTimeoutMs,
  });
  if (firstAttempt.ok || firstAttempt.deadlineExceeded) {
    return firstAttempt;
  }

  const remainingMs = Number.isFinite(deadlineMs) ? deadlineMs - Date.now() : Infinity;
  if (remainingMs <= 0) {
    return {
      ok: false,
      deadlineExceeded: true,
      reason: "CNB mirror sync deadline exceeded.",
    };
  }

  if (retryDelayMs > 0) {
    await new Promise((resolve) => setTimeout(resolve, Math.min(retryDelayMs, remainingMs)));
  }

  return verifyAnonymousCnbAssetUrl(url, {
    expectedSize,
    fetchImpl,
    deadlineMs,
    requestTimeoutMs,
  });
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
 *   expectedTagCommit?: string,
 *   cnbTagCommit?: string,
 *   makeLatest?: boolean,
 *   timeoutMs?: number,
 *   requestTimeoutMs?: number,
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
  expectedTagCommit,
  cnbTagCommit,
  makeLatest = !release?.prerelease,
  timeoutMs = Number(process.env.CNB_SYNC_TIMEOUT_MS) > 0
    ? Number(process.env.CNB_SYNC_TIMEOUT_MS)
    : DEFAULT_CNB_SYNC_TIMEOUT_MS,
  requestTimeoutMs = DEFAULT_CNB_REQUEST_TIMEOUT_MS,
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
  const deadlineMs = Number.isFinite(timeoutMs) && timeoutMs > 0 ? Date.now() + timeoutMs : Infinity;

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
    if (!isFullCommitSha(expectedTagCommit)) {
      throw new Error(
        `Missing or invalid GitHub tag commit SHA for '${release.tag_name}'; refusing unverified CNB release sync.`,
      );
    }

    const expectedSha = expectedTagCommit.trim().toLowerCase();
    let resolvedCnbTagCommit = null;
    if (isFullCommitSha(cnbTagCommit)) {
      resolvedCnbTagCommit = cnbTagCommit.trim().toLowerCase();
    } else {
      resolvedCnbTagCommit = await fetchCnbTagCommit({
        apiBaseUrl: normalizedApiBase,
        cnbRepo: normalizedRepo,
        tagName: release.tag_name,
        cnbToken,
        fetchImpl,
        deadlineMs,
        requestTimeoutMs,
      });
    }

    if (!resolvedCnbTagCommit) {
      throw new Error(`CNB tag '${release.tag_name}' does not exist; refusing to create a release from a branch.`);
    }

    if (resolvedCnbTagCommit !== expectedSha) {
      throw new Error(
        `CNB tag '${release.tag_name}' points to ${resolvedCnbTagCommit}, expected GitHub commit ${expectedSha}.`,
      );
    }

    cnbRelease = await fetchCnbReleaseByTag({
      apiBaseUrl: normalizedApiBase,
      cnbRepo: normalizedRepo,
      tagName: release.tag_name,
      cnbToken,
      fetchImpl,
      deadlineMs,
      requestTimeoutMs,
    });

    if (!cnbRelease) {
      cnbRelease = await createCnbRelease({
        apiBaseUrl: normalizedApiBase,
        cnbRepo: normalizedRepo,
        release,
        makeLatest,
        cnbToken,
        fetchImpl,
        deadlineMs,
        requestTimeoutMs,
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

  let tempDir;
  try {
    tempDir = await fs.mkdtemp(path.join(os.tmpdir(), "cnb-release-sync-"));
  } catch (error) {
    logger.warn(`CNB staging directory creation failed for '${release.tag_name}'; falling back to proxy mode: ${error.message}`);
    return {
      skipped: false,
      error: error.message,
      releaseId: cnbRelease.id,
      verifiedMirrors,
      assets: assetResults,
    };
  }

  let syncError;
  try {
    for (const asset of assets) {
      if (Number.isFinite(deadlineMs) && Date.now() >= deadlineMs) {
        throw new CnbDeadlineExceededError();
      }

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
            deadlineMs,
            requestTimeoutMs,
          });
          if (existingVerification.deadlineExceeded) {
            throw new CnbDeadlineExceededError(existingVerification.reason);
          }

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
          deadlineMs,
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
            deadlineMs,
            requestTimeoutMs,
          });
        } finally {
          await fs.rm(downloaded.filePath, { force: true });
        }

        const verification = await verifyCnbAssetWithRetry(expectedMirrorUrl, {
          expectedSize: downloaded.size,
          fetchImpl,
          retryDelayMs,
          deadlineMs,
          requestTimeoutMs,
        });
        if (verification.deadlineExceeded) {
          throw new CnbDeadlineExceededError(verification.reason);
        }
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
        if (error instanceof CnbDeadlineExceededError) {
          syncError = error.message;
          logger.warn(
            `CNB mirror sync stopped early for '${release.tag_name}' (${error.message}); returning ${verifiedMirrors.size} verified mirror(s).`,
          );
          break;
        }

        logger.warn(`CNB mirror sync failed for asset '${asset.name}': ${error.message}`);
        assetResults.push({
          name: asset.name,
          status: "failed",
          error: error.message,
        });
      }
    }
  } catch (error) {
    syncError = error.message;
    logger.warn(
      `CNB mirror sync aborted for '${release.tag_name}' (${error.message}); returning ${verifiedMirrors.size} verified mirror(s).`,
    );
  } finally {
    try {
      await fs.rm(tempDir, { recursive: true, force: true });
    } catch (cleanupError) {
      logger.warn(`Failed to remove CNB staging directory '${tempDir}': ${cleanupError.message}`);
    }
  }

  return {
    skipped: false,
    ...(syncError ? { error: syncError } : {}),
    releaseId: cnbRelease.id,
    verifiedMirrors,
    assets: assetResults,
  };
}
