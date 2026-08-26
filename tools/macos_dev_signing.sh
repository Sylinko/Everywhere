#!/bin/bash
set -euo pipefail

umask 077

SCRIPT_DIRECTORY="$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")" && pwd -P)"
REPOSITORY_ROOT="$(cd -- "$SCRIPT_DIRECTORY/.." && pwd -P)"
SIGNING_DIRECTORY="$REPOSITORY_ROOT/.macos-signing"
KEYCHAIN_PATH="$SIGNING_DIRECTORY/macos-signing.keychain-db"
KEYCHAIN_PASSWORD_PATH="$SIGNING_DIRECTORY/keychain-password"
IDENTITY_PATH="$SIGNING_DIRECTORY/identity.sha1"
SETUP_VERSION_PATH="$SIGNING_DIRECTORY/setup-version"
CERTIFICATE_NAME="Everywhere Local Development"
SETUP_VERSION="1"

TEMPORARY_DIRECTORY=""
KEYCHAIN_UNLOCKED=false
INITIALIZING=false
SEARCH_LIST_MODIFIED=false
ORIGINAL_KEYCHAINS=()

fail() {
    echo "error: $*" >&2
    exit 1
}

cleanup() {
    local exit_code=$?
    trap - EXIT

    if [[ "$KEYCHAIN_UNLOCKED" == true && -f "$KEYCHAIN_PATH" ]]; then
        security lock-keychain "$KEYCHAIN_PATH" >/dev/null 2>&1 || true
    fi

    if [[ "$SEARCH_LIST_MODIFIED" == true && ${#ORIGINAL_KEYCHAINS[@]} -gt 0 ]]; then
        security list-keychains -d user -s "${ORIGINAL_KEYCHAINS[@]}" >/dev/null 2>&1 || true
    fi

    if [[ -n "$TEMPORARY_DIRECTORY" && -d "$TEMPORARY_DIRECTORY" ]]; then
        rm -rf -- "$TEMPORARY_DIRECTORY"
    fi

    if [[ $exit_code -ne 0 && "$INITIALIZING" == true ]]; then
        security delete-keychain "$KEYCHAIN_PATH" >/dev/null 2>&1 || true
        rm -f -- "$KEYCHAIN_PATH" "$KEYCHAIN_PASSWORD_PATH" "$IDENTITY_PATH" "$SETUP_VERSION_PATH"
    fi

    exit "$exit_code"
}

trap cleanup EXIT

read_single_line() {
    local path=$1
    tr -d '\r\n' < "$path"
}

validate_existing_identity() {
    local identity
    local setup_version

    identity="$(read_single_line "$IDENTITY_PATH")"
    identity="$(printf '%s' "$identity" | tr '[:lower:]' '[:upper:]')"
    setup_version="$(read_single_line "$SETUP_VERSION_PATH")"

    [[ "$identity" =~ ^[0-9A-Fa-f]{40}$ ]] || fail "Invalid identity fingerprint in $IDENTITY_PATH. Delete $SIGNING_DIRECTORY to recreate it."
    [[ "$setup_version" == "$SETUP_VERSION" ]] || fail "Unsupported signing setup version in $SETUP_VERSION_PATH. Delete $SIGNING_DIRECTORY to recreate it."

    if ! security find-identity -p codesigning "$KEYCHAIN_PATH" 2>/dev/null | grep -Fq "$identity"; then
        fail "The local signing identity is missing from $KEYCHAIN_PATH. Delete $SIGNING_DIRECTORY to recreate it."
    fi

    if ! security find-key -t private -s "$KEYCHAIN_PATH" >/dev/null 2>&1; then
        fail "The private key is missing from $KEYCHAIN_PATH. Delete $SIGNING_DIRECTORY to recreate it."
    fi

    echo "$identity"
}

create_identity() {
    local keychain_password
    local identity
    local private_key_path
    local certificate_path

    mkdir -p "$SIGNING_DIRECTORY"
    chmod 700 "$SIGNING_DIRECTORY"

    TEMPORARY_DIRECTORY="$(mktemp -d "$SIGNING_DIRECTORY/.setup.XXXXXX")"
    INITIALIZING=true

    private_key_path="$TEMPORARY_DIRECTORY/private-key.pem"
    certificate_path="$TEMPORARY_DIRECTORY/certificate.pem"
    keychain_password="$(openssl rand -hex 32)"

    echo "Creating repository-local macOS development signing identity..." >&2

    openssl req -new -newkey rsa:2048 -nodes -x509 -sha256 -days 3650 \
        -subj "/CN=$CERTIFICATE_NAME/O=Everywhere Development" \
        -addext "basicConstraints=critical,CA:FALSE" \
        -addext "keyUsage=critical,digitalSignature" \
        -addext "extendedKeyUsage=critical,codeSigning" \
        -addext "subjectKeyIdentifier=hash" \
        -keyout "$private_key_path" \
        -out "$certificate_path" \
        >/dev/null 2>&1

    security create-keychain -p "$keychain_password" "$KEYCHAIN_PATH"
    security set-keychain-settings -lut 21600 "$KEYCHAIN_PATH"
    security unlock-keychain -p "$keychain_password" "$KEYCHAIN_PATH"
    KEYCHAIN_UNLOCKED=true

    security import "$private_key_path" \
        -k "$KEYCHAIN_PATH" \
        -T /usr/bin/codesign \
        >/dev/null

    security import "$certificate_path" \
        -k "$KEYCHAIN_PATH" \
        -t cert \
        -f pemseq \
        >/dev/null

    security set-key-partition-list \
        -S "apple-tool:,apple:,codesign:" \
        -s \
        -k "$keychain_password" \
        "$KEYCHAIN_PATH" \
        >/dev/null

    identity="$(security find-identity -p codesigning "$KEYCHAIN_PATH" | awk -v name="$CERTIFICATE_NAME" 'index($0, name) { print $2; exit }')"
    [[ "$identity" =~ ^[0-9A-Fa-f]{40}$ ]] || fail "The generated certificate is not available as a code-signing identity."
    identity="$(printf '%s' "$identity" | tr '[:lower:]' '[:upper:]')"

    printf '%s\n' "$keychain_password" > "$KEYCHAIN_PASSWORD_PATH"
    printf '%s\n' "$identity" > "$IDENTITY_PATH"
    printf '%s\n' "$SETUP_VERSION" > "$SETUP_VERSION_PATH"
    chmod 600 "$KEYCHAIN_PASSWORD_PATH" "$IDENTITY_PATH" "$SETUP_VERSION_PATH" "$KEYCHAIN_PATH"

    INITIALIZING=false
    echo "Created local signing identity $identity." >&2
    echo "$identity"
}

ensure_identity() {
    local existing_file_count=0
    local required_path

    for required_path in "$KEYCHAIN_PATH" "$KEYCHAIN_PASSWORD_PATH" "$IDENTITY_PATH" "$SETUP_VERSION_PATH"; do
        if [[ -e "$required_path" ]]; then
            existing_file_count=$((existing_file_count + 1))
        fi
    done

    if [[ $existing_file_count -eq 4 ]]; then
        validate_existing_identity
        return
    fi

    if [[ $existing_file_count -ne 0 ]]; then
        fail "The local signing state in $SIGNING_DIRECTORY is incomplete. Delete that directory to recreate it."
    fi

    create_identity
}

unlock_keychain() {
    local keychain_password
    keychain_password="$(read_single_line "$KEYCHAIN_PASSWORD_PATH")"
    security unlock-keychain -p "$keychain_password" "$KEYCHAIN_PATH"
    KEYCHAIN_UNLOCKED=true
}

add_keychain_to_search_list() {
    local existing_keychain
    local search_list_line

    while IFS= read -r search_list_line; do
        existing_keychain="${search_list_line#"${search_list_line%%[![:space:]]*}"}"
        existing_keychain="${existing_keychain#\"}"
        existing_keychain="${existing_keychain%\"}"
        ORIGINAL_KEYCHAINS+=("$existing_keychain")

        if [[ "$existing_keychain" == "$KEYCHAIN_PATH" ]]; then
            return
        fi
    done < <(security list-keychains -d user)

    [[ ${#ORIGINAL_KEYCHAINS[@]} -gt 0 ]] || fail "The user keychain search list is empty."

    SEARCH_LIST_MODIFIED=true
    security list-keychains -d user -s "$KEYCHAIN_PATH" "${ORIGINAL_KEYCHAINS[@]}"
}

sign_code() {
    local identity=$1
    local path=$2

    /usr/bin/codesign \
        --keychain "$KEYCHAIN_PATH" \
        --force \
        --sign "$identity" \
        --timestamp=none \
        "$path"
}

sign_app() {
    local app_path=$1
    local file_description
    local identity
    local native_code_path

    [[ -d "$app_path" ]] || fail "App bundle not found: $app_path"

    identity="$(ensure_identity)"
    unlock_keychain
    add_keychain_to_search_list

    echo "Signing repository-local Debug app with identity $identity..." >&2

    while IFS= read -r -d '' native_code_path; do
        case "$native_code_path" in
            *.dylib|*.so)
                ;;
            *)
                [[ -x "$native_code_path" ]] || continue
                ;;
        esac

        file_description="$(/usr/bin/file -b "$native_code_path")"
        [[ "$file_description" == *Mach-O* ]] || continue
        sign_code "$identity" "$native_code_path"
    done < <(find "$app_path/Contents" -type f -print0)

    sign_code "$identity" "$app_path"
    /usr/bin/codesign --verify --deep --strict --verbose=2 "$app_path"

    echo "Debug app signing complete." >&2
}

usage() {
    echo "Usage: $0 ensure | sign-app <app-bundle>" >&2
    exit 2
}

case "${1:-}" in
    ensure)
        [[ $# -eq 1 ]] || usage
        ensure_identity
        ;;
    sign-app)
        [[ $# -eq 2 ]] || usage
        sign_app "$2"
        ;;
    *)
        usage
        ;;
esac
