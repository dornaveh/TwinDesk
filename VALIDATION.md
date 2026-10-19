# Current validation

Updated October 18, 2026. Source was reconciled from the Windows installation's
working tree and the current Mac working tree. Historical snapshots, local
settings, credentials, diagnostics, and new experimental test files are not part
of this source update.

## Checks

- Windows Release build: passed with zero warnings and errors after source sync.
- Microphone selection: nine checks passed for exact endpoint restoration,
  rebuilt endpoints, absent/reappearing devices, legacy names, hardware identity,
  ambiguous inputs, and absence of a default selection.
- Mac main app and Calls helper: Swift type-checks passed on the Mac after sync.
  These checks did not replace or restart the installed apps.
- Mac sign-in implementation: 61 protocol checks and 31 startup, identity, UI,
  and live read-only SSH command checks passed during implementation. Protocol
  checks cover account authentication, raw/copy/resize updates, fragmented updates,
  cancellation, invalid messages, and size limits. They do not replace hardware
  qualification.
- Live sign-in: the user completed Mac login through the Windows sign-in view.
  The Mac console belonged to the expected account, TwinDesk reconnected, shared
  control switched to Mac and back to Windows, and Mac audio reached Windows.

## Remaining limits

The restart qualification required fixing SSH command-channel configuration
before login succeeded. A fresh FileVault unlock through the final build is
still unqualified. Monitor input switching remains intermittent on the tested
dual Samsung setup; clear video, successful compilation, or an acknowledged
command does not prove reliable switching. These checks do not establish
compatibility with other hardware or macOS releases. Native WhatsApp video
remains unsupported in the tested configuration; see the
[Calls helper notes](mac/CallMediaHelper/README.md).
