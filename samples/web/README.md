# AMPM Sample Project

**make sure the ampm.json file contains this line:**

```json
"plugin": "index.js",
```

With the file being your web server start file. This is what runs the web server. And the `"launchCommand"` then opens and watches the browsers and reloads it if it crashes.

This is how you should start your web server in your index.js file for ampm versus no ampm:

```js
// this is needed for ampm to run our server as a plugin
export class Plugin {}
// ampm doesn't give us an ENV, so its undefined.
if (!process.env.NODE_ENV) {
  Plugin.prototype.boot = async () => {
    startServer();
  };
} else {
  startServer();
}
```

cd `/cient`
`npm install`

for dev: `npm run dev`

for prod: `npm build`

---

cd `/server`
`npm install`

(run dev server from `/client` as well with `npm run dev`)
for dev: `./node_modules/.bin/ampm ampm.json dev` or `./node_modules/.bin/ampm ampm.json mac.dev`

for prod: `./node_modules/.bin/ampm` or `./node_modules/.bin/ampm ampm.json mac`

(if you want to use just the `ampm` command, instal the package globally)
`ampm ampm.json dev` - to load the ampm.json file, but run in dev mode.

If prompted for a username and password when opening the AMPM Web Server (to see the logs):
Username: username
Password: password
