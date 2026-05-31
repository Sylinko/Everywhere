# tools/sync_i18n/sync_i18n.py
import argparse
import json
import os
import re
# Use the standard library for XML generation and defusedxml for parsing
import xml.etree.ElementTree as ET
import defusedxml.ElementTree as DefusedET
from typing import Dict, List, Optional, Tuple

# You need to install this dependency: pip install openai
from openai import APIError, OpenAI

# Register the 'xml' namespace to preserve it during serialization
ET.register_namespace("", "http://www.w3.org/2001/XMLSchema-instance")
ET.register_namespace("xsd", "http://www.w3.org/2001/XMLSchema")

# --- Configuration ---

# Software introduction for AI context
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

# Resources that should not be translated (e.g., language names)
# Uses regex patterns for flexible matching.
NO_TRANSLATE_PATTERNS = [
    r'SettingsSelectionItem_Common_Language_.*'
]

# Helper for printing logs immediately in CI environments
def log(message):
    print(message, flush=True)

class I18nSync:
    """
    Automatically synchronizes i18n resx resources using AI translation.
    """

    def __init__(self, config):
        self.base_url = config.base_url
        self.api_key = config.api_key
        self.model_id = config.model_id
        self.batch_size = config.batch_size
        self.i18n_paths = [os.path.abspath(path) for path in config.i18n_paths]

        # Initialize the OpenAI client
        self.client = OpenAI(
            api_key=self.api_key,
            base_url=self.base_url,
            max_retries=config.max_retries,
            timeout=120.0
        )

    def _read_resx(self, file_path: str) -> Dict[str, str]:
        """Reads a .resx file and returns a dictionary of resources."""
        if not os.path.exists(file_path):
            return {}
        try:
            # Use the safe parser from defusedxml
            tree = DefusedET.parse(file_path)
            root = tree.getroot()
            resources = {}
            for data_node in root.findall("./data"):
                name = data_node.get("name")
                value_node = data_node.find("value")
                if name and value_node is not None:
                    resources[name] = value_node.text or ""
            return resources
        except DefusedET.ParseError as e:
            log(f"  [x] Error parsing XML file {os.path.basename(file_path)}: {e}")
            return {}

    def _read_resx_ordered(self, file_path: str) -> List[Tuple[str, str]]:
        """Reads a .resx file and returns a list of (name, value) tuples, preserving order."""
        if not os.path.exists(file_path):
            return []
        try:
            # Use the safe parser from defusedxml
            tree = DefusedET.parse(file_path)
            root = tree.getroot()
            ordered_resources = []
            for data_node in root.findall("./data"):
                name = data_node.get("name")
                value_node = data_node.find("value")
                if name and value_node is not None:
                    ordered_resources.append((name, value_node.text or ""))
            return ordered_resources
        except DefusedET.ParseError as e:
            log(f"  [x] Error parsing XML file {os.path.basename(file_path)}: {e}")
            return []

    def _get_language_comment(self, file_path: str) -> Optional[str]:
        """Extracts the language name from an XML comment at the top of the file."""
        if not os.path.exists(file_path):
            return None
        with open(file_path, 'r', encoding='utf-8') as f:
            content = f.read(200) # Read first few lines
            match = re.search(r'<!--\s*(.+?)\s*-->', content)
            return match.group(1).strip() if match else None

    def _write_resx(self, file_path: str, resources: Dict[str, str], base_order: List[str], lang_comment: Optional[str]):
        """Writes resources to a .resx file, maintaining the order of the base file."""
        # Use the standard ElementTree for generation
        root = ET.Element('root')
        headers = {
            "resmimetype": "text/microsoft-resx",
            "version": "1.3",
            "reader": "System.Resources.ResXResourceReader, System.Windows.Forms, Version=2.0.0.0, Culture=neutral, PublicKeyToken=b77a5c561934e089",
            "writer": "System.Resources.ResXResourceWriter, System.Windows.Forms, Version=2.0.0.0, Culture=neutral, PublicKeyToken=b77a5c561934e089",
        }
        for name, value in headers.items():
            header_node = ET.SubElement(root, 'resheader', {'name': name})
            ET.SubElement(header_node, 'value').text = value

        for name in base_order:
            if name in resources:
                data_node = ET.SubElement(root, 'data', {'name': name, 'xml:space': 'preserve'})
                ET.SubElement(data_node, 'value').text = resources[name]

        import xml.dom.minidom
        xml_string = ET.tostring(root, encoding='unicode', method='xml')
        dom = xml.dom.minidom.parseString(xml_string)
        pretty_xml = dom.toprettyxml(indent="    ", encoding="utf-8").decode('utf-8')

        pretty_xml = re.sub(r'^<\?xml .*\?>\n', '', pretty_xml)
        final_content = '<?xml version="1.0" encoding="utf-8"?>\n'
        if lang_comment:
            final_content += f"<!-- {lang_comment} -->\n"
        final_content += pretty_xml

        with open(file_path, 'w', encoding='utf-8') as f:
            f.write(final_content)

    def _invoke_ai_translation(self, resources_to_translate: Dict[str, str], target_language: str) -> Optional[Dict[str, str]]:
        """Calls the AI API to translate a batch of resources using the openai library."""
        system_prompt = f"""
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
        try:
            response = self.client.chat.completions.create(
                model=self.model_id,
                messages=[
                    {"role": "system", "content": system_prompt},
                    {"role": "user", "content": json.dumps(resources_to_translate, ensure_ascii=False, indent=2)}
                ],
                temperature=0.3,
                response_format={"type": "json_object"}
            )
            content = response.choices[0].message.content.strip()

            # Robust JSON parsing: clean up potential markdown code blocks just in case
            match = re.search(r'```json\s*([\s\S]+?)\s*```', content)
            if match:
                content = match.group(1)

            return json.loads(content)

        except APIError as e:
            log(f"  ! API Error: {e}")
        except json.JSONDecodeError as e:
            log(f"  ! JSON Decode Error: {e}")
            # The library's retry mechanism should handle transient issues,
            # so if we get here, it's likely a persistent problem.
            log(f"  ! Received problematic content from API.")

        log(f"  [x] Failed to get translation after {self.client.max_retries} retries.")
        return None

    def run(self):
        """Executes the synchronization process."""
        log("=== Everywhere i18n Synchronization (Python) ===")
        log(f"Base URL: {self.base_url}")
        log(f"Model: {self.model_id}")
        log(f"I18N Paths: {', '.join(self.i18n_paths)}\n")

        for i18n_path in self.i18n_paths:
            self._run_directory(i18n_path)

        log("=== Synchronization Complete ===")

    def _run_directory(self, i18n_path: str):
        """Synchronizes one project-local I18N directory."""
        base_resx_path = os.path.join(i18n_path, "Strings.zh-hans.resx")

        if not os.path.exists(base_resx_path):
            log(f"[x] Error: Base resource file not found: {base_resx_path}")
            return

        log(f"Reading base resources from {base_resx_path}...")
        base_resources_ordered = self._read_resx_ordered(base_resx_path)
        base_resources = dict(base_resources_ordered)
        base_resource_keys = [key for key, _ in base_resources_ordered]
        log(f"Found {len(base_resources)} base resources.\n")

        # Explicitly exclude the base resx file from the list of files to process
        all_files = os.listdir(i18n_path)
        base_filename = os.path.basename(base_resx_path)
        localized_files = [f for f in all_files if f.startswith('Strings.') and f.endswith('.resx') and f != base_filename]

        for filename in localized_files:
            file_path = os.path.join(i18n_path, filename)
            lang_comment = self._get_language_comment(file_path)
            lang_name = lang_comment

            if not lang_name:
                match = re.match(r'Strings\.(.+)\.resx', filename)
                if match:
                    lang_name = match.group(1) # Fallback to language code from filename
                elif filename == 'Strings.resx':
                    lang_name = 'English'
                else:
                    log(f"  [x] Could not determine language code from filename: {filename}. Skipping.")
                    continue

            log(f"Processing: {filename} ({lang_name})")
            existing_resources = self._read_resx(file_path)
            all_resources = existing_resources.copy()
            
            # Identify keys that are missing OR have empty values in the target file
            missing_keys = [
                key for key, val in base_resources.items()
                if key not in existing_resources or (not existing_resources[key] and val)
            ]

            if not missing_keys:
                log("  [OK] No missing resources. Verifying order...")
                self._write_resx(file_path, all_resources, base_resource_keys, lang_comment)
                log("  [OK] File order synchronized.")
                continue

            log(f"  Found {len(missing_keys)} missing or empty resources.")
            to_translate = {}
            for key in missing_keys:
                if any(re.match(pattern, key) for pattern in NO_TRANSLATE_PATTERNS):
                    all_resources[key] = base_resources[key]
                    log(f"  - Copying non-translatable key: {key}")
                else:
                    to_translate[key] = base_resources[key]

            if to_translate:
                keys_to_translate = list(to_translate.keys())
                num_batches = (len(keys_to_translate) + self.batch_size - 1) // self.batch_size

                for i in range(num_batches):
                    batch_start = i * self.batch_size
                    batch_end = batch_start + self.batch_size
                    batch_keys = keys_to_translate[batch_start:batch_end]
                    batch_resources = {key: to_translate[key] for key in batch_keys}

                    log(f"  Translating batch {i + 1}/{num_batches} ({len(batch_resources)} items)...")
                    translations = self._invoke_ai_translation(batch_resources, lang_name)

                    if translations:
                        for key, value in translations.items():
                            if key not in to_translate:
                                log(f"  ! Warning: AI returned an unexpected key '{key}'. Ignoring.")
                            else:
                                all_resources[key] = value
                        log(f"  [OK] Batch {i + 1} translated.")
                    else:
                        log(f"  [x] Failed to translate batch {i + 1}.")

            log("  Saving and reordering file...")
            self._write_resx(file_path, all_resources, base_resource_keys, lang_comment)
            log(f"  [OK] {filename} updated.\n")

def main():
    """Main function to parse arguments and run the sync process."""
    parser = argparse.ArgumentParser(description="Synchronize i18n .resx files using AI translation.")
    env_base_url = os.environ.get('OPENAI_API_BASE') or os.environ.get('BASE_URL')
    env_api_key = os.environ.get('OPENAI_API_KEY')
    env_model_id = os.environ.get('MODEL_ID')

    parser.add_argument('--base-url', default=env_base_url, help="OpenAI-compatible API base URL.")
    parser.add_argument('--api-key', default=env_api_key, help="API key for authentication.")
    parser.add_argument('--model-id', default=env_model_id, help="The model ID to use for translation.")
    parser.add_argument('--batch-size', type=int, default=20, help="Number of resources to translate per batch.")
    parser.add_argument('--max-retries', type=int, default=3, help="Maximum retries for failed API calls.")
    parser.add_argument('--i18n-path', dest='i18n_paths', action='append', help="Path to an I18N directory. Can be specified multiple times.")

    args = parser.parse_args()

    if not args.i18n_paths:
        src_root = os.path.abspath(os.path.join(os.path.dirname(__file__), '..', 'src'))
        args.i18n_paths = [
            os.path.join(src_root, project, 'I18N')
            for project in os.listdir(src_root)
            if os.path.exists(os.path.join(src_root, project, 'I18N', 'Strings.zh-hans.resx'))
        ]

    if not all([args.base_url, args.api_key, args.model_id]):
        log("Error: --base-url, --api-key, and --model-id are required.")
        log("Provide them as arguments, or set OPENAI_API_BASE, OPENAI_API_KEY, MODEL_ID as environment variables.")
        exit(1)

    sync_tool = I18nSync(args)
    sync_tool.run()

if __name__ == "__main__":
    main()
