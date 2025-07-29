
# ExpressJS (and Websocket) Server for GA-APP2.0

This is the boilerplate web server using **websockets**.

Using:
* NodeJS (LTS, v22)
* [ws](https://www.npmjs.com/package/ws)
* [ampm](https://www.npmjs.com/package/ampm)
* Chrome Kiosk mode (commands in `./bin`) for web interactives in prod

---


## Back-End Setup

Get into server folder
``cd ./server``

Install packages
``npm install``

Commands available:
- **Dev mode** `npm run dev`

- **Production mode** `npm run start`

- **Deploy: ampm startup and monitoring command (starts the server and opens and monitors a browser window)** `ampm`
   - to deploy dev or mac or another config: `ampm ampm.json dev`

---

## General Commands

Develop Mode:
- In client web app: `npm run dev`
- In server: `npm run dev`

Production Mode AMPM:
- stop: `CTRL+C`
- get server log are in  `server/logs`

---

## DEPLOY DEV BUILD TO GOOGLE CLOUD + FIREBASE

This dev site is running the web server on App Engine and the front-end on Firebase, due to weird caching issues when deploying the full site to App Engine only. 

In the `Server` Folder
* make sure you have `gcloud` installed, are logged in, and project set to `usafa-visitor-center`
* run command: `gcloud app deploy app.yaml --project=usafa-visitor-center`


## DEPLOY TO MUSEUM / ONSITE
* run `npm run build:install` in `/server`
* upload `build.zip` to museum PC, unzip where you want it to sit (i.e. Desktop)
* make a Task Scheduler task to start the `start_app.bat` file with highest priviledges on Log On (with a 1 min delay) **(update paths in the batch file!)**

---

## Windows
Set the shell to use NODE_ENV in Windows CMD prompt: `npm config set script-shell "C:\\Program Files\\git\\bin\\bash.exe"`


## Proximity Sensor

There is code in the ``proximity-sensor`` directory for a USB/Serial-Port based proximity/infrared sensor.
