import sys
import xml.etree.ElementTree as ET
from collections import defaultdict
from pathlib import Path

from catalog import ROOT, SOURCE_NAME, expected_locales, locale_files, project_directories


def read_keys(path: Path) -> list[str]:
    tree = ET.parse(path)
    return [
        node.attrib["name"]
        for node in tree.getroot().findall("./data")
        if "name" in node.attrib
    ]


def main() -> int:
    errors: list[str] = []
    owners: dict[str, Path] = {}
    duplicates: dict[str, list[Path]] = defaultdict(list)

    i18n_dirs = project_directories()
    locales = expected_locales()

    for i18n_dir in i18n_dirs:
        for name in sorted(locales - locale_files(i18n_dir)):
            errors.append(f"Missing locale file: {(i18n_dir / name).relative_to(ROOT)}")
        source = i18n_dir / SOURCE_NAME
        if not source.is_file():
            continue
        source_keys = read_keys(source)
        source_key_set = set(source_keys)

        for key in source_keys:
            if key in owners:
                duplicates[key].extend([owners[key], source])
            else:
                owners[key] = source

        for resx in sorted(i18n_dir.glob("Strings*.resx")):
            keys = set(read_keys(resx))
            missing = source_key_set - keys
            extra = keys - source_key_set
            if missing:
                errors.append(f"{resx.relative_to(ROOT)} is missing {len(missing)} key(s): {', '.join(sorted(missing)[:5])}")
            if extra:
                errors.append(f"{resx.relative_to(ROOT)} has {len(extra)} extra key(s): {', '.join(sorted(extra)[:5])}")

    for key, paths in sorted(duplicates.items()):
        locations = ", ".join(sorted({str(path.relative_to(ROOT)) for path in paths}))
        errors.append(f"Duplicate key {key}: {locations}")

    if errors:
        print("I18N validation failed:")
        for error in errors:
            print(f"  - {error}")
        return 1

    print(f"I18N validation passed for {len(i18n_dirs)} project(s), {len(owners)} key(s).")
    return 0


if __name__ == "__main__":
    sys.exit(main())
