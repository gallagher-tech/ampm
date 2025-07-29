import "dotenv/config";
import express from "express";
import path from "path";
import cors from "cors";

export class Plugin {
  async boot() {
    const __dirname = path.resolve();
    const app = express();

    process.on('uncaughtException', (error) => {
      console.error('Uncaught exception:', error);
      // Handle the error gracefully
      // Consider logging the error and restarting the application
    });

    const startServer = function () {
      async function init() {
        /** Uncomment if you need a websocket server */
        // const websocket = new Websocket();
        // websocket.init();

        /** Web Server to host interactive */
        app.use(
          express.static(path.join(__dirname, "./dist"), {maxage: "365d"})
        );
        app.use(express.static('cms'))
        const assetsPath = express.static(path.join(__dirname, "./dist/assets"));
        app.use("/assets", assetsPath);

        app.use(cors());
        app.use(express.json()); // to support JSON-encoded bodies
        app.use(express.urlencoded({extended: true})); // to support URL-encoded bodies

        const port = process.env.PORT || 3000;
        app.listen(port, function () {
          console.log(`current environment is: ${process.env.NODE_ENV}`);
          console.log(`\nWeb server is listening at http://localhost:${port}\n`);
        });

        /** making each page an SPA, all pages redirect to index */
        app.get("/", (req, res) => {
          res.sendFile(path.join(__dirname, "./dist/index.html"));
        });

        /** API calls here */

        /** console errors from client are logged in server logs */
        app.post("/console-error", function (req) {
          console.log("CLIENT CONSOLE ERROR: ", req.body.message);
        });

      }
      init();

    };
    startServer();
  }
}