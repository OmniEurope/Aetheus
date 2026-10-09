#!/bin/sh
# SPDX-License-Identifier: EUPL-1.2
# Runs INSIDE the disposable container started by tests/mail-stack/run.sh, as root, against the real
# helpers deposited in /usr/local/lib/aetheus. Every check prints PASS or FAIL with its evidence; the
# script exits non-zero when any check failed.
set -u

SETUP=/usr/local/lib/aetheus/mail-setup
MANAGE=/usr/local/lib/aetheus/mail-manage
failures=0
passes=0

pass() { passes=$((passes + 1)); echo "PASS: $1"; }
fail() { failures=$((failures + 1)); echo "FAIL: $1"; }
expect_ok() { label="$1"; shift; if out="$("$@" 2>&1)"; then pass "$label"; else fail "$label -> $(printf '%s' "$out" | tail -n 3 | tr '\n' ' ')"; fi; }
expect_refused() { label="$1"; shift; if out="$("$@" 2>&1)"; then fail "$label (accepted: $out)"; else pass "$label ($(printf '%s' "$out" | tail -n 1))"; fi; }
delivered_to() {
  # $1 = maildir, $2 = token: PASS when a delivered message carries the token.
  grep -rlsF "$2" "$1" 2>/dev/null | head -n 1
}

echo "== mail-setup (with rspamd)"
if out="$(printf 'Sup3rSecret!\n' | "$SETUP" mail.example.test example.test default admin@example.test 1024 rspamd 2>&1)"; then
  pass "mail-setup completed"
else
  fail "mail-setup -> $(printf '%s' "$out" | tail -n 5 | tr '\n' ' ')"
  echo "RESULT: $passes passed, $failures failed"
  exit 1
fi
for unit in postfix dovecot opendkim rspamd redis-server; do
  if [ "$(systemctl is-active "$unit")" = active ]; then pass "$unit active"; else fail "$unit not active"; fi
done
for port in 25 587 993 8891 11332; do
  if ss -ltnH "sport = :$port" | grep -q .; then pass "port $port listening"; else fail "port $port not listening"; fi
done
[ -S /var/spool/postfix/private/dovecot-lmtp ] && pass "dovecot LMTP socket in postfix chroot" || fail "dovecot LMTP socket missing"
[ -S /var/spool/postfix/private/auth ] && pass "dovecot auth socket in postfix chroot" || fail "dovecot auth socket missing"
[ -f /etc/aetheus/mail-stack.managed ] && pass "ownership marker written" || fail "ownership marker missing"
expect_ok "helper version reported" sh -c "\"$MANAGE\" version | grep -qx 'aetheus-mail-helper-version: 3'"
# The agent collector runs unprivileged: every inventory source must be readable without sudo, and the
# DKIM private key must stay out of reach.
for path in /etc/postfix/vmailbox /etc/opendkim/KeyTable /etc/aetheus/mail-stack.managed \
    /etc/opendkim/keys/example.test/default.txt /etc/rspamd/local.d/actions.conf "$MANAGE"; do
  if su -s /bin/sh nobody -c "cat '$path'" >/dev/null 2>&1; then pass "unprivileged read of $path"; else fail "unprivileged read of $path"; fi
done
if su -s /bin/sh nobody -c 'cat /etc/opendkim/keys/example.test/default.private' >/dev/null 2>&1; then
  fail "DKIM private key readable by an unprivileged user"
else
  pass "DKIM private key unreadable by an unprivileged user"
fi
if su -s /bin/sh nobody -c 'cat /etc/dovecot/users' >/dev/null 2>&1; then fail "password hashes readable by an unprivileged user"; else pass "password hashes unreadable by an unprivileged user"; fi

echo "== incremental management"
expect_ok "add-domain with DKIM key" "$MANAGE" add-domain second.test sel2
[ -f /etc/opendkim/keys/second.test/sel2.private ] && pass "per-domain DKIM key generated" || fail "per-domain DKIM key missing"
expect_ok "add-account" sh -c "printf 'B0bPassw0rd!\n' | \"$MANAGE\" add-account bob@second.test second.test 512"
expect_ok "add-alias" "$MANAGE" add-alias info@second.test bob@second.test
expect_ok "dovecot authenticates the new account" sh -c "doveadm auth test bob@second.test 'B0bPassw0rd!' | grep -q 'auth succeeded'"
expect_ok "dkim-read per-domain key" sh -c "\"$MANAGE\" dkim-read second.test sel2 | grep -q 'v=DKIM1'"

