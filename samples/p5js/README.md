# ampm p5.js sample

A minimal p5.js app wired to ampm for heartbeat monitoring and event tracking.
The sketch sends a `session/start` event on connect and an `interaction/click`
event on every canvas click. Events flow over the socket to the ampm server,
which logs them and (when configured) relays them to PostHog.

## Install

Install dependencies in **both** this directory and the `server` directory:

```
npm install
cd server && npm install
```

## Run (local development)

```
ampm ampm.json dev
```

Then open **http://localhost:8000/** in your browser. Click the canvas to send
events; watch them arrive in the ampm web console at **http://localhost:8888/**.

> Dev mode runs the ampm server and the static file server only — it does **not**
> auto-launch a browser. This avoids the kiosk-launch conflicts that happen when
> Chrome is already running on a development machine. Just open the URL yourself.

## Run (production / kiosk)

```
ampm
```

The default config launches Chrome in kiosk mode pointed at the app, restarts it
if it crashes or stops sending heartbeats, and is intended for a dedicated kiosk
machine where Chrome is not already running. (On a dev machine where Chrome is
already open, the kiosk launch hands off to the existing instance and ampm will
restart-loop — use `ampm ampm.json dev` for local testing instead.)

## Sending events to PostHog

Events are relayed to PostHog when `logging.posthog` is enabled and an API key is
provided. The key goes either directly in the `apiKey` field, or via the
`POSTHOG_API_KEY` Windows environment variable using the `%POSTHOG_API_KEY%`
placeholder — see [Event Tracking](../../README.md#integration-events) in the main
README for the steps. The committed `ampm.json` ships with PostHog disabled and no
key.
