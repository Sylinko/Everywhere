const TAG_PATTERN = /^v(\d+)\.(\d+)\.(\d+)(?:-([A-Za-z0-9-]+(?:\.[A-Za-z0-9-]+)*))?$/;

export const ENABLED_CHANNELS = new Set(["stable", "canary"]);

export function parseReleaseTag(tagName) {
  if (!tagName) {
    throw new Error("Release tag is required.");
  }

  const match = TAG_PATTERN.exec(tagName);
  if (!match) {
    throw new Error(`Release tag '${tagName}' must match vX.Y.Z or vX.Y.Z-<channel>[.<qualifier>...].`);
  }

  const versionPrefix = `${match[1]}.${match[2]}.${match[3]}`;
  const suffix = match[4] ?? "";
  const version = suffix ? `${versionPrefix}-${suffix}` : versionPrefix;
  const channel = suffix ? suffix.split(".")[0].toLowerCase() : "stable";

  if (suffix && channel === "stable") {
    throw new Error("Stable releases must use vX.Y.Z tags without a suffix.");
  }

  if (!ENABLED_CHANNELS.has(channel)) {
    throw new Error(`Channel '${channel}' is not enabled. Enabled channels: ${[...ENABLED_CHANNELS].join(", ")}.`);
  }

  return {
    version,
    versionPrefix,
    semanticVersion: version,
    channel,
    releaseTag: tagName,
    prerelease: channel !== "stable",
  };
}
