# Diagnostics Export

NetSplit exposes a sanitized diagnostics snapshot for P0 validation and support.
It can be exported from the Logs page with `Export diagnostics`, or from an
elevated PowerShell session:

```powershell
.\scripts\p0-control.ps1 -Action diagnostics
```

The command writes a timestamped JSON file under `artifacts\diagnostics` by
default. Use `-OutputDirectory` to choose another directory.

The snapshot contains:

- service readiness and runtime mode;
- whether installation/recovery startup suppression is active;
- the current runtime status;
- adapter names, interface indexes, addresses, gateways, DNS servers and counters;
- subscription and rule counts;
- Mihomo and GeoData availability;
- hashes and metadata for Mihomo, GeoData and runtime state files;
- the presence of `startup.force-disabled`, transaction journal and PID evidence;
- the recent already-sanitized service log buffer.

It does not contain subscription URLs, proxy credentials, controller secrets,
or the full Mihomo configuration.

## Startup Registration

To inspect the Windows Service Control Manager entry and the current user's
tray task, run:

```powershell
.\scripts\startup-status.ps1
```

The report checks the installed service path, delayed automatic start, task
user/action, logon delay, missed-trigger recovery and retry policy. It also
includes a read-only RPC snapshot of Mihomo, TUN, DNS and adapter readiness.

To repair only those registrations without changing the saved split state or
starting/stopping the service, run:

```powershell
.\scripts\repair-startup.ps1
```

Use `-StartService` only as an explicit action. The repair script is designed
to leave the current TUN state untouched by default.
