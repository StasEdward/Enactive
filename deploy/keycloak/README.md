# Keycloak deployment for Enactive

Prepared deployment configuration; not evidence that Keycloak is installed on enactive.app.
The actual SSH destination, available memory, Docker version, existing services, and HTTPS routing
must be inspected on that host before installation. `auth.enactive.app` is a proposed public origin.

The Compose stack uses production-mode Keycloak and a separate PostgreSQL database with a persistent
volume. It does not modify Gateway or its database. Only loopback ports 8180 and 9190 are published;
PostgreSQL has no published port. Ensure these ports are free and the host can spare at least the
2 GiB Keycloak limit plus PostgreSQL memory. Verify the pinned Keycloak image is available and review
the current security release before deployment. Record image digests used on the host.

Copy `compose.yaml` and `.env.example` into a dedicated server directory, such as
`/opt/enactive-keycloak`. Create a mode-0600 `.env` there, supplying two independently generated
passwords. Never commit or print that file in deployment logs. The Compose file deliberately refuses
to start with empty passwords. Run `docker compose config --quiet` to validate without printing secrets,
then `docker compose up -d`. Keep the Docker socket restricted: container inspection exposes environment
variables to Docker administrators.

Configure the existing host reverse proxy or Cloudflare Tunnel to serve the chosen HTTPS origin
through `http://127.0.0.1:8180`. It must overwrite forwarded headers and preserve the correct public
host and HTTPS scheme. If the proxy itself is containerized, adapt networking after inspecting the
host; its localhost is not the host's localhost. Do not expose port 9190 through public routing.

Check `http://127.0.0.1:9190/health/ready` locally and the public
`https://auth.enactive.app/realms/master/.well-known/openid-configuration` over HTTPS. Check its issuer
and endpoint URLs contain the intended public origin. Confirm service recovery after a container
restart and verify that no database or management port listens on a public interface.

Use the temporary bootstrap account to create a permanent Keycloak operator with MFA, verify that
operator can log in, then delete the temporary account and remove its bootstrap settings from the
Compose environment and `.env`. Do not leave a temporary bootstrap account as the permanent operator.

Installation alone does not enable Enactive administrative login. Next create a dedicated `enactive`
realm and confidential client, configure required MFA and its signed authentication context, and
register the exact Gateway admin callback after confirming its hostname. Test both MFA success and
password-only rejection. Follow [the Gateway administration guide](../../Docs/REMOTE_ADMINISTRATION.md)
for the five Gateway settings and CLI grant; Keycloak roles do not automatically grant Gateway access.

Back up PostgreSQL using `pg_dump` and verify restoration before relying on the service. Protect backups:
they contain authentication data and signing keys. Retain the Compose configuration and encrypted
copies of the required secrets. Do not use `docker compose down -v` during routine updates: it deletes
the identity database. Back up before upgrades; database migrations can prevent image-only rollback.

References: [official container guide](https://www.keycloak.org/server/containers),
[reverse proxy configuration](https://www.keycloak.org/server/reverseproxy), and
[supported databases](https://www.keycloak.org/server/db).