echo "== delivery"
out="$("$MANAGE" send-test admin@example.test bob@second.test 2>&1)"; rc=$?
token="$(printf '%s' "$out" | awk -F'\t' '/^DELIVERY/ { print $4 }')"
if [ "$rc" -eq 0 ] && printf '%s' "$out" | grep -q "^DELIVERY	sent	"; then pass "send-test reports status=sent"; else fail "send-test -> $out"; fi
f="$(delivered_to /var/mail/vhosts/second.test/bob "$token")"
if [ -n "$f" ]; then pass "test message stored in bob's Maildir"; else fail "test message not found in bob's Maildir"; fi
if [ -n "$f" ] && grep -q '^DKIM-Signature: .*d=example.test' "$f"; then pass "outgoing message DKIM-signed for example.test"; else fail "DKIM-Signature missing"; fi
out="$("$MANAGE" send-test admin@example.test info@second.test 2>&1)"
token="$(printf '%s' "$out" | awk -F'\t' '/^DELIVERY/ { print $4 }')"
if [ -n "$(delivered_to /var/mail/vhosts/second.test/bob "$token")" ]; then pass "alias info@ delivered to bob"; else fail "alias delivery -> $out"; fi
if swaks --server 127.0.0.1 --port 587 --tls --auth PLAIN --auth-user bob@second.test --auth-password 'B0bPassw0rd!' \
    --from bob@second.test --to admin@example.test --header 'Subject: submission' 2>&1 | grep -q '^<~  250 2.0.0'; then
  pass "authenticated submission on 587 (STARTTLS)"
else
  fail "authenticated submission on 587"
fi
if swaks --server 127.0.0.1 --port 587 --tls --auth PLAIN --auth-user bob@second.test --auth-password 'wrong-password' \
    --from bob@second.test --to admin@example.test 2>&1 | grep -q '^<~\* 535'; then
  pass "wrong password refused on 587"
else
  fail "wrong password not refused on 587"
fi

echo "== antispam"
if swaks --server 127.0.0.1 --port 25 --from outsider@remote.test --to admin@example.test --header 'Subject: gtube' \
    --body 'XJS*C4JDBQADN1.NSBN3*2IDNEN*GTUBE-STANDARD-ANTI-UBE-TEST-EMAIL*C.34X' 2>&1 | grep -q '^<\*\* 554 5.7.1'; then
  pass "GTUBE message rejected by rspamd"
else
  fail "GTUBE message not rejected"
fi
expect_ok "spam-configure valid thresholds" "$MANAGE" spam-configure 20 8 5
expect_ok "actions.conf holds the new reject score" grep -q '^reject = 20;' /etc/rspamd/local.d/actions.conf
expect_refused "spam-configure refuses inverted thresholds" "$MANAGE" spam-configure 5 8 20
expect_refused "spam-configure refuses non-numeric score" "$MANAGE" spam-configure '1;id' 8 5
expect_ok "spam-install is idempotent" "$MANAGE" spam-install
milters="$(postconf -h smtpd_milters)"
if [ "$(printf '%s' "$milters" | grep -o 11332 | wc -l)" -eq 1 ]; then pass "rspamd milter chained once ($milters)"; else fail "rspamd milter chain: $milters"; fi
expect_ok "spam-learn spam from stdin" sh -c "printf 'Subject: spam sample\n\nbuy now buy now\n' | \"$MANAGE\" spam-learn spam"
expect_ok "service restart rspamd" sh -c "\"$MANAGE\" service rspamd restart | grep -q '^SERVICE	rspamd	restart	active'"

