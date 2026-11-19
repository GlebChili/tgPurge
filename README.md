# tgPurge

Emergency tool to **back up and then erase your Telegram history**. It logs in as you (MTProto user client via
[WTelegramClient](https://github.com/wiz0u/WTelegramClient)), backs up exactly the messages it is about to delete — text as Markdown plus their media —
verifies the backup, and then deletes them **for all participants**: every message older than a cut-off date that you
are allowed to delete. Nothing that stays on Telegram is copied.

> ⚠️ Irreversible. In private chats both your messages and the other person's are deleted for both sides.
> In groups where you have delete rights, all members' messages are deleted for everyone.

## Requirements

- .NET SDK 11+
- Telegram API credentials (`api_id` and `api_hash`); see [Registering with Telegram](#registering-with-telegram) below.

## Registering with Telegram

tgPurge is a Telegram *client* (it speaks MTProto, like the official apps), so Telegram requires it to run under
an application id that **you** register. It is free and takes a minute. Nothing is shared with the tgPurge authors.

1. Make sure you have a Telegram account created with an official Telegram app and an up-to-date phone number on it.
2. Open <https://my.telegram.org> and log in with that phone number. The login code arrives **inside the Telegram
   app** (from the "Telegram" service account), not by SMS.
3. Choose **API development tools** (<https://my.telegram.org/apps>) and fill in the form:

   | field | what to enter |
   |---|---|
   | App title | anything, e.g. `tgPurge personal` — Telegram's terms forbid the word "Telegram" in the title |
   | Short name | 5–32 letters/digits, e.g. `tgpurgeme` |
   | URL | may be left empty |
   | Platform | `Desktop` (or `Other`) |
   | Description | optional, e.g. "personal backup and cleanup tool" |

4. Click **Create application**. The page then shows your **App api_id** (a number) and **App api_hash**
   (a 32-character hex string). These are what tgPurge asks for.

Pass them to tgPurge in one of these ways (most private first):

- let tgPurge **prompt** for them when it starts (nothing ends up in shell history);
- environment variables `TGPURGE_API_ID` and `TGPURGE_API_HASH`;
- `--api-id` / `--api-hash` (visible in shell history and to other local users via the process list).

Good to know:

- **One application per phone number.** If you already created one for another tool you see the existing
  api_id/api_hash instead of the form; you can reuse them.
- **Treat the api_hash like a password.** It identifies your application; don't publish it or commit it to a repository.
- **The api_id is not your login.** You still log in to *your account* inside tgPurge with your phone number, the
  code Telegram sends you, and your two-step verification password if you have one.
- **Accounts using unofficial clients are monitored by Telegram** for abuse such as spam or flooding. Normal
  personal use like this tool's is fine. If your account is ever restricted after using the API without having
  broken the rules, Telegram's official page says to email `recover@telegram.org` explaining what you used the API for.
- If the form keeps answering "ERROR", it is usually a network or browser issue on Telegram's side: try another
  browser, disable ad-blockers, or switch network (VPN on/off) and retry.

References:

- Telegram — Creating your application / obtaining api_id: <https://core.telegram.org/api/obtaining_api_id>
- Telegram — API Terms of Service: <https://core.telegram.org/api/terms>
- Telegram — API development tools (the form): <https://my.telegram.org/apps>
- WTelegramClient (the MTProto library tgPurge uses): <https://wiz0u.github.io/WTelegramClient/> ·
  source <https://github.com/wiz0u/WTelegramClient>

## Usage

```bash
dotnet run --project src/TgPurge -- --cutoff 2025-06-01
```

```bash
dotnet run --project src/TgPurge -- --cutoff now --except @mom --except "Work chat" --dry-run
```

Credentials can be passed with `--api-id/--api-hash/--phone` or `TGPURGE_API_ID`, `TGPURGE_API_HASH`, `TGPURGE_PHONE`.
Run `--help` for all options. Useful ones:

| option | meaning |
|---|---|
| `--cutoff <date\|now>` | delete messages sent strictly before this moment (`yyyy-MM-dd` = local midnight) |
| `--except <chat>` / `--except-file <path>` | chats left completely untouched — neither backed up nor purged (id as shown in the chat table, e.g. `user:123`, a bare or Bot-API id, `@username`, exact title). Every exception must match exactly one chat or the tool refuses to run. Excluding a supergroup also keeps its pre-upgrade history; excluding a channel also keeps your comments under it. |
| `--login qr\|code` | `qr`: scan a QR code in the Telegram app (Settings → Devices → Link Desktop Device); no login code is sent. `code`: phone number + login code. Asked interactively if omitted (QR is the default). |
| `--dry-run` | back up + verify + report what would be deleted; delete nothing |
| `--yes-i-understand-this-is-irreversible` | no interactive prompts (disclaimer still printed) |
| `--keep-session` / `--session <path>` | by default the session is temporary and logged out at exit |

To build a standalone binary: `dotnet publish src/TgPurge -c Release -o out`.

### The login code never arrives

If you are already logged in to Telegram on another device, Telegram does not send the code by SMS: it sends it as
a message from the verified **"Telegram"** service account inside the app (look in Archived Chats too). If it is not
there either, use **QR login** (`--login qr`, the default when asked): tgPurge shows a QR code in the terminal and you
scan it from Telegram → Settings → Devices → Link Desktop Device, so no code has to be delivered. Avoid requesting
codes over and over: Telegram then blocks login attempts for that number for a while (`FLOOD_WAIT`). Never forward
or paste a login code into any chat — Telegram cancels codes that have been shared.

## What it does

1. **Login** by QR code (scanned in the Telegram app on your phone) or by phone number and login code, then the
   2FA password if you have one. The `api_hash` and the password are not echoed while you type. By default the session lives in a private temporary folder and is
   encrypted with a random key that exists only in memory for this run. It is logged out and deleted at exit,
   including on Ctrl+C, SIGTERM, terminal close (SIGHUP) and SIGQUIT; a second Ctrl+C forces an immediate quit.
2. **Discover** every dialog — main list *and* archive — plus Saved Messages, the comments groups linked to
   channels you follow (where your comments live even if you never joined the group), and the old basic group
   behind every supergroup that was upgraded from one (it holds all pre-upgrade history).
3. **Exceptions & disclaimer**: shows every chat with what will happen to it, then a full warning (backup location,
   both-sides deletion, other people's messages, irreversibility). You must type `PURGE`.
4. **Backup** to `./tgBacups/<yyyy-MM-dd_HH-mm-ss>/` — **only what this run deletes**: messages sent before the
   cut-off that you may delete for everyone (in private chats and groups you administer, everyone's; elsewhere, only
   yours), with their media. Other people's messages you cannot delete, newer messages, channels you only follow and
   your exceptions are never copied. The backup and the purge use the same rule (`PurgeRule`), so they cannot disagree.
   ```
   manifest.json                     chats, counts, checksums
   private_123456_Alice/
     history.md                      readable history, oldest first
                                     (chats over 50,000 messages: history-0001.md, history-0002.md, … linked to each other)
     messages.jsonl                  structured history, one JSON message per line (used for verification)
     media/<msgId>_<name>.<ext>      every photo, video, voice note, sticker, document…
   backup-log.txt, mtproto.log, purge-report.md
   ```
   Chats are streamed to disk page by page (100 messages at a time) and media straight to files, so memory use
   stays flat however large a chat is — a test streams 100,000 messages (~100 MB of text) with a few MB of heap.
5. **Verify** (re-read from disk, streamed, per chat): manifest, SHA-256 of `messages.jsonl` and of every history
   part, every message present exactly once in the Markdown, every media file present, non-empty, with the
   recorded size and SHA-256 and, for documents, the size Telegram declared (Telegram declares no size for photos), no failed downloads, and no fewer messages than Telegram reported.
   **Chats that fail verification are not purged.**
6. **Purge** each verified chat:

   | chat | deleted | API |
   |---|---|---|
   | private chat (incl. bots, Saved Messages) | **all** messages, yours and theirs, **both sides** | `messages.deleteMessages(revoke=true)` |
   | basic group, you are creator/admin | all members' messages, for everyone | `messages.deleteMessages(revoke=true)` |
   | basic group, no rights | only your messages, for everyone | `messages.deleteMessages(revoke=true)` |
   | supergroup/channel with delete rights | all messages, for everyone | `channels.deleteMessages` |
   | supergroup/channel without delete rights, comments groups | only your messages, for everyone | `channels.deleteMessages` |
   | channel you only follow | nothing (contains nothing of yours) | — |

   What to delete is planned from the verified backup, which came from a **full read** of the chat (every message,
   your own recognised by Telegram's "outgoing" flag, which also covers posts made as an anonymous admin or as your
   channel). Only message ids in the verified backup are ever deleted. Deletion is retried up to 3 passes, and every
   planned message is then looked up by id to confirm it is gone. Finally a cheaper **checking read** looks for
   anything in scope still there, including messages the backup read might have missed (reported, never deleted):
   - it reads from the cut-off backwards, so newer messages are not fetched;
   - in groups where you may delete only your own messages, it searches for messages sent by you, by the channels you
     own (members can post "as" a channel) and, where you are an anonymous admin, by the group itself.
7. **Report** printed and saved as `purge-report.md`; exit code `3` if anything in scope remains **or any chat
   could not be read or inspected** (such chats are listed as CRITICAL, never counted as done). A clean run ends with
   "no in-scope message … left in your account" — the tool can see only your side, so it never claims more.

### Never "delete for me"

Deleting only on your side is treated as a critical error, so the tool is built to make it impossible:

- the only deletion calls are `messages.deleteMessages` with `revoke: true` and `channels.deleteMessages`
  (always for everyone), both in one method in `Telegram/TelegramApi.cs`;
- `messages.deleteHistory`, `just_clear` and friends are never used — a unit test scans the source to enforce this;
- if Telegram's server config ever sets a finite "delete for everyone" window, messages older than it are **not**
  deleted (that would only delete them for you) and are reported as CRITICAL. `revoke_pm_time_limit` applies to
  private chats; basic groups (undocumented) get the stricter of it and `revoke_time_limit`. Supergroups and
  channels need no guard: their messages exist once for everyone, so Telegram deletes for all or returns an error.
- the tool can only see your side: it checks the messages are gone from your account, but Telegram offers no way
  to confirm the other side's copy. Test on two throwaway accounts if you need that proof.

### Progress output

Everything the tool does is reported on stdout, each line stamped with the clock time and elapsed run time
(`[14:02:31 +1h12m]`):

- a banner per phase, a line per chat, and live counters: messages read, media `7/40 — 640 MB / 1 GB (62%)`,
  files verified, messages deleted;
- **heartbeat**: after 15 s without output it prints `… still working: <what it is doing> — <latest counter>`;
- **Telegram rate limits** longer than 10 s are announced with how long and until when, counted down every minute,
  and followed by `Resuming.`; they never count as a failed attempt;
- network retries, failed media downloads, and connection drops/reconnects are shown as they happen;
- on a terminal counters update in place; when stdout goes to a file they are written as separate lines at most every
  10 s, so `tgPurge … > run.log` gives a readable log;
- the heartbeat pauses while you are typing an answer.

### Local data protection

- On Linux/macOS the backup folder is created `0700` and every file in it `0600`; the temporary session folder too.
  On Windows they inherit your user profile's permissions.
- Every backup file is flushed to disk as it is written, and the whole file system is synced before deleting starts,
  so a power cut or forced shutdown right after the purge cannot lose a backup that had verified.
- Message text and names are HTML-escaped in `history.md`, so they cannot inject markup into a Markdown viewer or
  fake another message's entry (the verifier requires each message's anchor exactly once, on its own line).
- Chats are identified by peer type and id (`user:…`, `chat:…`, `channel:…`): Telegram reuses numbers across those
  types, so a bare id can be ambiguous. The chat table shows this form and exceptions accept it.

### Disk full or unwritable backup folder

- Free space is shown before the backup; under 2 GB you are warned and asked to confirm.
- Before each page of 100 messages is saved, that page's media size is estimated, and every file is checked against
  free space (keeping a 256 MB reserve) before download, so the tool stops before the disk is actually full.
- Any write failure (no space, quota, permissions) stops the backup at once with a clear message and exit code `4`.
  **Nothing is deleted**, because deletion only starts after the complete backup has been verified.
- Files are written to a temporary name and renamed, so a failed write never leaves a truncated `history.md` or media file.
- The final report is printed before it is saved; if saving fails, the on-screen copy is all you get, but the run still completes.
- The MTProto debug log silently stops on a write error, so a full disk can't break the Telegram connection mid-purge.

- File and folder names are capped at 200 bytes (UTF-8, measured in decomposed form), cut only between whole
  characters, so long CJK/emoji/Hangul titles and sender-supplied file names fit the 255-byte limit on Linux and macOS.
- Writes, renames and verification reads retry for about 6 seconds on Windows sharing/lock violations (antivirus,
  search indexer) before treating the error as real.

Exit codes: `0` complete · `1` aborted · `2` bad usage · `3` purge incomplete · `4` backup storage failure · `5` unexpected error.

## Limits

The saved report (`purge-report.md` / `dry-run-report.md` in the backup folder) names the chats that were not purged
— your exceptions, channels you only follow, and chats that could not be accessed — so that a run's coverage can be
checked. Delete or protect it accordingly.


Not done by this tool (do these in the Telegram app if they matter):
- other logged-in sessions (Settings → Devices) — best ended *before* running tgPurge;
- stories, profile photos, bio and username, scheduled messages, drafts, synced contacts;
- groups/channels you already left.

Not possible for any tool:
- in supergroups and channels, Telegram's "Recent actions" admin log shows messages deleted in the last 48 hours
  to the group's admins, so they can still see deleted messages for up to two days;
- secret chats live only on devices — delete them in the app on each device;
- messages you earlier deleted "only for me" (the other side keeps them), and copies already exported, forwarded,
  screenshotted or cached on offline devices;
- bots: Telegram keeps no bot-side history, but the bot's owner may have logged what you sent.

Also:
- Each chat is read in full once (for the backup) and once more by the cheaper checking read after the purge. The
  checking read locates the cut-off by date and Telegram's reported position; histories imported from other apps
  (WhatsApp, etc.) can have old dates on new ids, so the checking read may not see such messages, but the full backup
  read does, so they are backed up and deleted like any other. The position values are written to `mtproto.log`.
- In groups where only your messages can be deleted, the purge report shows other people's messages as "not read":
  the checking read does not download them, so they are not counted.
- Flood limits: Telegram rate-limits history reads and deletes; the tool waits as long as Telegram requires.
  Large accounts can take hours.

## Tests

```bash
dotnet test
```

Covers option parsing, exception matching, the purge planner, backup verification, storage and portability,
session handling, status output, and the "no delete-for-me" source guard. History paging, chat discovery and the
purge itself are tested against an in-memory fake Telegram (`tests/TgPurge.Tests/FakeTelegram.cs`) that answers
like the real API (sliced pages, channel pages, deleted messages as empty). The tool has **not** yet been run
against a live account; before relying on it, test on two throwaway accounts.

## Security

Please report security problems privately to **security@krasilich.net**, not in public issues.
See [SECURITY.md](SECURITY.md).

## License

[MIT](LICENSE) © 2026 Gleb Krasilich
