#!/usr/bin/env bash
# i18n-check.sh - Validate translation key consistency across all language files
#
# English.cs is the canonical schema. This script enforces that:
#   A) every other language defines exactly the same keys (no missing, no drift)
#   B) every key defined in English is actually consumed by code
#   C) no language file contains keys that don't exist in English
#
# Why this exists:
#   The Labels.Get() method silently falls back to English when a key is missing
#   in the user's locale. That makes drift invisible at runtime, so without this
#   guard a missing translation can ship undetected for months. This script makes
#   the drift loud at PR time.
#
# Usage:
#   ./scripts/i18n-check.sh              # Run all audits, exit 1 on ANY finding
#   ./scripts/i18n-check.sh --summary    # Single-line counts, no detail
#   ./scripts/i18n-check.sh --json       # Machine-readable output for CI
#   ./scripts/i18n-check.sh --quiet      # Only print errors, suppress headers
#   ./scripts/i18n-check.sh --help       # Show this help
#
# Exit codes:
#   0 - all audits pass
#   1 - at least one audit found an issue
#   2 - script setup error (missing files, etc.)

set -uo pipefail

SCRIPT_DIR="$(cd "$(dirname "$0")" && pwd)"
REPO_ROOT="$(cd "$SCRIPT_DIR/.." && pwd)"
LANG_DIR="$REPO_ROOT/src/I18n/Languages"
SRC_DIR="$REPO_ROOT/src"
ENGLISH_FILE="$LANG_DIR/English.cs"

# Colors (only when stdout is a TTY)
if [[ -t 1 ]]; then
    RED=$'\033[0;31m'; GREEN=$'\033[0;32m'
    DIM=$'\033[2m'; BOLD=$'\033[1m'; NC=$'\033[0m'
else
    RED=''; GREEN=''; DIM=''; BOLD=''; NC=''
fi

# Indirect-lookup allowlist:
#   These prefixes are emitted as string literals from C# code (e.g.
#   SystemInfoCollector.cs builds entries with "sysinfo_xxx" labels that are
#   later resolved via Labels.Get(entry.LabelKey)). A naive "find Labels.Get"
#   scan would falsely flag them as unused, so we treat any key matching these
#   prefixes as "used" if a string literal of that key appears anywhere in src/.
#   - "sysinfo_": SystemInfoCollector emits Label keys as bare string literals
#     and resolves them via Labels.Get(entry.LabelKey) later.
#   - "bind_", "btn_", "ally_grp_", "controller_mode_": ROG Ally controller
#     binding catalog (BindingGroups.cs) stores i18n keys in tuple Display
#     fields; HandheldWindow looks them up via Labels.Get(entry.Display) at
#     dropdown population time.
INDIRECT_PREFIXES=("sysinfo_" "bind_" "btn_" "ally_grp_" "controller_mode_")

# ----- argument parsing -----

mode="full"      # full | summary | json | quiet
for arg in "$@"; do
    case "$arg" in
        --summary) mode="summary" ;;
        --json)    mode="json" ;;
        --quiet)   mode="quiet" ;;
        --help|-h)
            sed -n '2,/^$/p' "$0" | sed 's/^# \{0,1\}//'
            exit 0
            ;;
        *)
            echo "Unknown option: $arg" >&2
            echo "Run '$0 --help' for usage." >&2
            exit 2
            ;;
    esac
done

if [[ ! -d "$LANG_DIR" ]]; then
    echo "ERROR: Language directory not found: $LANG_DIR" >&2
    exit 2
fi
if [[ ! -f "$ENGLISH_FILE" ]]; then
    echo "ERROR: English.cs not found: $ENGLISH_FILE" >&2
    exit 2
fi

# ----- helpers -----

# Extract translation keys from a language file.
# Matches lines like:    ["some_key"] = "value",
extract_keys() {
    local file="$1"
    grep -oE '\["[a-zA-Z0-9_]+"\]' "$file" | sed 's/^\["//;s/"\]$//' | sort -u
}

