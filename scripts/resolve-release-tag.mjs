import fs from "node:fs";
import { parseReleaseTag } from "./release-tag.mjs";

const tagName = process.argv[2] || process.env.GITHUB_REF_NAME || process.env.GITHUB_REF?.replace(/^refs\/tags\//, "");
const release = parseReleaseTag(tagName);

const envLines = [
  `VERSION=${release.version}`,
  `VERSION_PREFIX=${release.versionPrefix}`,
  `SEMANTIC_VERSION=${release.semanticVersion}`,
  `CHANNEL=${release.channel}`,
  `RELEASE_TAG=${release.releaseTag}`,
  `PRERELEASE=${release.prerelease ? "true" : "false"}`,
];

if (process.env.GITHUB_ENV) {
  fs.appendFileSync(process.env.GITHUB_ENV, `${envLines.join("\n")}\n`, "utf8");
}

console.log(envLines.join("\n"));
