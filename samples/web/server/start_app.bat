cd "C:\Users\<User Name>\<Project Name>\server"
taskkill /F /IM chrome* /T
node ".\node_modules\.bin\ampm"
taskkill /f /IM explorer.exe