# Collect every key referenced from C#/XAML code (direct + indirect).
collect_used_keys() {
    {
        # Direct usages - capture the key from Labels.Get/Format calls.
        # Capture every quoted token on lines containing these calls (covers
        # ternary expressions like Labels.Format(cond ? "a" : "b", ...) which
        # produce two candidates per line).
        grep -rhE 'Labels\.(Get|Format)\(' \
            --include='*.cs' --include='*.axaml' \
            "$SRC_DIR" 2>/dev/null \
            | grep -oE '"[a-zA-Z0-9_]+"' \
            | sed 's/"//g'

        # Indirect usages - any literal matching a known dynamic prefix
        # anywhere in source (excluding the language dictionaries themselves).
        for prefix in "${INDIRECT_PREFIXES[@]}"; do
            grep -rhoE "\"${prefix}[a-zA-Z0-9_]+\"" \
                --include='*.cs' --include='*.axaml' \
                --exclude-dir='Languages' \
                "$SRC_DIR" 2>/dev/null \
                | sed 's/"//g'
        done
    } | sort -u
}

# ----- gather data -----

WORK_DIR="$(mktemp -d)"
trap 'rm -rf "$WORK_DIR"' EXIT

EN_KEYS_FILE="$WORK_DIR/en.keys"
USED_KEYS_FILE="$WORK_DIR/used.keys"

extract_keys "$ENGLISH_FILE" > "$EN_KEYS_FILE"
collect_used_keys > "$USED_KEYS_FILE"

en_count=$(wc -l < "$EN_KEYS_FILE" | tr -d ' ')
used_count=$(wc -l < "$USED_KEYS_FILE" | tr -d ' ')

# ----- Audit A: missing keys (per language) -----
# For each non-English language file, write a per-language report file
# containing the keys that are in English but missing locally. We use files
# instead of arrays-with-newlines to keep the data flow simple and reliable.

MISSING_DIR="$WORK_DIR/missing"
mkdir -p "$MISSING_DIR"

total_missing=0
missing_files=()

