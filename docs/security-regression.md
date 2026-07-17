# Security and reliability regression matrix

Issue #18 uses the OpenAPI document as the schema boundary and repository-owned fixtures as the
cross-platform serialization boundary. CI runs the Android and Windows contract tests after linting
`protocol/openapi.yaml`.

| Risk | Automated coverage |
| --- | --- |
| Missing, invalid, and revoked bearer tokens | `ApiControllerTest.privateRoutesRequireBearerToken`, `ApiControllerTest.revokeInvalidatesPairedToken` |
| Expired pairing window/session | `PairingManagerTest.expiredWindowStopsPairing` and pairing confirmation tests |
| Replayed transfer-status sequence | `ApiControllerTest.transferStatusRequiresAuthRejectsReplayAndNeverAcceptsAPath` |
| Invalid opaque media ID and path traversal | `DefaultMediaRepositoryTest.rejects file paths instead of treating them as media IDs` |
| Invalid or out-of-range HTTP Range | `ApiControllerTest` range tests |
| Media write verbs/routes | `ReadOnlyRoutePolicyTest` and `ApiControllerTest.writesAndUnknownMediaRoutesDoNotExist` |
| Android/Windows success response drift | shared `protocol/fixtures/media-page.json` consumed by `ProtocolContractFixtureTest` and `HttpReadOnlyMediaSourceTests` |
| Android/Windows problem response drift | shared `protocol/fixtures/problem.json` consumed by the same test suites |
| Disconnect and truncated response | `PersistentTransferCoordinatorTests` interruption/truncation tests |
| Disk full and access denied | `PersistentTransferCoordinatorTests.PermanentDestinationFailuresDoNotRetry` |
| Process restart and partial-file recovery | `PersistentTransferCoordinatorTests` persisted queue/restart tests |
| Remote size/entity change | `PersistentTransferCoordinatorTests.RemoteSizeChangeFailsWithoutPublishingDestination` and resume/entity-tag tests |

CI uploads only protocol lint text, TRX/coverage output, and Android JUnit/HTML reports, even when a
job fails. These paths must never include application data directories, media files, bearer tokens,
pairing keys, credentials, or runtime logs. Contract fixtures are synthetic and governed by
`protocol/fixtures/README.md`.

Access-token expiry is not part of the current pairing contract. The MVP uses a random stored token
until explicit revocation. A future TTL must not be added without refresh/rotation, migration, an
explicit expired-credential response, and Windows reauthentication UX; see GitHub issue #116.

Android instrumentation tests are compiled in ordinary CI but require an emulator/device lane to
execute. Foreground-service notification visibility, Doze, OEM task killers, and interruption of a
real ContentResolver stream remain physical-device acceptance work and are not claimed by JVM tests.
