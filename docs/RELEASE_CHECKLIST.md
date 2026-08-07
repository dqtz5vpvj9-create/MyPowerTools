# MyPowerTools OTA release checklist

The OTA pipeline is release-ready once these steps complete. Local `main`
already contains the full implementation and the signing secret is configured
in GitHub Actions.

## Status

- **v0.3.0 released on 2026-08-08 via CI** with the full ZIP, Setup exe,
  file manifest, signed stable feed, package feeds, and source bundle.
- The dorm machine was upgraded to the released 0.3.0 package and reports
  `mpt ota check` → `up-to-date` from the GitHub feed.
- Subsequent tags publish automatically; `ota-history/` holds the 0.3.0
  manifest so the next release builds a delta.

## Prerequisites

- GitHub secret `MPT_OTA_SIGNING_KEY_BASE64` exists (checked 2026-08-07).
- Local `main` is clean and contains all OTA commits.
- The last local build passed `scripts/verify-release-artifacts.ps1`.

## 1. Publish main

```powershell
git push origin main
```

The `windows` CI job runs build/test/validate on the pushed commit. It does not
create a release on a branch push.

## 2. Tag and push v0.3.0

```powershell
git tag v0.3.0
git push origin v0.3.0
```

The tag push triggers the `release` job after the `windows` job passes. The
release job resolves version 0.3.0 and channel `stable`, downloads the previous
release manifest if one exists (the v0.2.0 release has none, so this is skipped
on the first publish), runs `publish-windows.ps1` with the signing secret and
`publish-ota-package-feeds.ps1`, creates Release `v0.3.0` with all OTA assets,
and commits the new manifest into `ota-history/` on `main`.

## 3. Verify the release

```powershell
gh release view v0.3.0 --repo dqtz5vpvj9-create/MyPowerTools --json assets --jq '.assets[].name'
```

Expected assets include the full ZIP, its SHA-256 marker, the release file
manifest, the Setup executable, the source bundle, `channel-stable.json` plus
its signature, both public-key files, `release-metadata.json`,
`RELEASE_NOTES.md`, and per-package feeds under `packages/`.

## 4. Verify the feed from the client side

On the dorm machine:

```powershell
mpt ota status
mpt ota check
```

`ota check` fetches the stable channel feed from the GitHub latest release,
verifies the Ed25519 signature against the embedded public key, and reports
`available: false` with reason `up-to-date` because dorm is already on 0.3.0.

## 5. Exercise an end-to-end update (optional, next release)

1. Bump `version.json` to `0.3.1`.
2. Push `main`, tag `v0.3.1`, push the tag.
3. The release job downloads the 0.3.0 manifest into `ota-history/` and builds
   `MyPowerTools-0.3.0-to-0.3.1.ota.zip`.
4. On dorm run `mpt ota apply`; it selects the delta by installed manifest
   hash, verifies the feed signature and package hash, applies the transaction,
   health-checks, and persists the new manifest.

## Rollback

- A failed release job leaves the previous Release untouched; fix and push a
  new tag.
- A failed client apply rolls back from the transaction journal; if health
  fails after a delta, the updater falls back to the full ZIP.
- Dorm can always be restored with the Inno Setup exe or the portable ZIP from
  the latest Release.
