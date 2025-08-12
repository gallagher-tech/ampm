import WebSocket, {WebSocketServer} from 'ws';
import {v4 as uuidv4} from 'uuid';
import {JSONFilePreset} from 'lowdb/node'

//TODO: fix the lowdb querie write for the new db syntax
class Websocket {
  constructor() {
    this.wss = new WebSocketServer({port: 3333});
    this.ws = null;
  }

  init() {
    this.wss.on('connection', (ws) => this.onConnection(ws));
    this.wss.on('close', this.onClose);
    this.wss.on('error', (error) => this.onError(error));
    this.wss.on('listening', this.onListening);

    const initDb = async () => {
      this.db = await JSONFilePreset('db.json', {gaQueries: []});
      this.db.data = {gaQueries: []};
      this.db.write();
    };
    initDb();
  }

  onConnection(ws) {
    this.ws = ws;

    console.log('wss client conneciton established');

    ws.on('message', (msg) => this.sendMessage(msg));

    ws.on('close', () => this.onClientClose(ws));
  }

  onClose() {
    console.log('wss closed');
  }

  onClientClose(id, client) {
    console.log(id, 'closed connection');
    if (client) {
      client.terminate();
    }
  }

  sendMessage(msg, sender, id) {
    if (this.wss) {
      const parsedMsg = JSON.parse(msg);
      console.log(`received message from client: `, parsedMsg);
      this.db.data = {
        gaQueries: [
          ...this.db.data.gaQueries,
          {
            event: parsedMsg.event,
            distinct_id: uuidv4(),
            timestamp: parsedMsg.timestamp,
            properties: {
              ...parsedMsg.properties
            },
          },
        ]
      };
      this.db.write();
    }
  }

  onError(error) {
    console.log('wss error: ', error);
  }

  onListening() {
    console.log('wss is listening on 3333');
  }
};
export default Websocket;
