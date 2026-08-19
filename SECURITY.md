# Security and privacy

`net-split` manages privileged network state and may process private proxy
configuration. Keep the repository free of live runtime data.

Never commit:

- subscription URLs or downloaded provider YAML;
- proxy hostnames, usernames, passwords, tokens, or DPAPI-protected blobs;
- generated Mihomo configuration or controller secrets;
- adapter GUIDs, MAC addresses, local/public IP addresses, or DNS leak results;
- packet captures, diagnostic exports, logs, crash dumps, or installer output;
- local editor, agent, or automation state.

The repository `.gitignore` excludes the known runtime and diagnostic paths.
Before sharing a change, review the staged file list and scan staged text for
credentials, private endpoints, user-profile paths, and machine identifiers.

If sensitive data is committed, do not rely on a follow-up deletion. Rotate the
affected credential, remove the data from Git history, and force-push only after
coordinating with every repository user.

Security-sensitive defaults:

- the Windows service owns privileged operations;
- the tray process communicates through an ACL-restricted named pipe;
- Mihomo's controller listens only on loopback and requires a random secret;
- subscription and residential proxy credentials are protected with Windows
  DPAPI;
- proxy failure blocks foreign traffic rather than silently bypassing the
  proxy.
