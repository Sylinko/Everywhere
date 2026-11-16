from pathlib import Path


ROOT = Path(__file__).resolve().parents[2]
SRC = ROOT / "src"
CORE_I18N = SRC / "Everywhere.Core" / "I18N"
SOURCE_NAME = "Strings.zh-hans.resx"


def project_directories() -> list[Path]:
    return sorted(path for path in SRC.glob("*/I18N") if any(path.glob("Strings*.resx")))


def locale_files(directory: Path) -> set[str]:
    return {path.name for path in directory.glob("Strings*.resx")}


def expected_locales() -> set[str]:
    """Core ships every supported locale. The neutral resource is English."""
    names = locale_files(CORE_I18N)
    if not {"Strings.resx", SOURCE_NAME} <= names:
        raise ValueError(f"Missing English or Simplified Chinese resources in {CORE_I18N}")
    return names
