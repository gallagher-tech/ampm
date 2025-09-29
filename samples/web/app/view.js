// Root view, contains all subviews.
APP.View = Backbone.View.extend({
  events: {
    "click #crash": "_onCrashClicked",
    "click #hang": "_onHangClicked",
    "click #log": "_onLogClicked",
    "click #event": "_onEventClicked",
    "click #console": "_onConsoleClicked",
    "click #restart": "_onRestartClicked",
  },

  _subView: null,

  initialize: function () {
    // Initializing a sub view with a model.
    this._subView = new APP.Views.SomeView({
      el: this.$el,
      model: this.model.get("someModel"),
    });

    $("#config", this.$el).html(JSON.stringify(APP.config, null, "\t"));

    // Send the mouse position back to the server.
    this.$el.mousemove(function (e) {
      ampm.socket().emit("mouse", {
        x: e.pageX,
        y: e.pageY,
      });
    });
  },

  _onCrashClicked: function () {
    ampm.logEvent("APP CRASH", "crashed");
    // Crashes will cause heartbeats to stop being sent and your app will get restarted.
    var bar = foo.bar;
  },

  _onHangClicked: function () {
    ampm.logEvent("APP HANG", "hang");

    // Hangs will cause heartbeats to stop being sent and your app will get restarted.
    while (true) {}
  },

  // logEvent parameters: function(category, action, label, value)

  _onLogClicked: function () {
    // Example of how to send log messages.
    // 4th paramater '0' is arbitrary
    ampm.info("informational!");
    ampm.logEvent("APP LOG", "info");

    ampm.warning("warning!");
    ampm.logEvent("APP LOG", "warning");

    ampm.error("error!");
    ampm.logEvent("APP LOG", "error");
  },

  _onEventClicked: function () {
    // Example of how to track events.
    ampm.logEvent("APP EVENT", "clicked");
  },

  _onRestartClicked: function () {
    ampm.socket().emit("restart");
    ampm.logEvent("APP RESTART", "restart");
  },

  _onConsoleClicked: function () {
    ampm.logEvent("WEB CONSOLE OPENED", "open console");
    window.open("http://localhost:8888");
  },
});
