import * as ChromeLauncher from 'chrome-launcher';
import {launch} from 'chrome-runner';

const newFlags = ChromeLauncher.Launcher.defaultFlags().filter(flag => flag !== '--mute-audio');
let moreFlags = [];
moreFlags = [
  '--autoplay-policy=no-user-gesture-required',
  '--disable-pinch',
  '--overscroll-history-navigation=0',
  '--disable-infobars',
  '--simulate-outdated-no-au="01 Jan 2199"',
  '--kiosk'
];

const launchChrome = async (url, debugPort) => {
  const runner = await launch({
    port: debugPort,
    chromeFlags: [...newFlags, ...moreFlags],
    startupPage: url,
    shouldRestartChrome: true,
  });
  console.log(`Chrome debugging port running on ${runner.port}`);
};

if (process.env.NODE_ENV === 'production') {
  console.log(`launching chrome in PROD mode at ${process.env.URL}`);
  launchChrome(process.env.URL, process.env.DEBUG_PORT);
} else if (process.env.NODE_ENV === 'development') {
  // start a different URL
  console.log(`launching chrome in DEV mode at ${process.env.URL}`);
  launchChrome(process.env.URL, process.env.DEBUG_PORT);
} else {
  console.log(`launching chrome in DEFAULT mode at default URL: http://localhost:3000`);
  launchChrome('http://localhost:3000');
}

/**
 * Example of starting a large display screen and kiosks
 * Can put any of these vars in ecosystem file.
 * In ecosystem.config.cjs:
 * screen = KIOSK or DISPLAY
 * childpc =  192.168.120.115 (aka IP of server/display)
 * if (process.env.childpc === '192.168.120.115') {
    launchChrome(`http://192.168.120.115:3000/${process.env.screen}?num=${process.env.num}`);
  } else {
    launchChrome(`http://localhost:3000/${process.env.screen}`);
  }
 */