echo "== diagnostics"
out="$("$MANAGE" check 2>&1)"; rc=$?
if [ "$rc" -eq 0 ] && ! printf '%s' "$out" | grep -q '	fail	'; then pass "check: every component ok"; else fail "check -> $out"; fi
expect_ok "queue-list runs" "$MANAGE" queue-list
out="$("$MANAGE" quota-report 2>&1)"
if printf '%s' "$out" | grep -q '^QUOTA	bob@second.test	[0-9]'; then pass "quota-report lists bob"; else fail "quota-report -> $out"; fi
echo "== queue"
# Stop Dovecot so LMTP refuses: the message stays deferred in the Postfix queue.
systemctl stop dovecot
printf 'Subject: queued\n\nqueued\n' | /usr/sbin/sendmail -f admin@example.test bob@second.test
sleep 4
out="$("$MANAGE" queue-list 2>&1)"
qid="$(printf '%s' "$out" | sed -n 's/.*"queue_id": *"\([0-9A-Za-z]*\)".*/\1/p' | head -n 1)"
if [ -n "$qid" ]; then pass "queue-list reports the deferred message ($qid)"; else fail "queue-list -> $out"; fi
echo "EVIDENCE queue-list: $(printf '%s' "$out" | head -n 1)"
expect_ok "queue-delete removes it" "$MANAGE" queue-delete "$qid"
if "$MANAGE" queue-list | grep -q "\"$qid\""; then fail "message $qid still queued"; else pass "queue no longer lists $qid"; fi
systemctl start dovecot
expect_ok "logs postfix" sh -c "\"$MANAGE\" logs postfix 20 | grep -q postfix"
expect_ok "logs filtered by token" sh -c "\"$MANAGE\" logs postfix 50 \"$token\" | grep -q \"$token\""

echo "== TLS certificate"
live=/etc/letsencrypt/live/mail.example.test
mkdir -p "$live"
openssl req -x509 -newkey rsa:2048 -nodes -days 30 -subj '/CN=mail.example.test' \
  -keyout "$live/privkey.pem" -out "$live/fullchain.pem" >/dev/null 2>&1
expect_ok "install-cert" "$MANAGE" install-cert mail.example.test
if [ "$(postconf -h smtpd_tls_cert_file)" = "$live/fullchain.pem" ]; then pass "postfix uses the lineage certificate"; else fail "postfix cert: $(postconf -h smtpd_tls_cert_file)"; fi
if doveconf -n | grep -q "$live/fullchain.pem"; then pass "dovecot uses the lineage certificate"; else fail "dovecot certificate not updated"; fi
[ -x /etc/letsencrypt/renewal-hooks/deploy/aetheus-mail ] && pass "renewal deploy hook installed" || fail "renewal hook missing"
if echo | openssl s_client -connect 127.0.0.1:993 2>/dev/null | grep -q 'CN *= *mail.example.test'; then pass "IMAPS serves the new certificate"; else fail "IMAPS certificate not served"; fi
expect_refused "install-cert refuses a missing lineage without email" "$MANAGE" install-cert absent.example.test
expect_refused "install-cert refuses an invalid email" "$MANAGE" install-cert absent.example.test 'x;id'

echo "== rotation, password, removal"
expect_ok "dkim-rotate example.test to sel3" "$MANAGE" dkim-rotate example.test sel3
out="$("$MANAGE" send-test admin@example.test bob@second.test 2>&1)"
token="$(printf '%s' "$out" | awk -F'\t' '/^DELIVERY/ { print $4 }')"
f="$(delivered_to /var/mail/vhosts/second.test/bob "$token")"
if [ -n "$f" ] && grep -q '^DKIM-Signature: .*s=sel3' "$f"; then pass "signature uses the rotated selector"; else fail "rotated selector not used"; fi
expect_ok "change-password" sh -c "printf 'N3wPassw0rd!\n' | \"$MANAGE\" change-password bob@second.test"
# Dovecot re-stats the passwd-file at most once per second; the previous delivery may have just
# synced it, so give the new hash one second to become visible.
sleep 2
expect_ok "new password authenticates" sh -c "doveadm auth test bob@second.test 'N3wPassw0rd!' | grep -q 'auth succeeded'"
expect_ok "remove-alias" "$MANAGE" remove-alias info@second.test
expect_ok "delete-account" "$MANAGE" delete-account bob@second.test second.test
expect_ok "remove-domain" "$MANAGE" remove-domain second.test
if postconf -h virtual_mailbox_domains | grep -q second.test; then fail "second.test still in virtual_mailbox_domains"; else pass "second.test removed from virtual_mailbox_domains"; fi
if grep -q second.test /etc/opendkim/KeyTable; then fail "second.test still signed"; else pass "second.test removed from KeyTable"; fi
if [ "$(stat -c '%U:%G %a' /etc/dovecot/users)" = "root:dovecot 640" ]; then pass "users file stays root:dovecot 640"; else fail "users file mode $(stat -c '%U:%G %a' /etc/dovecot/users)"; fi

