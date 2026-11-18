#!/usr/bin/env bash
# End-to-end demo / smoke test against a running SeatFlow stack (docker compose or kind). Needs curl and jq.
#
#   scripts/demo.sh [happy|race|declined|late-payment|all]
#   BASE_URL=http://localhost:8081 MAILPIT_URL=http://localhost:8026 scripts/demo.sh all   # kind
set -euo pipefail

BASE_URL="${BASE_URL:-http://localhost:8080}"
MAILPIT_URL="${MAILPIT_URL:-http://localhost:8025}"
SCENARIO="${1:-happy}"
TMP="$(mktemp -d)"
trap 'rm -rf "$TMP"' EXIT

step() { printf '\n\033[36m==> %s\033[0m\n' "$*"; }
ok() { printf '    \033[32mOK\033[0m  %s\n' "$*"; }
info() { printf '        %s\n' "$*" >&2; }
fail() { printf '    \033[31mFAIL\033[0m %s\n' "$*" >&2; exit 1; }

# api METHOD PATH [TOKEN] [JSON] [HEADER] -> sets STATUS, BODY, HEADERS
api() {
  local method=$1 path=$2 token=${3:-} data=${4:-} header=${5:-}
  local args=(-s -o "$TMP/body" -D "$TMP/headers" -w '%{http_code}' -X "$method" "$BASE_URL$path")
  if [[ -n $token ]]; then args+=(-H "Authorization: Bearer $token"); fi
  if [[ -n $data ]]; then args+=(-H 'Content-Type: application/json' --data "$data"); fi
  if [[ -n $header ]]; then args+=(-H "$header"); fi
  STATUS=$(curl "${args[@]}" || echo 000)
  BODY=$(cat "$TMP/body" 2>/dev/null || true)
  HEADERS=$(cat "$TMP/headers" 2>/dev/null || true)
}

token() {
  api POST /auth/token "" "{\"email\":\"$1\"}"
  [[ $STATUS == 200 ]] || fail "could not get a token for $1 (HTTP $STATUS)"
  jq -r .access_token <<<"$BODY"
}

new_key() { cat /proc/sys/kernel/random/uuid 2>/dev/null || uuidgen; }

hold() { # hold TOKEN EVENT_ID KEY SEAT...
  local token=$1 event=$2 key=$3
  shift 3
  api POST /api/bookings "$token" "$(jq -nc --arg e "$event" '{eventId: $e, seatIds: $ARGS.positional}' --args "$@")" "Idempotency-Key: $key"
}

free_seats() { # free_seats EVENT_ID COUNT SECTION -> "A-1-1 A-1-2"
  api GET "/api/bookings/events/$1/seats"
  local seats
  seats=$(jq -r --arg s "$3" --argjson n "$2" '[.seats[] | select(.status == "Available" and .section == $s) | .seatId][:$n] | join(" ")' <<<"$BODY")
  [[ $(wc -w <<<"$seats") -eq $2 ]] || fail "not enough free seats in section $3; reset with 'docker compose down -v'"
  echo "$seats"
}

wait_status() { # wait_status TOKEN BOOKING_ID TIMEOUT STATUS... -> prints the booking JSON
  local token=$1 id=$2 timeout=$3 last="" status want
  shift 3
  local deadline=$((SECONDS + timeout))
  while ((SECONDS < deadline)); do
    api GET "/api/bookings/$id" "$token"
    status=$(jq -r .status <<<"$BODY")
    if [[ $status != "$last" ]]; then info "status: $status"; last=$status; fi
    for want in "$@"; do
      if [[ $status == "$want" ]]; then echo "$BODY"; return 0; fi
    done
    sleep 0.5
  done
  fail "booking $id did not reach $* within ${timeout}s (last: $last)"
}

seat_status() { # seat_status EVENT_ID SEAT_ID
  api GET "/api/bookings/events/$1/seats"
  jq -r --arg id "$2" '.seats[] | select(.seatId == $id) | .status' <<<"$BODY"
}

setup() {
  step "Waiting for the gateway at $BASE_URL"
  for _ in $(seq 1 120); do
    api GET /health/ready
    [[ $STATUS == 200 ]] && break
    sleep 1
  done
  [[ $STATUS == 200 ]] || fail "gateway not ready"
  ok "gateway is ready"

  step "Fetching the catalog"
  api GET /api/catalog/events
  EVENT_ID=$(jq -r '.[0].id' <<<"$BODY")
  [[ $EVENT_ID != null ]] || fail "the catalog has no events"
  ok "using '$(jq -r '.[0].title' <<<"$BODY")' ($EVENT_ID)"

  step "Waiting for Booking to receive the event over RabbitMQ"
  for _ in $(seq 1 120); do
    api GET "/api/bookings/events/$EVENT_ID/seats"
    [[ $STATUS == 200 ]] && break
    sleep 0.5
  done
  [[ $STATUS == 200 ]] || fail "seat map never appeared"
  ok "seat map: $(jq -r '"\(.available) available, \(.held) held, \(.sold) sold"' <<<"$BODY")"
}

