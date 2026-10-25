# JANOARG — Save Import/Export Plan

Status: **draft** · Last updated: 2026-10-03

The urgent, standalone fix for early-access progress retention. Client-only: no
account, no server, no store. Independent of Cloud Save (§ separate plan) and
ships first.

---

## 1. Problem

Early-access / experimental builds install as separate apps (different package
ID, different signature, sometimes side-by-side with the main game). Android gives
each its own app-data sandbox, so each has its own save and the player is forced
to start over. We need to move progress between builds **without relying on
Android keeping app data intact**, and without any third-party identity.

## 2. Scope

- Encrypt the current save into a portable **blob** and export it; import it on
  another build.
- Fully offline and storage-permission-free where possible.
- No backend, no TapTap, no login.

## 3. Non-goals

- Automatic or cross-device sync (that is Cloud Save).
- Account identity / `unionid`.
- Real anti-cheat (see the shared blob threat model, §4).

## 4. Shared save blob format

Canonical definition — **Cloud Save reuses this exact container.**

```
MAGIC(4) | version(2) | flags(2) | nonce(12) | ciphertext + tag
```

- **AEAD**: AES-GCM (or ChaCha20-Poly1305). Auth tag means a flipped byte fails
  on import instead of deserializing to garbage.
- **AAD**: `MAGIC` + `version` + `flags` + `createdAt`. **Do not bind a
  device-local player id** — a fresh install would then be unable to decrypt the
  file. Account binding, if ever wanted, belongs outside the blob.
- **Key**: derived (HKDF) from obfuscated constants embedded in the client.
  Random nonce per export. Do not hand-roll the cipher.
- **Security posture (deliberate)**: security-by-obscurity. The key ships in the
  client, so a determined attacker can reverse it (Phigros's AES-CBC save was
  opened this way). This stops casual editing; it is tamper-*evident*, not
  tamper-*proof*. Accepted for a single-player game.
- **`SAVE:Version`** (a normal `Storage` key) drives migration: unknown keys are
  ignored; per-version upgrades run on load; a blob newer than the build warns or
  refuses.

The live on-disk save stays the existing plaintext `save.jas` (+ `.backup.jas`)
for backward compatibility; only the *export* is encrypted.

## 5. Export / import channels

Pick by dependency cost; can support more than one.

| Channel | Dependency | Notes |
| --- | --- | --- |
| **Clipboard** | none | MVP. Export → copy base64 blob text; import → paste. Saves are small. |
| **File via share sheet / SAF** | small Android intent plugin (or NativeShare) | Best UX; `ACTION_OPEN_DOCUMENT` for import. |
| **QR** | none (or a QR lib) | For long-distance/manual transfer; a small save fits a few frames. |

Export flow: `Storage.Save()` → serialize → AEAD encrypt → channel.
Import flow: channel → decrypt + verify tag → check `SAVE:Version`/migrate →
**prompt** ("Import will replace your current save — Keep Current / Import") if
local progress exists → **snapshot the current save** (`save.preimport-<utc>.jas`)
→ apply → `Storage.Load()`/`Save()`.

The import prompt is the single-sided cousin of the Cloud Save conflict prompt
(no "cloud side"); reuse the same UI component.

## 6. Client integration

- New key `SAVE:Version` in `Storage`.
- `ISaveTransport` (contracts submodule) with `ClipboardSaveTransport` /
  `FileSaveTransport`; Cloud Save later adds `HttpSaveTransport`.
- Buttons in the Options panel (Export / Import).
- Hook: import must go through `StorageManager.Save()` so `SYNC:Dirty` and other
  listeners (`Storage.OnSave`) stay consistent.

## 7. Phases

0. `SAVE:Version` + blob encrypt/decrypt + `ISaveTransport`.
1. Clipboard export/import (zero-dependency, testable end-to-end).
2. Import prompt + pre-import snapshot.
3. File/share-sheet channel (Android plugin) if the clipboard UX is too rough.

## 8. Risks

- Embedded key = obscurity only (accepted).
- `SAVE:Version` skew between an old build importing a new blob — warn, don't
  silently corrupt.
- Android share-sheet/file access needs a native plugin; clipboard avoids it.
