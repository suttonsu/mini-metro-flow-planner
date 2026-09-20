# Security Policy

## Supported version

Security fixes are applied to the current `main` branch.

## Reporting a vulnerability

Please use GitHub's private vulnerability reporting feature when it is available for this repository. Do not open a public issue containing credentials, private paths, save data, or an exploit that modifies arbitrary game files.

## Trust boundaries

- The repository does not ship Mini Metro assemblies or assets.
- Build and install scripts operate only on the explicitly selected Mini Metro directory.
- The vision sidecar does not inject mouse or keyboard input and does not expose a network service.
- Model bundles are validated with a manifest and SHA-256 hashes before activation.
- A missing, stale, or invalid visual snapshot falls back to native game state instead of granting additional control.

