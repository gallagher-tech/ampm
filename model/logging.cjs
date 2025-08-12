var os = require("os"); // http://nodejs.org/api/os.html
var path = require("path"); //http://nodejs.org/api/path.html
var child_process = require("child_process"); // http://nodejs.org/api/child_process.html
var util = require("util"); // http://nodejs.org/api/util.html

var moment = require("moment"); // Date processing. http://momentjs.com/
var _ = require("lodash"); // Utilities. http://underscorejs.org/
var Backbone = require("backbone"); // Data model utilities. http://backbonejs.org/
var winston = require("winston"); // Logging. https://github.com/flatiron/winston
var fs = require("fs-extra"); // Enhanced file system with recursive directory creation. https://github.com/jprichardson/node-fs-extra
var ua = require("universal-analytics"); // Google Analytics. https://npmjs.org/package/universal-analytics

var BaseModel = require("./baseModel.cjs").BaseModel;

// Initialize and manage the various loggers.
exports.Logging = BaseModel.extend({
  // See readme.md for all this.
  defaults: {
    // Settings for the file logger.
    file: {
      enabled: true, // false to turn off
      filename: "logs/server", // Path to the log file, relative to server.js.
      maxsize: 1048576, // The max size of the log file before rolling over (1MB default)
      json: false, // Whether to log in JSON format.
      level: "info", // The logging level to write: info, warn, error.,
      datePattern: ".yyyy-MM-dd.log",
    },

    // Settings for the console logger.
    console: {
      enabled: true, // false to turn off
      colorize: true, // Colors are fun.
      timestamp: true, // Include timestamps.
      level: "info", // The logging level to write: info, warn, error.
      preserve: false, // If false, the ampm client should send console output to the server if possible.
    },

    // Settings for Google Analytics.
    google: {
      enabled: true, // false to turn off
      accountId: "", // The property ID -- this should be unique per project
    },

    // Settings for the event log file.
    eventFile: {
      enabled: true, // false to turn off
      filename: "logs/event-{date}.tsv", // Path to the log file, relative to server.js. {date} will be replaced by the current date.
    },

    // Settings for screenshots taken after crashes.
    screenshots: {
      enabled: true, // false to turn off
      filename: "logs/capture-{date}.jpg", // Path to save screen shots, relative to server.js. {date} will be replaced by the current date.
    },

    // Settings for loggly.com.
    loggly: {
      enabled: false, // false to turn off
      subdomain: "", // The account name. https://stimulant.loggly.com/dashboards
      inputToken: "", // The API token.
      json: true, // Whether to log as JSON -- this should be true.
      token: "", // The um, other token.
      tags: "ampm", // A tag to differentiate app logs from one another in loggly.
    },

    // Settings for the email logger.
    mail: {
      enabled: false, // false to turn off
      ssl: false, // Whether to use SSL.
      subject: "ERROR: {hostname}", // The subject of the emails. "{hostname}" is replaced by the output of os.hostname().
      level: "error", // The logging level to write: info, warn, error.
      host: "", // The SMTP server to use.
      username: "", // The account to log in with.
      from: "", // Where the emails should appear to be from.
      password: "", // The password to log in with.
      to: "", // Where the emails should go.
    },

    cacheAmount: 20, // How many lines of logs and events to show in the web console.

    // Cache of the last n log messages, sent to console.
    logCache: null,

    // Cache of the last n GA events, sent to console.
    eventCache: null,
  },

  // The Google Analytics client.
  _google: null,

  // A console window used for the event viewer logger.
  _eventSourceConsole: null,

  initialize: function () {
    BaseModel.prototype.initialize.apply(this);

    // Create logger using modern Winston API
    global.logger = winston.createLogger({
      exitOnError: $$persistence.get("exitOnError"),
      transports: [],
    });

    // Set up console logger.
    if (this.get("console").enabled) {
      const consoleOptions = {
        format: winston.format.combine(
          this.get("console").timestamp
            ? winston.format.timestamp()
            : winston.format.simple(),
          this.get("console").colorize
            ? winston.format.colorize()
            : winston.format.simple(),
          winston.format.printf(
            (info) =>
              `${info.timestamp ? info.timestamp + " " : ""}${info.level}: ${
                info.message
              }`
          )
        ),
        level: this.get("console").level,
        handleExceptions: !$$persistence.get("exitOnError"),
      };
      logger.add(new winston.transports.Console(consoleOptions));
    }

    // Set up file logger.
    if (this.get("file").enabled) {
      // Create the log file folder.
      var dir = path.dirname(this.get("file").filename);
      if (!fs.existsSync(dir)) {
        fs.mkdirSync(dir, { recursive: true });
      }

      const fileFormat = winston.format.combine(
        winston.format.timestamp({
          format: "YYYY-MM-DD HH:mm:ss",
        }),
        this.get("file").json ? winston.format.json() : winston.format.simple(),
        winston.format.printf(
          (info) => `${info.timestamp}: ${info.level}: ${info.message}`
        )
      );

      // Fixed daily rotate file configuration for compatibility with Winston 3.x
      const fileOptions = {
        format: fileFormat,
        dirname: path.dirname(this.get("file").filename),
        filename: path.basename(this.get("file").filename) + "-%DATE%",
        datePattern: "YYYY-MM-DD",
        maxSize: this.get("file").maxsize,
        level: this.get("file").level,
        handleExceptions: !$$persistence.get("exitOnError"),
      };

      const winstonDailyRotateFile = require("winston-daily-rotate-file");
      logger.add(new winstonDailyRotateFile(fileOptions));
    }

    // Set up email.
    if (this.get("mail").enabled) {
      var subject = this.get("mail").subject;
      if (subject) {
        subject = subject.replace("{hostname}", os.hostname());
      } else {
        subject = os.hostname();
      }

      function deepFind(obj, path) {
        var paths = path.split(".");
        var current = obj;
        var i;

        for (i = 0; i < paths.length; ++i) {
          if (current[paths[i]] === undefined) {
            return undefined;
          } else {
            current = current[paths[i]];
          }
        }
        return current;
      }

      subject = subject.replace(/\{([^\}]+)\}/g, function (mustache, param) {
        var configProp = deepFind($$config, param);
        return configProp === undefined ? mustache : configProp;
      });

      const mailOptions = {
        transportOptions: {
          host: this.get("mail").host,
          auth: {
            user: this.get("mail").username,
            pass: this.get("mail").password,
          },
          secure: this.get("mail").ssl,
        },
        messageOptions: {
          to: this.get("mail").to,
          from: this.get("mail").from,
          subject: subject,
        },
        level: this.get("mail").level,
      };

      try {
        const Mail = require("winston-mail-lite"); // Corrected import
        logger.add(new Mail(mailOptions));
      } catch (e) {
        console.error("Failed to initialize mail transport:", e);
      }
    }

    // Set up loggly.
    if (this.get("loggly").enabled) {
      var opts = _.clone(this.get("loggly"));
      opts.tags = opts.tags ? [opts.tags] : [];
      opts.tags.push(os.hostname());

      try {
        const { Loggly } = require("winston-loggly-bulk");
        logger.add(new Loggly(opts));
      } catch (e) {
        console.error("Failed to initialize Loggly transport:", e);
      }
    }

    // Set up Google Analytics.
    if (this.get("google").enabled) {
      this._google = ua(this.get("google").accountId, os.hostname(), {
        strictCidFormat: false,
      });
    }

    // Create the screenshots directory if needed.
    if (this.get("screenshots").enabled) {
      var shotsDir = path.dirname(this.get("screenshots").filename);
      if (!fs.existsSync(shotsDir)) {
        fs.mkdirSync(shotsDir);
      }
    }

    // Set up the cache, which is just a history of log messages.
    if (this.get("cacheAmount")) {
      this.set("logCache", []);
      this.set("eventCache", []);
      // Updated to use modern Winston 'logged' event instead of 'logging'
      logger.on(
        "logged",
        _.bind(function (info) {
          if (info.level) {
            var cache = this.get("logCache");
            cache.push({
              time: moment().format("YYYY-MM-DD HH:mm:ss"),
              level: info.level,
              msg: info.message,
            });
            if (cache.length > this.get("cacheAmount")) {
              cache.splice(0, cache.length - this.get("cacheAmount"));
            }
            if ($$network.transports.socketToConsole) {
              $$network.transports.socketToConsole.emit(
                "log",
                cache[cache.length - 1]
              );
            }
          }
        }, this)
      );
    }

    // Updated to use modern Socket.IO API (removed .sockets)
    $$network.transports.socketToApp.on(
      "connection",
      _.bind(function (socket) {
        // Log on request from the app.
        socket.on("log", _.bind(this._logMessage, this));
        // Track events on request from the app.
        socket.on("event", _.bind(this._logEvent, this));
      }, this)
    );

    // Log on request from the app.
    $$network.transports.oscFromApp.on("log", _.bind(this._logMessage, this));
    // Track events on request from the app.
    $$network.transports.oscFromApp.on("event", _.bind(this._logEvent, this));
  },

  _logMessage: function (data) {
    if (logger && logger[data.level]) {
      logger[data.level](data.message);
    }
  },

  _logEvent: function (data) {
    if (!data) {
      logger.warn("_logEvent called without any data.");
      return;
    }

    var cache = this.get("eventCache");
    cache.push({
      time: moment().format("YYYY-MM-DD HH:mm:ss"),
      data: data,
    });
    if (cache.length > this.get("cacheAmount")) {
      cache.splice(0, cache.length - this.get("cacheAmount"));
    }

    if (this._google) {
      // Log to Google Analytics.

      this._google.eventCount = this._google.eventCount || 0;

      var params = {};
      // Restart a session every 500 events, see issue #40
      if (this._google.eventCount >= 500) {
        this._google.eventCount = 0;
        params.sessionControl = "start";
      }

      this._google.event(
        data.Category,
        data.Action,
        data.Label,
        data.Value,
        params
      );
      var queue = _.clone(this._google._queue);
      this._google.send(
        _.bind(function (error) {
          if (!error) {
            this._google.eventCount++;
            return;
          }

          if (error.code === "ENOTFOUND") {
            // Couldn't connect -- replace the queue and try next time.
            // https://github.com/peaksandpies/universal-analytics/issues/12
            this._google._queue = queue;
          } else {
            // Something else bad happened.
            logger.warn("Error with Google Analytics", error);
          }
        }, this)
      );
    }

    if (this.get("eventFile").enabled && this.get("eventFile").filename) {
      var dir = path.dirname(this.get("eventFile").filename);
      if (!fs.existsSync(dir)) {
        fs.mkdirSync(dir);
      }

      // Log to the event log file.
      var date = new Date();
      var datestring = date.getFullYear() + "-";
      var month = date.getMonth() + 1;
      if (month < 10) {
        month = "0" + month;
      }
      datestring += month + "-";
      var day = date.getDate();
      if (day < 10) {
        day = "0" + day;
      }
      datestring += day;

      var fileName = this.get("eventFile").filename.replace(
        "{date}",
        datestring
      );
      var timestamp = Math.round(date.getTime() / 1000);
      var log = util.format(
        "%d\t%s\t%s\t%s\t%d\n",
        timestamp,
        data.Category || "",
        data.Action || "",
        data.Label || "",
        data.Value || 0
      );
      fs.appendFile(fileName, log, (error) => {
        if (error) throw error;
      });
    }
  },
});
