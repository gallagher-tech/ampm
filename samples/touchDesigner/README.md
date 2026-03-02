# TouchDesigner Sample
Sample using AMPM to run a TouchDesigner file as well as a tox file that can be dragged into any TouchDesigner file to enable AMPM

## Run this Sample
To run this sample:
* Install TouchDesigner. This sample was built using 2025.32280 so if you use a different version you might get a dialog popup the first time you run it
* Run `ampm ampm.json` to launch the touch file.

## Configure Your App for AMPM
* Drag the `ampm.tox` file into your TouchDesigner file
* Configure the paramaters in the "Config" folder. The port should be set correctly, but change it to match what you are using in your AMPM config file
* Model your `ampm.json` file after the one in this sample
	* If you have multiple TouchDesigner versions installed and you have a specific one you want to use you may need to edit the launchCommand to run the exact TouchDesigner binary. For example:
	```js
	"launchCommand": "C:\\Program Files\\Derivative\\TouchDesigner\\bin\\TouchDesigner.exe ampm-sample.toe"
	```
