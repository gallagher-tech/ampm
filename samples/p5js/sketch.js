let x = 0;
let y = 0;
let clickCount = 0;

function setup() {
  createCanvas(720, 400);
  noStroke();

  // Start the heartbeat loop so ampm knows the app is alive
  (function heart() {
    ampm.heart();
    requestAnimationFrame(heart);
  })();

  // Send a session-start event once the socket connects
  ampm.socket().on('connect', function() {
    ampm.logEvent('session', 'start', 'p5js', 1);
  });
}

function draw() {
  background(51);

  fill(200);
  textSize(14);
  text('p5js + ampm — click canvas to send events', 50, 30);
  text('Events sent: ' + clickCount, 50, 55);

  // Follow the mouse with a lerp
  x = lerp(x, mouseX, 0.05);
  y = lerp(y, mouseY, 0.05);

  fill(255);
  ellipse(x, y, 66, 66);
}

function mouseClicked() {
  clickCount++;
  ampm.logEvent('interaction', 'click', 'canvas', clickCount);
}
