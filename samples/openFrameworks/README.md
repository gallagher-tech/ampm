# On Windows

## Build an OpenFrameworks app
- Make sure you have [OpenFrameworks](https://openframeworks.cc/download/) downloaded. 
- Go to your openFrameworks directory
- In openFrameworks/projectGenerator, run projectGenerator.exe. 
- In projectGenerator, select the directory that this README file is saved in (ie. Users > Your Username > Downloads > ampm > samples > openFrameworks)
- Click "Generate" to create the .sln and bin/ folders

## Build the openFrameworks project
- Open the .sln file generated in Visual Studio on Windows (Use XCode on Mac).
- Cntl + Shift + B to build the project 
- Once built, you should see openFrameworks_debug.exe being generated in "bin" directory. 
- In "scripts/ampm.json", the "LaunchCommand" property holds the correct path for openFrameworks_debug.exe

## Run the sample with ampm server
- In terminal, cd into scripts directory
- Run `ampm`. An OpenFrameworks sketch should open.
