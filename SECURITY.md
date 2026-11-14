# Security Policy

tgPurge handles Telegram sessions, irreversibly deletes messages for all chat participants, and writes unencrypted
backups of private conversations. Security problems in it can directly harm the people who rely on it, so please
report them privately.

## Reporting a vulnerability

**Do not open a public GitHub issue, discussion or pull request for a security problem.**

Send the details to **security@krasilich.net**. Please include:

- a description of the problem and its impact (for example: messages deleted only "for me", messages missed by the
  purge, data written outside the backup scope, session or credential exposure, unsafe file permissions);
- steps to reproduce, or a proof of concept;
- the commit or version you tested, your operating system and .NET version;
- whether the problem is already known publicly or being exploited.

Please do not include real people's private messages, session files, `api_hash` values or other credentials in a
report. Use throwaway accounts and redact anything sensitive.

Please give a reasonable amount of time to investigate and release a fix before disclosing the issue publicly.

## Supported versions

Security fixes are made on the `main` branch. Use the latest commit.

## Scope

In scope: the tgPurge source code in this repository and the files it writes (backups, reports, logs, sessions).

Out of scope: vulnerabilities in Telegram itself, in the Telegram apps, or in third-party libraries such as
[WTelegramClient](https://github.com/wiz0u/WTelegramClient) — report those to their maintainers (for Telegram, see its bug bounty program:
<https://core.telegram.org/bug-bounty>).
