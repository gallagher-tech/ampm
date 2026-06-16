var os = require("os"); // http://nodejs.org/api/os.html
var path = require("path"); // http://nodejs.org/api/path.html
var crypto = require("crypto"); // http://nodejs.org/api/crypto.html
var fs = require("fs-extra"); // https://github.com/jprichardson/node-fs-extra
var _ = require("lodash"); // http://underscorejs.org/
var axios = require("axios"); // https://github.com/axios/axios

var BaseModel = require("./baseModel.cjs").BaseModel;

// PostHog analytics relay.
//
// Clients are unchanged: they still emit events to ampm over Socket.IO/OSC, and
// logging.cjs maps each one to a PostHog-native {event, properties} before
// handing it here. This model owns delivery to PostHog.
//
// Durability: every event is appended to an on-disk outbox (JSONL) BEFORE any
// network send, a flush worker batches unsent lines to PostHog's /batch/ HTTP
// endpoint, and the delivered count is persisted. On restart the outbox is
// replayed, so events survive crashes, restarts, and network drops -- the
// in-memory SDK queue does not, which is why we own the queue instead of using
// posthog-node. Each event carries a uuid so replays dedupe in PostHog.
exports.Analytics = BaseModel.extend({
  defaults: {
    enabled: false, // false to turn off
    apiKey: "", // PostHog project API key (write-only capture key).
    host: "https://eu.i.posthog.com", // PostHog Cloud EU / US / self-hosted URL.
    distinctId: "", // Optional fixed identity; empty -> generated + persisted.
    flushAt: 20, // Flush once this many events are queued.
    flushInterval: 10000, // ...or at least this often (ms).
    maxBatch: 250, // Max events per HTTP batch.
    outboxFile: "logs/posthog-outbox.jsonl", // Durable queue, relative to config.
  },

  // The resolved per-machine identity sent to PostHog.
  _distinctId: null,
  // Path to the file persisting the count of delivered lines.
  _offsetFile: null,
  // Guards against overlapping flushes.
  _sending: false,
  // The periodic flush timer.
  _flushTimer: null,
  // Count of outbox lines confirmed delivered to PostHog.
  _sent: 0,
  // Running count of total lines in the outbox (avoids re-reading on capture).
  _total: 0,

  initialize: function () {
    BaseModel.prototype.initialize.apply(this);

    if (!this.get("enabled")) {
      return;
    }

    // Validate the key up front. PostHog's ingest endpoint returns HTTP 200 even
    // for a missing/invalid api_key and then silently drops the events, so a bad
    // key looks exactly like success (the outbox offset advances, but nothing
    // shows up in PostHog). These checks surface the most common failure: the
    // %POSTHOG_API_KEY% placeholder being substituted with "" or the literal
    // "undefined" because the env var was not set in the shell that launched ampm.
    var apiKey = this.get("apiKey");
    if (!apiKey || apiKey === "undefined") {
      logger.warn(
        "PostHog is enabled but no apiKey resolved (check the POSTHOG_API_KEY env var in the shell that launched ampm). Events will be queued in the outbox but not delivered."
      );
    } else if (!/^phc_/.test(apiKey)) {
      logger.warn(
        "PostHog apiKey does not look like a project key (expected to start with 'phc_'; got " +
          apiKey.length +
          " chars). The ingest endpoint will still return 200 and the events will be dropped. Check the POSTHOG_API_KEY env var."
      );
    } else {
      logger.info(
        "PostHog enabled -> " +
          this.get("host") +
          " (apiKey resolved, " +
          apiKey.length +
          " chars)."
      );
    }

    // Resolve a stable identity: explicit config > persisted id > generate one.
    this._distinctId = this.get("distinctId");
    if (!this._distinctId) {
      var saved = global.$$serverState
        ? $$serverState.get("analyticsId")
        : null;
      if (!saved) {
        saved = os.hostname() + "-" + crypto.randomUUID();
        if (global.$$serverState) {
          $$serverState.saveState("analyticsId", saved);
        }
      }
      this._distinctId = saved;
    }

    // Make sure the outbox directory exists and learn where we left off.
    var outbox = this.get("outboxFile");
    this._offsetFile = outbox + ".offset";
    fs.ensureDirSync(path.dirname(outbox));

    try {
      if (fs.existsSync(this._offsetFile)) {
        this._sent = parseInt(fs.readFileSync(this._offsetFile, "utf8"), 10) || 0;
      }
    } catch (e) {
      this._sent = 0;
    }
    this._total = this._countLines();

    // Replay anything unsent from a previous run, then flush on an interval.
    this.replayUnsent();
    this._flushTimer = setInterval(
      _.bind(this._flush, this),
      this.get("flushInterval")
    );
  },

  // Append one event to the durable outbox, then flush if we have a batch.
  capture: function (evt) {
    if (!this.get("enabled") || !evt) {
      return;
    }

    var record = {
      uuid: crypto.randomUUID(),
      event: evt.event,
      distinct_id: evt.distinctId || this._distinctId,
      timestamp: evt.timestamp || new Date().toISOString(),
      properties: evt.properties || {},
    };

    try {
      fs.appendFileSync(this.get("outboxFile"), JSON.stringify(record) + "\n");
      this._total++;
    } catch (e) {
      logger.warn("Failed to write to the analytics outbox: " + e.message);
      return;
    }

    if (this._total - this._sent >= this.get("flushAt")) {
      this._flush();
    }
  },

  // Public: flush everything, resolving within timeoutMs no matter what.
  // Used by the shutdown hooks in server.cjs.
  flush: function (timeoutMs) {
    var flushing = Promise.resolve(this._flush());
    if (!timeoutMs) {
      return flushing;
    }
    var deadline = new Promise(function (resolve) {
      setTimeout(resolve, timeoutMs);
    });
    return Promise.race([flushing, deadline]);
  },

  // Re-send everything not yet confirmed delivered. Called at startup.
  replayUnsent: function () {
    return this._flush();
  },

  // Stop the periodic flush timer (used on shutdown after a final flush).
  stop: function () {
    if (this._flushTimer) {
      clearInterval(this._flushTimer);
      this._flushTimer = null;
    }
  },

  _countLines: function () {
    try {
      var data = fs.readFileSync(this.get("outboxFile"), "utf8");
      if (!data) {
        return 0;
      }
      return data.split("\n").filter(function (l) {
        return l.length > 0;
      }).length;
    } catch (e) {
      return 0;
    }
  },

  // Send all unsent events in batches; advance and persist the delivered count
  // only on a successful 2xx so failures simply retry next interval.
  _flush: async function () {
    if (this._sending || !this.get("enabled") || !this.get("apiKey")) {
      return;
    }
    this._sending = true;

    try {
      var lines;
      try {
        var data = fs.readFileSync(this.get("outboxFile"), "utf8");
        lines = data
          ? data.split("\n").filter(function (l) {
              return l.length > 0;
            })
          : [];
      } catch (e) {
        lines = [];
      }
      this._total = lines.length;

      // The outbox was compacted out from under our offset; start over.
      if (this._sent > lines.length) {
        this._sent = 0;
      }

      var url = this.get("host").replace(/\/+$/, "") + "/batch/";

      while (this._sent < lines.length) {
        var slice = lines.slice(this._sent, this._sent + this.get("maxBatch"));
        var batch = [];
        for (var i = 0; i < slice.length; i++) {
          try {
            var rec = JSON.parse(slice[i]);
            batch.push({
              event: rec.event,
              uuid: rec.uuid,
              timestamp: rec.timestamp,
              properties: _.extend({}, rec.properties, {
                distinct_id: rec.distinct_id,
              }),
            });
          } catch (e) {
            // Skip a corrupt line rather than wedging the whole queue.
          }
        }

        if (batch.length) {
          await axios.post(
            url,
            { api_key: this.get("apiKey"), batch: batch },
            { timeout: 10000, headers: { "Content-Type": "application/json" } }
          );
        }

        this._sent += slice.length;
        this._persistOffset();
      }

      this._maybeCompact(lines.length);
    } catch (e) {
      var reason = e && e.response
        ? "HTTP " + e.response.status
        : (e && (e.code || e.message)) || "unknown error";
      logger.warn("PostHog flush failed, will retry: " + reason);
    } finally {
      this._sending = false;
    }
  },

  _persistOffset: function () {
    try {
      fs.writeFileSync(this._offsetFile, String(this._sent));
    } catch (e) {
      logger.warn("Failed to persist the analytics offset: " + e.message);
    }
  },

  // Once everything is delivered and the file is large, truncate it so the
  // outbox stays bounded over a long-running 24/7 deployment.
  _maybeCompact: function (lineCount) {
    if (this._sent >= lineCount && lineCount > 1000) {
      try {
        fs.writeFileSync(this.get("outboxFile"), "");
        this._sent = 0;
        this._total = 0;
        this._persistOffset();
      } catch (e) {
        // Non-fatal; we'll compact on a later flush.
      }
    }
  },
});
