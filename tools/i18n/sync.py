import argparse
import json
import os
import re
import sys
import xml.etree.ElementTree as ET
from collections import Counter
from pathlib import Path
from xml.dom import minidom

import defusedxml.ElementTree as SafeET
from defusedxml.common import DefusedXmlException
from openai import APIError, OpenAI

from catalog import CORE_I18N, SOURCE_NAME, expected_locales, locale_files, project_directories


COPY_PREFIXES = ("LocaleName_", "SettingsSelectionItem_Common_Language_")
# .NET format items, escaped braces, and the named tokens shown in prompt-related UI.
PLACEHOLDER = re.compile(r"\{\{|\}\}|\{(?:[0-9]+(?:,\s*-?[0-9]+)?(?::[^{}]+)?|[A-Za-z_][A-Za-z_0-9]*)\}")
SOFTWARE_INTRODUCTION = """
Everywhere is an interactive AI assistant with context-aware capabilities. It's a Windows desktop application that:
- Provides instant AI assistance anywhere on the screen
- Features a modern, sleek UI built with Avalonia
- Offers intelligent context understanding and perception
- Supports multiple languages and themes
- Includes features like web search, file system access, and system integration
- Allows users to interact with UI elements through AI

Key features:
- Quick invocation via keyboard shortcuts
- Visual element detection and interaction
- Multi-language support with i18n
- Customizable AI models and parameters
- Plugin system for extended functionality
"""


def translation_prompt(target_language: str) -> str:
    return f"""
You are a professional translator for software localization. Your task is to translate UI strings for the "Everywhere" application.

Software Context:
{SOFTWARE_INTRODUCTION}

Translation Guidelines:
1. Maintain the original meaning and tone.
2. Keep placeholders intact (e.g., {{0}}, {{1}}).
3. Preserve formatting characters (e.g., \\n, \\r).
4. Use natural, native expressions for the target language: {target_language}.
5. Keep technical terms consistent.
6. For language names (like "中文 (简体)"), DO NOT TRANSLATE - keep them as is.
7. For proper nouns, brand names, and trademarks (like "Everywhere"), DO NOT TRANSLATE - keep them as is.
8. Ensure UI text is concise and clear for a software interface.
9. Return ONLY a valid JSON object with the same keys and translated values. Do NOT add any explanations, comments, or markdown formatting.
10. For headers, titles, short labels and tooltips, prefer shorter translations and title case where applicable.
11. For keys started with "Greetings_", use a friendly and casual tone in the translation and let it be fluent and elegant for native speakers.
"""


def read_resx(path: Path) -> ET.Element:
    try:
        root = SafeET.parse(path).getroot()
    except (ET.ParseError, DefusedXmlException) as error:
        raise ValueError(f"{path}: {error}") from error
    if root.tag != "root":
        raise ValueError(f"{path}: expected a RESX <root> element")
    return root


def read_values(root: ET.Element, path: Path) -> dict[str, str]:
    values = {}
    for entry in root.findall("data"):
        key = entry.get("name")
        value = entry.find("value")
        if not key or value is None or key in values:
            raise ValueError(f"{path}: missing name/value or duplicate RESX key {key!r}")
        values[key] = value.text or ""
    return values


def language_name(directory: Path, filename: str) -> str:
    path = directory / filename
    if not path.exists():
        path = CORE_I18N / filename
    with path.open(encoding="utf-8-sig") as stream:
        match = re.search(r"<!--\s*(.+?)\s*-->", stream.read(256))
    if match:
        return match[1]
    return "English" if filename == "Strings.resx" else filename.removeprefix("Strings.").removesuffix(".resx")


def write_resx(path: Path, source: ET.Element, values: dict[str, str], language: str) -> None:
    root = ET.Element("root")
    for header in source.findall("resheader"):
        node = ET.SubElement(root, "resheader", header.attrib)
        ET.SubElement(node, "value").text = header.findtext("value")
    for key, value in values.items():
        node = ET.SubElement(root, "data", {"name": key, "xml:space": "preserve"})
        ET.SubElement(node, "value").text = value

    # Match the existing RESX writer's escaping and indentation.
    document = minidom.parseString(ET.tostring(root, encoding="utf-8"))
    document.insertBefore(document.createComment(f" {language} "), document.documentElement)
    path.write_bytes(document.toprettyxml(indent="    ", encoding="utf-8"))


def batches(keys: list[str], size: int) -> list[list[str]]:
    result = [keys[start:start + size] for start in range(0, len(keys), size)]
    if len(result) > 1 and 0 < len(keys) % size < 5:
        tail = result.pop()
        result[-1].extend(tail)
    return result