echo "== input validation"
expect_refused "add-domain refuses shell metacharacters" "$MANAGE" add-domain 'evil.test;id'
expect_refused "logs refuses an unknown unit" "$MANAGE" logs sshd 10
expect_refused "logs refuses a command substitution filter" "$MANAGE" logs postfix 10 '$(id)'
expect_refused "service refuses an unknown unit" "$MANAGE" service ssh restart
expect_refused "queue-delete refuses a path" "$MANAGE" queue-delete ../../etc
expect_refused "send-test refuses an invalid recipient" "$MANAGE" send-test admin@example.test 'x;id'
expect_refused "unknown sub-command" "$MANAGE" rm-rf

echo "== purge (Réinstaller la messagerie) and a fresh setup"
# Tasks 23257 and 23258 (2026-10-03): the host had postfix masked. purge must leave nothing masked.
systemctl mask postfix.service >/dev/null 2>&1
[ "$(cat /etc/mailname 2>/dev/null)" = mail.example.test ] && pass "mail-setup wrote /etc/mailname" || fail "/etc/mailname not written by mail-setup"
expect_ok "purge" "$MANAGE" purge
for unit in postfix dovecot opendkim rspamd redis-server; do
  if systemctl list-unit-files "$unit.service" --no-legend 2>/dev/null | grep -q .; then fail "$unit unit still present ($(systemctl is-enabled "$unit.service" 2>&1))"; else pass "$unit unit gone"; fi
done
for pkg in postfix dovecot-core dovecot-imapd dovecot-pop3d dovecot-lmtpd opendkim opendkim-tools rspamd redis-server; do
  case "$(dpkg-query -W -f='${db:Status-Abbrev}' "$pkg" 2>/dev/null)" in ""|?n*) pass "$pkg purged" ;; *) fail "$pkg still known to dpkg: $(dpkg-query -W -f='${db:Status-Abbrev}' "$pkg")" ;; esac
done
for path in /etc/postfix /etc/dovecot /etc/opendkim /etc/opendkim.conf /etc/rspamd /var/mail/vhosts /etc/mailname \
  /etc/aetheus/mail-stack.managed /etc/aetheus/mail-spam.packages /etc/letsencrypt/renewal-hooks/deploy/aetheus-mail; do
  [ -e "$path" ] && fail "$path still present" || pass "$path removed"
done
id vmail >/dev/null 2>&1 && fail "vmail user still present" || pass "vmail user removed"
getent group vmail >/dev/null 2>&1 && fail "vmail group still present" || pass "vmail group removed"
if debconf-show postfix 2>/dev/null | grep -q main_mailer_type; then fail "postfix debconf answers still preseeded"; else pass "postfix debconf answers purged"; fi
[ -d /etc/letsencrypt/live/mail.example.test ] && pass "certificate lineage kept (certbot owns it)" || fail "certificate lineage removed"
expect_ok "purge again (nothing installed)" "$MANAGE" purge
if out="$(printf 'Sup3rSecret!\n' | "$SETUP" mail.example.test example.test default admin@example.test 1024 none 2>&1)"; then
  pass "mail-setup after purge"
else
  fail "mail-setup after purge -> $(printf '%s' "$out" | tail -n 5 | tr '\n' ' ')"
fi
for unit in postfix dovecot opendkim; do
  if [ "$(systemctl is-active "$unit")" = active ]; then pass "$unit active after the fresh setup"; else fail "$unit not active after the fresh setup"; fi
done
grep -qx 'hostname=mail.example.test' /etc/aetheus/mail-stack.managed && pass "marker records the mail host name" || fail "marker has no hostname line"

echo "RESULT: $passes passed, $failures failed"
[ "$failures" -eq 0 ]