happy() {
  local alice bob key booking_id seats
  alice=$(token alice@example.com)
  bob=$(token bob@example.com)
  read -r -a seats <<<"$(free_seats "$EVENT_ID" 2 A)"

  step "Alice holds ${seats[*]}"
  key=$(new_key)
  hold "$alice" "$EVENT_ID" "$key" "${seats[@]}"
  [[ $STATUS == 201 ]] || fail "expected 201, got $STATUS: $BODY"
  booking_id=$(jq -r .id <<<"$BODY")
  ok "201 Created, booking $booking_id, total $(jq -r '"\(.total) \(.currency)"' <<<"$BODY")"

  step "Retry with the same Idempotency-Key"
  hold "$alice" "$EVENT_ID" "$key" "${seats[@]}"
  [[ $STATUS == 200 && $(jq -r .id <<<"$BODY") == "$booking_id" ]] || fail "expected a 200 replay, got $STATUS"
  grep -qi '^idempotent-replayed: true' <<<"$HEADERS" || fail "missing Idempotent-Replayed header"
  ok "200 OK, same booking, Idempotent-Replayed: true"

  step "Bob tries to grab ${seats[0]}"
  hold "$bob" "$EVENT_ID" "$(new_key)" "${seats[0]}"
  [[ $STATUS == 409 ]] || fail "expected 409, got $STATUS"
  ok "409 Conflict"

  step "Alice pays with tok_visa"
  api POST "/api/bookings/$booking_id/checkout" "$alice" '{"paymentToken":"tok_visa"}'
  [[ $STATUS == 202 ]] || fail "expected 202, got $STATUS"
  [[ $(wait_status "$alice" "$booking_id" 30 Confirmed PaymentFailed Expired | jq -r .status) == Confirmed ]] || fail "not confirmed"
  ok "booking Confirmed"
  [[ $(seat_status "$EVENT_ID" "${seats[0]}") == Sold ]] || fail "seat not sold"
  ok "${seats[0]} is Sold"

  step "Checking the e-mail sent by the notification service"
  local query found=0
  query=$(jq -rn --arg q "to:alice@example.com $booking_id" '$q | @uri')
  for _ in $(seq 1 40); do
    if [[ $(curl -s "$MAILPIT_URL/api/v1/search?query=$query" | jq -r '.messages_count // 0') -gt 0 ]]; then found=1; break; fi
    sleep 0.5
  done
  [[ $found == 1 ]] || fail "no confirmation e-mail for $booking_id"
  ok "confirmation e-mail delivered"
}

race() {
  local seat users=20 i
  seat=$(free_seats "$EVENT_ID" 1 VIP)
  step "$users users try to hold $seat at the same moment"
  local body
  body=$(jq -nc --arg e "$EVENT_ID" --arg s "$seat" '{eventId: $e, seatIds: [$s]}')
  for i in $(seq 1 "$users"); do token "racer$i@example.com" >"$TMP/token$i"; done
  for i in $(seq 1 "$users"); do
    curl -s -o /dev/null -w '%{http_code}\n' -X POST "$BASE_URL/api/bookings" \
      -H "Authorization: Bearer $(cat "$TMP/token$i")" -H 'Content-Type: application/json' \
      -H "Idempotency-Key: $(new_key)" --data "$body" >"$TMP/code$i" &
  done
  wait
  local won lost
  won=$(cat "$TMP"/code* | grep -c '^201$' || true)
  lost=$(cat "$TMP"/code* | grep -c '^409$' || true)
  info "responses: $won x 201, $lost x 409"
  [[ $won == 1 && $lost == $((users - 1)) ]] || fail "expected exactly one winner"
  ok "exactly one hold succeeded"
}

declined() {
  local carol seat booking_id
  carol=$(token carol@example.com)
  seat=$(free_seats "$EVENT_ID" 1 B)
  step "Carol holds $seat and pays with tok_declined"
  hold "$carol" "$EVENT_ID" "$(new_key)" "$seat"
  booking_id=$(jq -r .id <<<"$BODY")
  api POST "/api/bookings/$booking_id/checkout" "$carol" '{"paymentToken":"tok_declined"}'
  [[ $(wait_status "$carol" "$booking_id" 30 PaymentFailed Confirmed | jq -r .status) == PaymentFailed ]] || fail "expected PaymentFailed"
  [[ $(seat_status "$EVENT_ID" "$seat") == Available ]] || fail "seat not released"
  ok "PaymentFailed and $seat is Available again"
}

late_payment() {
  local dave seat booking_id
  dave=$(token dave@example.com)
  seat=$(free_seats "$EVENT_ID" 1 B)
  step "Dave holds $seat and pays with tok_slow (answers after the payment deadline)"
  hold "$dave" "$EVENT_ID" "$(new_key)" "$seat"
  booking_id=$(jq -r .id <<<"$BODY")
  api POST "/api/bookings/$booking_id/checkout" "$dave" '{"paymentToken":"tok_slow"}'
  wait_status "$dave" "$booking_id" 40 Expired >/dev/null
  ok "saga timeout fired: Expired"
  wait_status "$dave" "$booking_id" 60 Refunded >/dev/null
  ok "late payment was refunded automatically"
}

setup
case $SCENARIO in
  happy) happy ;;
  race) race ;;
  declined) declined ;;
  late-payment) late_payment ;;
  all) happy; race; declined; late_payment ;;
  *) fail "unknown scenario $SCENARIO" ;;
esac
printf '\n\033[32mDemo %s passed.\033[0m\n' "$SCENARIO"
