# Flags and bans: test server checklist

Flagging accounts and addresses that limits refuse again and again, and the
operator's bans (see [Spotting abuse, and banning](../design.md#spotting-abuse-and-banning)
and [Flags and bans](../server.md#flags-and-bans)). Use a test server, never
the public one: these steps flag and ban characters. You need shell access to
the server as its user, and two characters, **A** (yours, on this build of
the plugin) and **B** (on another PC or client, on another internet
connection), in a channel together, here `sky` (`/lgc1`).

On the server, define a shorthand that runs the commands as the service, with
the service's own settings (adjust the paths to the install):

```sh
LG() {
    local settings
    mapfile -t settings < <(systemctl show lookingglass -p Environment --value | tr ' ' '\n' |
        grep -E '^LookingGlass__(DataDirectory|Abuse__|TrustedProxies__)')
    sudo -u lookingglass env "${settings[@]}" /opt/lookingglass/LookingGlass.Server "$@"
}
```

For quicker flags, and to see each connection's address, add to the server's
settings (`sudo systemctl edit lookingglass`), and restart:

```ini
[Service]
Environment=LookingGlass__Abuse__FlagAfterMinutesRefused=2
Environment=LookingGlass__Limits__LookupBurst=2
Environment=LookingGlass__Limits__LookupIntervalSeconds=600
Environment=Logging__LogLevel__LookingGlass=Debug
```

Take them out again at the end.

## Starting

- [ ] 1. **The server sees real addresses.** Connect A, then
      `journalctl -u lookingglass --since "5 min ago" | grep "Connection from"`.
      The address is A's public address (as a "what is my IP" site shows it; an
      IPv6 one as its /64), not 127.0.0.1, ::1, the server's own address, or a
      100.x Tailscale address. If it isn't, the proxy isn't passing the client's
      address on (see [Behind a reverse proxy](../server.md#behind-a-reverse-proxy)):
      fix that first, and ban no address until it is.
- [ ] 2. After the restart, the startup line (`journalctl -u lookingglass -n 20`)
      says `flagged when refused by limits in 2 of 60 minutes, or by 4 limits
      within 10 minutes; addresses blocked automatically for 15 minutes after
      1000 refusals`.
- [ ] 3. `LG --bans` prints `No bans in force.`, `Nothing flagged in the last
      24 hours.` and `No bans lifted or ended in the last 90 days.`, and exits
      with 0 (`echo $?`). The server keeps running.
- [ ] 4. `LG --ban` alone prints what it needs and exits with 2;
      `LG --ban 10.0.0.0/8` says a /16 at the widest and exits with 1.
- [ ] 5. `LG --ban 127.0.0.1` and `LG --ban ::1` say it is the server's own or
      its proxy's address, which would shut out every player, ban nothing, and
      exit with 1 (they mention `--force`; don't use it).

## Flags

- [ ] 6. As A, invite three different players by name in a row (they needn't
      be registered): the third is refused ("You've looked up a lot of players
      recently"). Wait for the next minute, and do it again.
- [ ] 7. The journal has one Warning `Flagged user <A's ID>: refused by limits
      in 2 of the last 60 minutes, by LookupBurst. ...`, with no character
      names in it. Doing it again in a third minute adds no second line.
- [ ] 8. `LG --bans` lists `user <A's ID> (A's name@world)` under
      `Flagged in the last 24 hours`, with `limits: LookupBurst`, and A's
      public address (the one from step 1; an IPv6 one as its /64 and /56)
      flagged too. Never 127.0.0.1, ::1, the server's own address or a 100.x
      address: if one of those shows, stop, and fix the proxy (step 1).

## Banning an account

- [ ] 9. `LG --ban "A's name@A's world" --days 1 --reason "Testing bans"`:
      it says `Banned user <ID> (...) until <tomorrow> UTC (1 day)` and that a
      running server applies it within 30 seconds.
- [ ] 10. Within 30 seconds A is disconnected. A's chat shows a warning, once:
      the server's operator has blocked this character until (tomorrow, in
      local time), with the reason "Testing bans", and that "Try again now"
      tries at once. In simple mode it says "The people who run this
      LookingGlass server have blocked this character".
- [ ] 11. A's main window (`/lg`) shows **Blocked by the server** in the
      header, and in the middle **Blocked by this server** with the same words
      and a **Try again now** button. Typing `/lgc1 hello` says LookingGlass
      isn't connected.
- [ ] 12. Leave it for 2 minutes: A doesn't reconnect (the journal shows no new
      `Connection from` A's address), and the warning isn't repeated.
- [ ] 13. B sees A offline in `sky`, and A is still listed as a member. B's
      messages to `sky` go through; nothing tells B that A was banned.
- [ ] 14. Press **Try again now**: A connects, is refused again at once (the
      journal: `User <ID> is banned: refused`), and is back in the blocked
      state with no second warning.
- [ ] 15. Turn A's plugin off and on (or restart the game): still blocked, and
      the warning is shown once again (a new session).

## Lifting it

- [ ] 16. `LG --bans` lists the ban under `Bans in force (1)` with its reason.
- [ ] 17. `LG --unban "A's name@A's world"`: `Lifted the ban on ...`.
- [ ] 18. On A, press **Try again now** (or wait up to 5 minutes): A connects,
      shows **Connected**, sees `sky`, and B sees A online. A and B chat both
      ways in `sky`.
- [ ] 19. `LG --bans` shows it under `Lifted or ended in the last 90 days`.

## Registering again

- [ ] 20. `LG --ban <A's user ID>` (the number from `--bans`). On A (on this
      test server only), choose **Forget account** in `/lgdebug`, then register
      A's character again through the Lodestone: after **Verify** it is refused
      with the block, and A shows **Blocked by the server**.
- [ ] 21. `LG --unban <A's user ID>`, press **Try again now**, and register
      again: it works, and A has its channels as before.

## Banning an address

- [ ] 22. Find B's public address the way step 1 found A's (the `Connection
      from` line when B connects): not 127.0.0.1, ::1, the server's own address
      or a 100.x Tailscale address, and not A's. Then
      `LG --ban <B's public address> --reason "Testing address bans"` (an IPv6
      address bans its /64). Within 30 seconds B is disconnected and told the
      server's operator blocked their internet address, which others may share,
      with the reason.
- [ ] 23. A (on another address) is unaffected.
- [ ] 24. `LG --unban <the address, exactly as --bans lists it>`; B connects
      again within 5 minutes, or at once with **Try again now**.

## Automatic blocks (on by default; this lowers the threshold to test it)

- [ ] 25. Add `LookingGlass__Abuse__AutoBlockMinutes=5`,
      `LookingGlass__Abuse__AutoBlockAfterRefusals=100` and
      `LookingGlass__Limits__ConnectionsPerMinutePerIp=5`, and restart. From a
      spare machine (not A's or B's connection), open 120 connections in a
      minute:

      ```sh
      for i in $(seq 120); do curl -s --http1.1 --max-time 2 -o /dev/null -w "%{http_code}\n" \
          -H "Connection: Upgrade" -H "Upgrade: websocket" -H "Sec-WebSocket-Version: 13" \
          -H "Sec-WebSocket-Key: dGhlIHNhbXBsZSBub25jZQ==" https://<server>/ws; done | sort | uniq -c
      ```

      The journal says `Blocked address <the spare machine's address> for 5
      minutes automatically`, and `LG --bans` lists it, made automatically.
      Every connection from there is now answered at once, with no
      `Connection from` line. (The 429 itself proves nothing: the connection
      limit answers 429 too, and looks the same. What shows the block is that
      there are no `Connection from` lines for that address, and the
      `LG --bans` listing.) After 5 minutes it is gone from the bans in
      force. Put the settings back.

## Afterwards

- [ ] 26. Remove the test settings (the drop-in from the start, and step 25's)
      and restart. `LG --bans` shows no bans in force. Flags clear by
      themselves after 24 hours.
