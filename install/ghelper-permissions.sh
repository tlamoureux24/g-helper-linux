#!/usr/bin/env bash
# Apply G-Helper hardware permissions from udev.
#
# This script is installed root-owned at /usr/local/lib/ghelper and called only
# from udev rules. It gives the ghelper group read/write access to selected
# kernel device nodes without opening them to every local user.
set -uo pipefail

GROUP="${GHELPER_GROUP:-ghelper}"
MODE="0660"

apply_one() {
    local path="$1"
    [[ -e "$path" ]] || return 0
    chgrp "$GROUP" "$path" 2>/dev/null || true
    chmod "$MODE" "$path" 2>/dev/null || true
}

case "${1:-}" in
    path)
        shift
        for path in "$@"; do
            apply_one "$path"
        done
        ;;
    glob)
        shift
        for pattern in "$@"; do
            for path in $pattern; do
                [[ -e "$path" ]] && apply_one "$path"
            done
        done
        ;;
    asus-hidraw)
        dev="${2:-}"
        [[ "$dev" == hidraw* ]] || exit 0
        if grep -q 00000B05 "/sys/class/hidraw/$dev/device/uevent" 2>/dev/null; then
            apply_one "/dev/$dev"
        fi
        ;;
    *)
        echo "Usage: $0 {path <file>...|glob <pattern>...|asus-hidraw <hidrawN>}" >&2
        exit 1
        ;;
esac
