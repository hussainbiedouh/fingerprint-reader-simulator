# Security Policy

This project is a development simulator. It installs a test-signed driver and creates a virtual biometric sensor.

## Do not use for real authentication

- The driver is test-signed.
- Templates are synthetic.
- Matching is intentionally simple.
- Windows Hello login is not supported.
- The simulator must not protect real accounts, attendance records, payments, or sensitive data.

## Supported versions

No production support is provided. Security fixes are accepted on a best-effort basis.

## Reporting a vulnerability

Open an issue with reproduction steps. Do not include private keys, real biometric data, or production credentials.

For local use, keep `VirtualFingerprint.pfx` out of version control. The repository ignores `*.pfx` and generated catalog files.
