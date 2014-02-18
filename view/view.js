var View = Backbone.View.extend({

	events: {
		'click #shutdown': '_onShutdownClicked',
		'click #start': '_onStartClicked',
		'click #restart': '_onRestartClicked',
		'click #update': '_onUpdateClicked'
	},

	_socket: null,

	initialize: function() {
		this._socket = io.connect();
		this._socket.on('appState', _.bind(this._onAppState, this));
	},

	_onAppState: function(message) {
		$(document.body).show();

		message.uptime = moment.duration(message.uptime, 'milliseconds').format('dd:hh:mm:ss');
		message.fps = message.fps ? message.fps[message.fps.length - 1] : '';
		message.cpu = message.cpu ? message.cpu[message.cpu.length - 1] : '';
		message.memory = message.memory && message.memory.length ? humanize.filesize(message.memory[message.memory.length - 1]) : '';
		message.logList = '';
		_.each(message.logs, function(log) {
			message.logList += log.level + ': ' + log.msg + '\n';
		});

		message.eventList = '';
		_.each(message.events, function(e) {
			message.eventList += JSON.stringify(e) + '\n';
		});

		var template = _.template($('#info-template').html(), message);
		$('#info').html(template);

		$('#shutdown').toggle(message.isRunning);
		$('#start').toggle(!message.isRunning);
		$('#restart').toggle(message.isRunning);
		$('#update').toggle(message.canUpdate);
	},

	_onShutdownClicked: function() {
		if (window.confirm('Are you sure you want to shut down the app? It will not restart automatically.')) {
			this._socket.emit('shutdown');
		}
	},

	_onStartClicked: function() {
		this._socket.emit('start');
	},

	_onRestartClicked: function() {
		if (window.confirm('Are you sure you want to shut down and restart the app?')) {
			this._socket.emit('restart');
		}
	},

	_onUpdateClicked: function() {
		this._socket.emit('updateContent');
	}
});