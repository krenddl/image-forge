#!/usr/bin/env bash
# ImageForge load test driver.
#
# Three modes:
#   ./scripts/load-test.sh download      # only fetch the image pack
#   ./scripts/load-test.sh upload        # only POST the local pack
#   ./scripts/load-test.sh all           # both, sequentially
#
# Tunables via env vars:
#   COUNT       how many images          (default 500)
#   CONCURRENCY parallel curl jobs       (default 20)
#   PACK_DIR    where to keep downloads  (default ./test-pack)
#   API         api base url             (default http://localhost:8080)
#   FORMAT      target format            (default webp)
#   MAXDIM      max dimension            (default 1280)
#
# Examples:
#   COUNT=100 CONCURRENCY=10 ./scripts/load-test.sh all
#   COUNT=500 ./scripts/load-test.sh download
#   ./scripts/load-test.sh upload                    # uses already-downloaded pack

set -e

# Defaults --------------------------------------------------------------------
: "${COUNT:=500}"
: "${CONCURRENCY:=20}"
: "${PACK_DIR:=./test-pack}"
: "${API:=http://localhost:8080}"
: "${FORMAT:=webp}"
: "${MAXDIM:=1280}"

MODE="${1:-all}"

# Pretty printing -------------------------------------------------------------
RED=$'\e[31m'; GRN=$'\e[32m'; YLW=$'\e[33m'; BLU=$'\e[34m'; DIM=$'\e[2m'; RST=$'\e[0m'

section() { printf '\n%s===%s %s %s===%s\n' "$BLU" "$RST" "$1" "$BLU" "$RST"; }
note()    { printf '%s%s%s\n' "$DIM" "$1" "$RST"; }
ok()      { printf '%s✓%s %s\n' "$GRN" "$RST" "$1"; }
warn()    { printf '%s!%s %s\n' "$YLW" "$RST" "$1"; }
fail()    { printf '%s✗%s %s\n' "$RED" "$RST" "$1"; }

# Wait until a background job slot is free ------------------------------------
throttle() {
    while (( $(jobs -rp | wc -l) >= CONCURRENCY )); do
        sleep 0.05
    done
}

# Download mode ---------------------------------------------------------------
download_pack() {
    section "Download $COUNT images from picsum.photos -> $PACK_DIR"
    mkdir -p "$PACK_DIR"

    local already=0 fetched=0
    local start_ts=$(date +%s)

    for i in $(seq 1 "$COUNT"); do
        local fname="$PACK_DIR/img-$(printf '%04d' "$i").jpg"
        if [[ -s "$fname" ]]; then
            already=$((already + 1))
            continue
        fi

        local url="https://picsum.photos/seed/forge${i}/3000/2000.jpg"
        throttle
        ( curl -sSL --max-time 60 -o "$fname" "$url" ) &
        fetched=$((fetched + 1))

        if (( i % 25 == 0 )); then
            printf '  %s%d / %d%s\r' "$DIM" "$i" "$COUNT" "$RST"
        fi
    done
    wait

    local end_ts=$(date +%s)
    local missing=0
    for i in $(seq 1 "$COUNT"); do
        [[ ! -s "$PACK_DIR/img-$(printf '%04d' "$i").jpg" ]] && missing=$((missing + 1))
    done

    printf '\n'
    note "elapsed: $((end_ts - start_ts))s"
    ok "downloaded $fetched, already had $already"
    (( missing > 0 )) && warn "missing $missing files (timeout / rate-limit?)"
}

