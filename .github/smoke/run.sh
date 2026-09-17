#!/usr/bin/env bash
# Smoke test for a release archive, run on the platform it was built for.
#
#   run.sh <archive> <work directory>
#
# Checks that the archive holds only the executable and the licence, that
# --help and -? print the usage and an unknown option prints it and exits
# with status 2, then serves two images and checks that:
#   - the server starts with HOME pointing at a directory that doesn't exist
#     (Linux and macOS), and never creates it,
#   - a browse lists both images, with links that download them intact,
#   - their thumbnails are JPEGs (ImageSharp),
#   - the metadata cache opens and is written (LiteDB),
#   - nothing is logged at ERROR or FATAL,
#   - SIGTERM, as a service manager sends it, stops the server and closes
#     the cache (Linux and macOS).
set -euo pipefail

archive=$(cd "$(dirname "$1")" && pwd)/$(basename "$1")
work=$2
fixtures=$(cd "$(dirname "$0")" && pwd)

fail() {
  echo "::error::$*"
  if [ -f "$work/sdlna.log" ]; then
    echo "--- sdlna.log"
    cat "$work/sdlna.log"
  fi
  exit 1
}

rm -rf "$work"
mkdir -p "$work/app" "$work/media"
cd "$work/app"
case "$archive" in
  *.zip)
    if command -v unzip >/dev/null; then unzip -q "$archive"; else 7z x -y "$archive" >/dev/null; fi
    ;;
  *)
    tar -xzf "$archive"
    ;;
esac

exe=sdlna
windows=false
if [ -f sdlna.exe ]; then
  exe=sdlna.exe
  windows=true
fi
contents=$(ls -A | sort | tr '\n' ' ')
[ "$contents" = "LICENSE $exe " ] || fail "unexpected archive contents: $contents"
echo "archive holds: $contents"

# Single-file builds couldn't print the usage (GetOptNet looked for the
# executable's assembly file, which they don't have).
usage() {
  local expected=$1
  shift
  local status=0
  "./$exe" "$@" >"$work/usage.txt" 2>&1 </dev/null || status=$?
  if [ "$status" -ne "$expected" ] || ! grep -q '^Usage: sdlna ' "$work/usage.txt"; then
    cat "$work/usage.txt"
    fail "sdlna $* exited with status $status (expected $expected) or printed no usage"
  fi
}
usage 0 --help
usage 0 '-?'
usage 2 --no-such-option
echo "usage OK"

cp "$fixtures/logo.png" "$fixtures/photo.jpg" "$work/media/"

# Git Bash's kill can't see native Windows processes, so there the
# executable's liveness is checked with tasklist.
alive() {
  if $windows; then
    tasklist //FI "IMAGENAME eq sdlna.exe" //NH 2>/dev/null | grep -qi 'sdlna\.exe'
  else
    kill -0 "$pid" 2>/dev/null
  fi
}

home="$work/no-such-home"
cache="$work/cache.db"
HOME="$home" "./$exe" -l INFO -p 0 -c "$cache" "$work/media" >"$work/sdlna.log" 2>&1 &
pid=$!

port=
for _ in $(seq 1 120); do
  sleep 0.5
  if ! alive; then
    status=0
    wait "$pid" || status=$?
    fail "sdlna exited during startup, with status $status"
  fi
  if grep -q ' mounted' "$work/sdlna.log"; then
    port=$(sed -n 's/.* on port \([0-9][0-9]*\).*/\1/p' "$work/sdlna.log" | head -n1)
    break
  fi
done
[ -n "$port" ] || fail "sdlna did not start within a minute"
echo "listening on port $port"

base="http://127.0.0.1:$port"
prefix=$(curl -sf "$base/" | grep -o 'href="/mm-[0-9]*/"' | head -n1 | cut -d'"' -f2)
[ -n "$prefix" ] || fail "no mount on the index page"

didl=$(curl -sf "$base${prefix}control" \
  -H 'Content-Type: text/xml; charset="utf-8"' \
  -H 'SOAPACTION: "urn:schemas-upnp-org:service:ContentDirectory:1#Browse"' \
  --data-binary '<?xml version="1.0"?><s:Envelope xmlns:s="http://schemas.xmlsoap.org/soap/envelope/"><s:Body><u:Browse xmlns:u="urn:schemas-upnp-org:service:ContentDirectory:1"><ObjectID>0</ObjectID><BrowseFlag>BrowseDirectChildren</BrowseFlag><Filter>*</Filter><StartingIndex>0</StartingIndex><RequestedCount>10</RequestedCount><SortCriteria></SortCriteria></u:Browse></s:Body></s:Envelope>') \
  || fail "browse request failed"

for name in logo photo; do
  echo "$didl" | grep -q "dc:title&gt;$name&lt;" || fail "$name is not listed"
done

files=$(echo "$didl" | grep -o 'http://[^&<"]*/file/[^&<"]*' | sort -u)
covers=$(echo "$didl" | grep -o 'http://[^&<"]*/cover/[^&<"]*' | sort -u)
[ "$(echo "$files" | wc -l)" -eq 2 ] || fail "expected 2 file links, got: $files"
[ "$(echo "$covers" | wc -l)" -eq 2 ] || fail "expected 2 cover links, got: $covers"

downloaded=0
for url in $files; do
  curl -sf "$url" -o "$work/download"
  if cmp -s "$work/download" "$work/media/logo.png" || cmp -s "$work/download" "$work/media/photo.jpg"; then
    downloaded=$((downloaded + 1))
  fi
done
[ "$downloaded" -eq 2 ] || fail "only $downloaded of 2 files downloaded intact"

for url in $covers; do
  curl -sf "$url" -o "$work/cover" || fail "cover request failed: $url"
  magic=$(od -An -tx1 -N3 "$work/cover" | tr -d ' \r\n')
  [ "$magic" = "ffd8ff" ] || fail "cover is not a JPEG ($magic): $url"
done
echo "browse, downloads and thumbnails OK"

grep -q 'is ready' "$work/sdlna.log" || fail "the cache did not open"

if $windows; then
  taskkill //F //IM sdlna.exe >/dev/null
  wait "$pid" 2>/dev/null || true
else
  kill -TERM "$pid"
  for _ in $(seq 1 30); do
    kill -0 "$pid" 2>/dev/null || break
    sleep 0.5
  done
  if kill -0 "$pid" 2>/dev/null; then
    kill -9 "$pid"
    fail "sdlna did not stop on SIGTERM"
  fi
  [ ! -e "$cache.lock" ] || fail "the cache was not closed: its lock file remains"
  [ ! -e "$home" ] || fail "sdlna created the missing home directory"
fi

[ -s "$cache" ] || fail "the cache file was not written"
if grep -E '^(ERROR|FATAL)' "$work/sdlna.log"; then
  fail "errors were logged"
fi
echo "smoke test passed"
