#include "ofMain.h"
#include "AMPMClient.h"

#include <unordered_map>
using namespace std;

namespace ampm {

	const unordered_map<ampm::LogEventLevel, const char*> LogEventLevelToString = {
		{ ampm::LogEventLevel::AMPM_INFO, "info" },
		{ ampm::LogEventLevel::AMPM_WARNING, "warn" },
		{ ampm::LogEventLevel::AMPM_ERROR, "error" } };

	// ----------------------
	// logging
	// ----------------------
	LogEventLevel getLogLevel(ofLogLevel level)
	{

		switch (level) {
		case OF_LOG_VERBOSE:
			return LogEventLevel::AMPM_INFO;
			break;
		case OF_LOG_NOTICE:
			return LogEventLevel::AMPM_INFO;
			break;
		case OF_LOG_WARNING:
			return LogEventLevel::AMPM_WARNING;
			break;
		case OF_LOG_ERROR:
			return LogEventLevel::AMPM_ERROR;
			break;
		case OF_LOG_FATAL_ERROR:
			return LogEventLevel::AMPM_ERROR;
			break;
		case OF_LOG_SILENT:
			return LogEventLevel::AMPM_INFO;
			break;
		default:
			break;
		}
		return LogEventLevel::AMPM_ERROR;
	}
	AMPMLoggerChannel::AMPMLoggerChannel()
	{
	}

	AMPMLoggerChannel::~AMPMLoggerChannel()
	{
	}

	void AMPMLoggerChannel::log(ofLogLevel level, const std::string& module, const std::string& message)
	{
		if (isAMPMLoggingLevel(level)) {
			ampm::ampm()->log(getLogLevel(level), "[" + ofGetLogLevelName(level, false) + "] " + module + ": " + message);
		}
		else {
			// print to cerr for OF_LOG_ERROR and OF_LOG_FATAL_ERROR, everything else to cout
			stringstream out;
			out << "[" << ofGetLogLevelName(level, false) << "] ";
			// only print the module name if it's not ""
			if (module != "") {
				out << module << ": ";
			}
			out << message << endl;
			OutputDebugStringA(out.str().c_str());
		}
	}

	void AMPMLoggerChannel::log(ofLogLevel level, const std::string& module, const char* format, ...)
	{
		va_list args;
		va_start(args, format);
		log(level, module, format, args);
		va_end(args);
	}

	void AMPMLoggerChannel::log(ofLogLevel level, const std::string& module, const char* format, va_list args)
	{
		std::string buffer;
		buffer = "[" + ofGetLogLevelName(level, false) + "] ";
		if (module != "") {
			buffer += module + ": ";
		}
		buffer += ofVAArgsToString(format, args);
		buffer += "\n";

		if (isAMPMLoggingLevel(level)) {
			ampm::ampm()->log(getLogLevel(level), buffer);
		}
		else {
			OutputDebugStringA(buffer.c_str());
		}
	}

	bool AMPMLoggerChannel::isAMPMLoggingLevel(ofLogLevel level)
	{
		if (std::find(m_levelsToLog.begin(), m_levelsToLog.end(), level) != m_levelsToLog.end()) {
			return true;
		}
		return false;
	}

	// ----------------------
	// client
	// ----------------------

	AMPMClient* AMPMClient::sInstance = nullptr;

	void AMPMClient::init(int sendPort, int recvPort, int serverPORT)
	{
		sInstance = new AMPMClient(sendPort, recvPort, serverPORT);
	}

	void AMPMClient::set(AMPMClient* instance)
	{
		sInstance = instance;
	}

	AMPMClient* AMPMClient::get()
	{
		return sInstance;
	}

	AMPMClient::~AMPMClient()
	{
		sInstance = nullptr;
	}

	AMPMClient::AMPMClient(int sendPort, int recvPort, int serverPORT)
	{
		// setup osc
		mSender.setup("localhost", sendPort);

		mListener.setup(recvPort);

		m_serverPort = serverPORT;

		std::shared_ptr<AMPMLoggerChannel> ampmLogChannel_p = std::make_shared<AMPMLoggerChannel>();
		ofSetLoggerChannel(ampmLogChannel_p);
	}

	ofJson AMPMClient::getConfig()
	{
		ofJson config;

		try {
			config = ofLoadJson("http://localhost:" + ofToString(m_serverPort) + "/config");
		}
		catch (const std::exception& ex) {
			ofLogFatalError() << ex.what();
		}

		return config;
	}

	void AMPMClient::update()
	{
		// send heartbeat
		sendHeartbeat();
	}

	// send heartbeat to server
	void AMPMClient::sendHeartbeat()
	{
		ofxOscMessage message;
		message.setAddress("/heart");
		mSender.sendMessage(message, false);
	}

	// send analytics event to server
	void AMPMClient::sendEvent(std::string category, std::string action, std::string label, int value)
	{
		ofxOscMessage message;
		message.setAddress("/event");

		ofJson arguments = ofJson{ { "Category", category }, { "Action", action }, { "Label", label }, { "Value", value } };
		message.addStringArg(arguments.dump());

		mSender.sendMessage(message);
	}

	// send log event to server
	void AMPMClient::log(LogEventLevel level, std::string msg)
	{
		ofxOscMessage message;
		message.setAddress("/log");

		ofJson arguments = ofJson{ { "level", LogEventLevelToString.at(level) }, { "message", msg } };
		message.addStringArg(arguments.dump());
		mSender.sendMessage(message);
	}

	// send custom osc message
	void AMPMClient::sendCustomMessage(std::string address, ofJson msg)
	{
		ofxOscMessage message;
		message.setAddress(address);
		message.addStringArg(msg.dump());
		mSender.sendMessage(message);
	}

	// strip out file for sending as part of log info
	char const* AMPMClient::getFileForLog(char const* file)
	{
		return strrchr(file, '\\') ? strrchr(file, '\\') + 1 : file;
	}

}  // namespace ampm
