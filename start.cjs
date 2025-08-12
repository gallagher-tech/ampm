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
  var npxExecutable = "npx"; // Use npx to avoid platform specific issues with nodemon
  var commandAndArgs = ["nodemon"].concat(args);

  var ampm = child_process.spawn(npxExecutable, commandAndArgs, {
    stdio: "inherit",
    shell: process.platform === "win32", // Still useful for npx on Windows
  });
  ampm.on("close", start);
}

start();
