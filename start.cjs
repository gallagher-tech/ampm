#! /usr/bin/env node

// Run ampm under nodemon. It watches changes to ampm-restart.json so that ampm
// can restart itself. It also restarts on crash.

// Really should be using nodemon as a module, but:
// https://github.com/stimulant/ampm/issues/12

const path = require("path");
const child_process = require("child_process");
const fs = require("fs");

var configFiles = process.argv[2] || "ampm.json";
var configFile = path.resolve(configFiles.split(",")[0]);
var appPath = path.dirname(configFile);
var restartFile = path.join(appPath, "ampm-restart.json");
var stateFile = path.join(appPath, "ampm-state.json");
var mode = process.argv[3] || "default";
var server = path.join(__dirname, "server.cjs");

if (!fs.existsSync(restartFile)) {
  fs.writeFileSync(restartFile, "");
}
if (!fs.existsSync(stateFile)) {
  fs.writeFileSync(stateFile, "{}");
}

// Single-instance lock: exit cleanly if another ampm is already running.
var lockFile = path.join(appPath, ".ampm.lock");
if (fs.existsSync(lockFile)) {
  var lockPid = parseInt(fs.readFileSync(lockFile, "utf8").trim());
  try {
    process.kill(lockPid, 0); // throws if process no longer exists
    console.error("ampm is already running (PID " + lockPid + "). Exiting.");
    process.exit(0);
  } catch (e) {
    // Stale lock file — previous run didn't clean up. Continue.
    console.log("Stale ampm lock file found (PID " + lockPid + "). Starting fresh.");
  }
}
fs.writeFileSync(lockFile, String(process.pid));
function cleanupLock() { try { fs.unlinkSync(lockFile); } catch (e) {} }
process.on("exit", cleanupLock);
process.on("SIGINT", function () { cleanupLock(); process.exit(0); });
process.on("SIGTERM", function () { cleanupLock(); process.exit(0); });

var args = [
  "--verbose",
  "--exitcrash",
  "--watch",
  configFile,
  "--watch",
  restartFile,
  "--ignore",
  "logs",
  "--ignore",
  stateFile,
  server,
  configFiles,
  mode,
];

// If there are arguments beyond the config file and mode, pass them to nodemon.
process.argv.slice(4).forEach(function (a, i) {
  args.splice(args.length - 3, 0, a);
});

function start() {
  var ampm = child_process.spawn("nodemon", args, {
    stdio: "inherit",
    shell: process.platform === "win32",
  });
  ampm.on("close", start);
}

start();
