# Contract fixtures

These fixtures are synthetic repository-owned test data. They must not contain personal media,
real device identifiers, bearer tokens, pairing keys, or local filesystem paths.

The Android serializer and Windows protocol parser both consume these files. A field-level change
therefore fails at least one platform test unless the shared contract is updated intentionally.