def translate(client: OpenAI, api: str, model: str, language: str, batch: dict[str, str]) -> dict[str, str]:
    instructions = translation_prompt(language)
    payload = json.dumps(batch, ensure_ascii=False, indent=2)
    if api == "responses":
        schema = {
            "type": "object",
            "properties": {key: {"type": "string"} for key in batch},
            "required": list(batch),
            "additionalProperties": False,
        }
        response = client.responses.create(
            model=model,
            instructions=instructions,
            input=payload,
            text={"format": {"type": "json_schema", "name": "translations", "strict": True, "schema": schema}},
        )
        if response.status != "completed":
            raise ValueError(f"Responses returned {response.status}: {response.incomplete_details or response.error}")
        if any(part.type == "refusal" for item in response.output if item.type == "message" for part in item.content):
            raise ValueError("The model refused the translation")
        content = response.output_text
    else:
        # Existing custom endpoints may support Chat Completions and JSON mode only.
        completion = client.chat.completions.create(
            model=model,
            messages=[{"role": "system", "content": instructions}, {"role": "user", "content": payload}],
            response_format={"type": "json_object"},
        )
        choice = completion.choices[0]
        if choice.finish_reason != "stop" or not choice.message.content:
            raise ValueError(f"Incomplete Chat Completions translation: {choice.finish_reason}")
        content = choice.message.content.strip()
        fence = re.fullmatch(r"```json\s*(.*?)\s*```", content, re.DOTALL)
        if fence:
            content = fence[1]

    translations = json.loads(content)
    if not isinstance(translations, dict):
        raise ValueError("Expected a JSON object of translations")
    if translations.keys() != batch.keys():
        missing = sorted(batch.keys() - translations.keys())
        extra = sorted(translations.keys() - batch.keys())
        raise ValueError(f"Translation keys do not match the batch: missing={missing}, extra={extra}")
    for key, original in batch.items():
        value = translations[key]
        if not isinstance(value, str) or not value.strip():
            raise ValueError(f"{key}: expected a non-empty translation")
        if Counter(PLACEHOLDER.findall(original)) != Counter(PLACEHOLDER.findall(value)):
            raise ValueError(f"{key}: translation changed the placeholders")
    return translations


def sync_directory(directory: Path, locales: set[str], client: OpenAI, args: argparse.Namespace) -> None:
    source_path = directory / SOURCE_NAME
    source = read_resx(source_path)
    originals = read_values(source, source_path)
    # Continue to sync any additional locale already present in a custom --i18n-path.
    for filename in sorted((locales | locale_files(directory)) - {SOURCE_NAME}):
        path = directory / filename
        exists = path.is_file()
        existing = read_values(read_resx(path), path) if exists else {}
        values = {}
        pending = []
        for key, original in originals.items():
            if existing.get(key) or not original or key.startswith(COPY_PREFIXES):
                values[key] = existing.get(key) or original
            else:
                pending.append(key)

        if exists and not pending and list(existing.items()) == list(values.items()):
            continue
        language = language_name(directory, filename)
        for index, keys in enumerate(batches(pending, args.batch_size), 1):
            print(f"{path}: translating batch {index} ({len(keys)} keys)", flush=True)
            batch = {key: originals[key] for key in keys}
            try:
                values.update(translate(client, args.api, args.model_id, language, batch))
            except (APIError, ValueError) as error:
                raise ValueError(f"{path}, batch {index}: {error}") from error

        write_resx(path, source, {key: values[key] for key in originals}, language)
        print(f"Updated {path}", flush=True)


def main() -> int:
    parser = argparse.ArgumentParser(description="Synchronize project-local RESX translations.")
    parser.add_argument("--base-url", default=os.environ.get("OPENAI_API_BASE") or os.environ.get("BASE_URL"))
    parser.add_argument("--api-key", default=os.environ.get("OPENAI_API_KEY"))
    parser.add_argument("--model-id", default=os.environ.get("MODEL_ID"))
    parser.add_argument("--api", choices=["chat", "responses"], default="chat",
                        help="Use responses for OpenAI, or chat for existing OpenAI-compatible endpoints.")
    parser.add_argument("--batch-size", type=int, default=50, help="Keys per batch; a tail of 1-4 joins the previous batch.")
    parser.add_argument("--max-retries", type=int, default=3, help="SDK retries for transient request failures.")
    parser.add_argument("--i18n-path", type=Path, action="append", help="Sync only this directory; repeatable.")
    args = parser.parse_args()
    if not all([args.base_url, args.api_key, args.model_id]):
        parser.error("Provide --base-url, --api-key and --model-id or their environment variables.")
    if args.batch_size < 1:
        parser.error("--batch-size must be positive")

    try:
        locales = expected_locales()
        with OpenAI(api_key=args.api_key, base_url=args.base_url, max_retries=args.max_retries, timeout=120) as client:
            for directory in args.i18n_path or project_directories():
                sync_directory(directory, locales, client, args)
    except (ValueError, OSError) as error:
        print(error, file=sys.stderr)
        return 1
    return 0


if __name__ == "__main__":
    sys.exit(main())
