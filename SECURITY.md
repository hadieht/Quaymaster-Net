# Security policy

Report vulnerabilities privately via GitHub security advisories.

## Model (same threat model as the original Quaymaster)

- Docker socket access is root-equivalent on the host; the backend narrows
  what it will do (read-only mode, managed scope, self-protection, audit)
  but cannot make the socket itself safe.
- Authentication required for everything except `/healthz`, login and
  first-run setup. No default password; the first admin is created with a
  one-time setup token.
- Server-side hashed sessions, `HttpOnly; SameSite=Strict` cookies
  (`qm_session` / `__Host-qm_session`), CSRF token (`X-QM-CSRF`) plus exact
  `Origin` check on every state-changing request, login rate limiting,
  strict CSP.
- Every Docker/Compose mutation goes through one gateway service:
  no generic Docker proxy, no route accepts a command line.
- Secrets in env vars, labels, Compose config, events and logs are redacted
  before they leave the process.

## Reporting

Use [GitHub's private vulnerability reporting](https://github.com/hadieht/quaymaster-net/security/advisories/new).
Response targets: acknowledgement within 48h, patch target within 30 days
for critical.
