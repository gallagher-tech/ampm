#include "ofApp.h"

//--------------------------------------------------------------
void ofApp::setup() {
	// NOTE: modify ampm.json to launch the correct binary file


	ampm::AMPMClient::init(3002, 3003, 8888);
	mAMPMInstance = ampm::ampm();

	ofLogNotice() << ("started ampm instance");
	ampm::AMPMClient::set(mAMPMInstance);

}

//--------------------------------------------------------------
void ofApp::update() {
	// sending ampm heartbeat
	ampm::ampm()->update();

}

//--------------------------------------------------------------
void ofApp::draw() {

}

//--------------------------------------------------------------
void ofApp::keyPressed(int key) {

	if (key == 'a') {
		ofLogNotice() << "notice won't be captured";
	}
	if (key == 'e') {
		ofLogError() << "error will be captured";
	}
	if (key == 'r') {
		ampm::AMPM_LOG_WARN("call ampm logging directly if needed");
	}

}

//--------------------------------------------------------------
void ofApp::keyReleased(int key) {

}

//--------------------------------------------------------------
void ofApp::mouseMoved(int x, int y) {

}

//--------------------------------------------------------------
void ofApp::mouseDragged(int x, int y, int button) {

}

//--------------------------------------------------------------
void ofApp::mousePressed(int x, int y, int button) {

}

//--------------------------------------------------------------
void ofApp::mouseReleased(int x, int y, int button) {

}

//--------------------------------------------------------------
void ofApp::mouseEntered(int x, int y) {

}

//--------------------------------------------------------------
void ofApp::mouseExited(int x, int y) {

}

//--------------------------------------------------------------
void ofApp::windowResized(int w, int h) {

}

//--------------------------------------------------------------
void ofApp::gotMessage(ofMessage msg) {

}

//--------------------------------------------------------------
void ofApp::dragEvent(ofDragInfo dragInfo) {

}