for file in "$LANG_DIR"/*.cs; do
    name=$(basename "$file")
    if [[ "$name" == "English.cs" ]]; then
        continue
    fi

    lang_keys_file="$WORK_DIR/${name}.keys"
    extract_keys "$file" > "$lang_keys_file"

    out="$MISSING_DIR/$name"
    comm -23 "$EN_KEYS_FILE" "$lang_keys_file" > "$out"

    if [[ -s "$out" ]]; then
        count=$(wc -l < "$out" | tr -d ' ')
        total_missing=$((total_missing + count))
        missing_files+=("$name:$count")
    else
        rm -f "$out"
    fi
done

# ----- Audit B: dead keys (defined in English, never used in code) -----

DEAD_FILE="$WORK_DIR/dead.keys"
comm -23 "$EN_KEYS_FILE" "$USED_KEYS_FILE" > "$DEAD_FILE"
dead_count=0
if [[ -s "$DEAD_FILE" ]]; then
    dead_count=$(wc -l < "$DEAD_FILE" | tr -d ' ')
fi

# ----- Audit C: drifting keys (in lang file but not in English) -----

DRIFT_DIR="$WORK_DIR/drift"
mkdir -p "$DRIFT_DIR"

total_drift=0
drift_files=()

for file in "$LANG_DIR"/*.cs; do
    name=$(basename "$file")
    if [[ "$name" == "English.cs" ]]; then
        continue
    fi

    lang_keys_file="$WORK_DIR/${name}.keys"
    out="$DRIFT_DIR/$name"
    comm -13 "$EN_KEYS_FILE" "$lang_keys_file" > "$out"

    if [[ -s "$out" ]]; then
        count=$(wc -l < "$out" | tr -d ' ')
        total_drift=$((total_drift + count))
        drift_files+=("$name:$count")
    else
        rm -f "$out"
    fi
done

# ----- output -----

exit_code=0
if [[ $total_missing -gt 0 || $dead_count -gt 0 || $total_drift -gt 0 ]]; then
    exit_code=1
fi

if [[ "$mode" == "json" ]]; then
    printf '{'
    printf '"english_keys":%d,' "$en_count"
    printf '"used_keys":%d,' "$used_count"
    printf '"total_missing":%d,' "$total_missing"
    printf '"dead_count":%d,' "$dead_count"
    printf '"total_drift":%d,' "$total_drift"
    printf '"languages_with_missing":%d,' "${#missing_files[@]}"
    printf '"languages_with_drift":%d,' "${#drift_files[@]}"
    printf '"exit_code":%d' "$exit_code"
    printf '}\n'
    exit "$exit_code"
fi

if [[ "$mode" == "summary" ]]; then
    if [[ $exit_code -eq 0 ]]; then
        echo "${GREEN}i18n OK${NC} - $en_count keys, $used_count used, 0 missing, 0 dead, 0 drift"
    else
        echo "${RED}i18n FAIL${NC} - $en_count keys | $total_missing missing across ${#missing_files[@]} langs | $dead_count dead | $total_drift drift across ${#drift_files[@]} langs"
    fi
    exit "$exit_code"
fi

# Full / quiet output

print_full() {
    [[ "$mode" == "full" ]] && echo "$@"
    return 0
}

print_full "${BOLD}=== i18n consistency check ===${NC}"
print_full "${DIM}English.cs:${NC} $en_count keys (canonical schema)"
print_full "${DIM}Code references:${NC} $used_count unique keys used"
print_full ""

# ---- Audit A output ----
print_full "${BOLD}[A] Missing keys${NC} ${DIM}(in English.cs but absent in other langs)${NC}"
if [[ $total_missing -eq 0 ]]; then
    print_full "  ${GREEN}✓ All 28 non-English files complete${NC}"
else
    for entry in "${missing_files[@]}"; do
        name="${entry%%:*}"
        count="${entry##*:}"
        if [[ "$mode" == "quiet" ]]; then
            echo "${RED}MISSING${NC} $name: $count keys"
        else
            echo "  ${RED}✗${NC} ${BOLD}$name${NC} - $count missing"
            while IFS= read -r k; do
                [[ -n "$k" ]] && echo "      ${DIM}- $k${NC}"
            done < "$MISSING_DIR/$name"
        fi
    done
fi
print_full ""

# ---- Audit B output ----
print_full "${BOLD}[B] Dead keys${NC} ${DIM}(defined in English but never used in code)${NC}"
if [[ $dead_count -eq 0 ]]; then
    print_full "  ${GREEN}✓ All English keys are referenced by code${NC}"
else
    if [[ "$mode" == "quiet" ]]; then
        echo "${RED}DEAD${NC} English.cs: $dead_count keys"
    else
        echo "  ${RED}✗${NC} ${BOLD}English.cs${NC} - $dead_count dead keys:"
        while IFS= read -r k; do
            [[ -n "$k" ]] && echo "      ${DIM}- $k${NC}"
        done < "$DEAD_FILE"
        print_full "  ${DIM}(also present in 28 other lang files - remove from all)${NC}"
    fi
fi
print_full ""

# ---- Audit C output ----
print_full "${BOLD}[C] Drifting keys${NC} ${DIM}(in lang file but not in English.cs)${NC}"
if [[ $total_drift -eq 0 ]]; then
    print_full "  ${GREEN}✓ No drift across 28 non-English files${NC}"
else
    for entry in "${drift_files[@]}"; do
        name="${entry%%:*}"
        count="${entry##*:}"
        if [[ "$mode" == "quiet" ]]; then
            echo "${RED}DRIFT${NC} $name: $count keys"
        else
            echo "  ${RED}✗${NC} ${BOLD}$name${NC} - $count drifting"
            while IFS= read -r k; do
                [[ -n "$k" ]] && echo "      ${DIM}- $k${NC}"
            done < "$DRIFT_DIR/$name"
        fi
    done
fi
print_full ""

# ---- Summary footer ----
if [[ $exit_code -eq 0 ]]; then
    print_full "${GREEN}${BOLD}✓ All audits passed${NC}"
else
    if [[ "$mode" == "quiet" ]]; then
        echo "${RED}FAIL${NC} $total_missing missing | $dead_count dead | $total_drift drift"
    else
        print_full "${RED}${BOLD}✗ i18n drift detected${NC} - $total_missing missing across ${#missing_files[@]} langs | $dead_count dead | $total_drift drift across ${#drift_files[@]} langs"
    fi
fi

exit "$exit_code"
