# On Windows

Tested working with Unity 2022.3.31f1

## To open the project

- Make sure you have Unity3D v5.3.4f1 or later
- Start Unity3D; Browse and open the Unity Project in the "Source" folder


## Download Unity Packages

- On Windows, in Unity, go to Window → Package Manager
- In the dropdown at the top left, select Unity Registry
- Search for "Unity UI" (also known as UGUI) and install it.
- In the same Package Manager window, search for "TextMeshPro"
- Install it if it isn't already.


## Download In-game Debug Console for Unity 3D 

There are 5 ways to install this plugin, just <u>choose 1<u>:

Option 1: Import [IngameDebugConsole.unitypackage](https://github.com/yasirkula/UnityIngameDebugConsole/releases) via Assets-Import Package

Option 2: Clone/download [UnityIngameDebugConsole Repository](https://github.com/yasirkula/UnityIngameDebugConsole) and move the Plugins folder to your Unity project's Assets folder

Option 3: Import it from [Asset Store](https://assetstore.unity.com/packages/tools/gui/in-game-debug-console-68068)

Option 4: (via Package Manager) click the + button and install the package from the following git URL:
https://github.com/yasirkula/UnityIngameDebugConsole.git

Option 5: (via [OpenUPM](https://openupm.com/)) after installing [openupm-cli](https://github.com/openupm/openupm-cli), run the following command:
openupm add com.yasirkula.ingamedebugconsole

For more info, visit the [UnityIngameDebugConsole Repository](https://github.com/yasirkula/UnityIngameDebugConsole)


## To run the sample

- Within the Unity3D folder, make a new directory called "Build"
- Build the project to "Build" directory
- Copy the file ampm.json to the "Build" directory. Edit the ampm.json file so that the "launchCommand" property holds the name of your build file.
- From a command line window navigate to the "Build" directory and run `ampm` to run in production mode, or `ampm ampm.json dev` to run in dev mode.
- Make sure the Unity window that opens up is active (ie. you should not have any other tabs open in front of the Unity window)


## How to integrate AMPM with your Unity3D Application:

- To integrate ampm with your app, drag and drop the AmpmCommunicator.cs script onto your Main Camera or any empty Game Object.
- To use additional AMPM methods in any of your other custom classes, add the line "using AmpmLib" on top your class declaration.
- Build the file to a directory.


## To start the app with AMPM

- Copy the file ampm.json to the same directory as the unity build. Edit the ampm.json file so that the "launchCommand" property holds the name of your build file.
- From a command line window navigate to your "Build" directory and run `ampm` to run in production mode, or `ampm ampm.json dev` to run in dev mode.