# Upload mode -----------------------------------------------------------------
upload_pack() {
    section "Upload $PACK_DIR/*.jpg to $API"

    if [[ ! -d "$PACK_DIR" ]]; then
        fail "$PACK_DIR not found. Run download first."
        return 1
    fi

    local files=("$PACK_DIR"/*.jpg)
    local total=${#files[@]}
    if (( total == 0 )); then
        fail "no .jpg files in $PACK_DIR"
        return 1
    fi

    note "found $total files; concurrency=$CONCURRENCY"
    note "format=$FORMAT, maxDimension=$MAXDIM"

    # Check API
    if ! curl -sSf -o /dev/null "$API/"; then
        fail "API not reachable at $API"
        return 1
    fi
    ok "API responsive"

    # Snapshot baseline lifetime stats
    local before_json
    before_json=$(curl -sS "$API/api/lifetime-stats")
    local before_processed=$(echo "$before_json" | grep -oE '"processed":[0-9]+' | cut -d: -f2)
    local before_in=$(echo "$before_json"        | grep -oE '"bytesIn":[0-9]+'   | cut -d: -f2)
    local before_out=$(echo "$before_json"       | grep -oE '"bytesOut":[0-9]+'  | cut -d: -f2)
    note "before: processed=$before_processed, bytesIn=$before_in, bytesOut=$before_out"

    # Counters
    local ok_count=0 fail_count=0 i=0
    local logfile=$(mktemp)
    trap "rm -f $logfile" EXIT

    local start_ts=$(date +%s)
    section "POSTing"

    for f in "${files[@]}"; do
        i=$((i + 1))
        throttle
        {
            local code
            code=$(curl -sS -o /dev/null -w '%{http_code}' \
                -X POST \
                -F "file=@$f;type=image/jpeg" \
                -F "format=$FORMAT" \
                -F "maxDimension=$MAXDIM" \
                "$API/api/images")
            echo "$code" >> "$logfile"
        } &

        if (( i % 50 == 0 )); then
            printf '  %sposted %d / %d%s\r' "$DIM" "$i" "$total" "$RST"
        fi
    done
    wait

    printf '\n'
    local post_done_ts=$(date +%s)
    note "all POSTs returned in $((post_done_ts - start_ts))s"

    while IFS= read -r code; do
        if [[ "$code" == "200" ]]; then
            ok_count=$((ok_count + 1))
        else
            fail_count=$((fail_count + 1))
        fi
    done < "$logfile"

    ok   "200 OK: $ok_count"
    (( fail_count > 0 )) && warn "non-200: $fail_count"

    section "Waiting for workers to drain..."
    local idle_rounds=0
    while (( idle_rounds < 3 )); do
        sleep 2
        local stats
        stats=$(curl -sS "$API/api/stats")
        local ready=$(echo    "$stats" | grep -oE '"messagesReady":[0-9]+'         | cut -d: -f2)
        local inflight=$(echo "$stats" | grep -oE '"messagesUnacknowledged":[0-9]+' | cut -d: -f2)
        local consumers=$(echo "$stats" | grep -oE '"consumers":[0-9]+'             | cut -d: -f2)
        printf '  ready=%-4s inflight=%-3s consumers=%s\n' "${ready:-?}" "${inflight:-?}" "${consumers:-?}"
        if [[ "${ready:-0}" == "0" && "${inflight:-0}" == "0" ]]; then
            idle_rounds=$((idle_rounds + 1))
        else
            idle_rounds=0
        fi
    done

    local end_ts=$(date +%s)

    # Final lifetime snapshot
    local after_json
    after_json=$(curl -sS "$API/api/lifetime-stats")
    local after_processed=$(echo "$after_json" | grep -oE '"processed":[0-9]+' | cut -d: -f2)
    local after_in=$(echo "$after_json"        | grep -oE '"bytesIn":[0-9]+'   | cut -d: -f2)
    local after_out=$(echo "$after_json"       | grep -oE '"bytesOut":[0-9]+'  | cut -d: -f2)

    local delta_processed=$((after_processed - before_processed))
    local delta_in=$((after_in   - before_in))
    local delta_out=$((after_out - before_out))
    local saved=$((delta_in - delta_out))

    section "Run summary"
    printf '  files posted          : %s\n' "$ok_count"
    printf '  tasks completed       : %s\n' "$delta_processed"
    printf '  total time (post->done): %ss\n' "$((end_ts - start_ts))"
    if (( delta_processed > 0 )); then
        printf '  throughput            : %.2f tasks/sec\n' \
            "$(echo "scale=2; $delta_processed / ($end_ts - $start_ts)" | awk -v d="$delta_processed" -v t="$((end_ts - start_ts))" 'BEGIN{ printf "%.2f", d/t }')"
    fi
    printf '  bytes in              : %s\n' "$delta_in"
    printf '  bytes out             : %s\n' "$delta_out"
    if (( delta_in > 0 )); then
        printf '  saved                 : %s bytes (%d%% of original)\n' \
            "$saved" "$((delta_out * 100 / delta_in))"
    fi
}

# Dispatcher ------------------------------------------------------------------
case "$MODE" in
    download)  download_pack ;;
    upload)    upload_pack   ;;
    all)       download_pack; upload_pack ;;
    *) echo "usage: $0 {download|upload|all}"; exit 1 ;;
esac
