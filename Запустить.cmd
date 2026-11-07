@echo off
setlocal
pushd "%~dp0" || exit /b 1

if not exist ".venv\Scripts\python.exe" goto create_environment
if not exist ".venv\Scripts\pythonw.exe" goto create_environment
goto check_environment

:create_environment
echo Preparing Python environment...
set "PYTHON=%LOCALAPPDATA%\Programs\Python\Python312\python.exe"
if not exist "%PYTHON%" goto try_system_python
"%PYTHON%" -c "import sys, struct; sys.exit(0 if sys.version_info[:2] == (3, 12) and struct.calcsize('P') == 8 else 1)" >nul 2>&1
if not errorlevel 1 goto use_python

:try_system_python
set "PYTHON=%ProgramFiles%\Python312\python.exe"
if not exist "%PYTHON%" goto try_python_launcher
"%PYTHON%" -c "import sys, struct; sys.exit(0 if sys.version_info[:2] == (3, 12) and struct.calcsize('P') == 8 else 1)" >nul 2>&1
if not errorlevel 1 goto use_python

:try_python_launcher
where py.exe >nul 2>&1
if errorlevel 1 goto missing_python
py -3.12 -c "import sys, struct; sys.exit(0 if sys.version_info[:2] == (3, 12) and struct.calcsize('P') == 8 else 1)" >nul 2>&1
if errorlevel 1 goto missing_python
py -3.12 -m venv ".venv"
goto environment_created

:use_python
"%PYTHON%" -m venv ".venv"

:environment_created
if errorlevel 1 goto failed

:check_environment
".venv\Scripts\python.exe" -c "import sys, struct; sys.exit(0 if sys.version_info[:2] == (3, 12) and struct.calcsize('P') == 8 else 1)" >nul 2>&1
if errorlevel 1 goto invalid_environment
".venv\Scripts\python.exe" -c "import av, ctranslate2, faster_whisper, huggingface_hub, keyboard, numpy, onnxruntime, PIL, pystray, sounddevice" >nul 2>&1
if not errorlevel 1 goto launch

echo Installing dependencies. Internet access is required; please wait...
".venv\Scripts\python.exe" -m pip install -r "requirements.txt"
if errorlevel 1 goto failed
".venv\Scripts\python.exe" -c "import av, ctranslate2, faster_whisper, huggingface_hub, keyboard, numpy, onnxruntime, PIL, pystray, sounddevice"
if errorlevel 1 goto failed

:launch
start "" "%CD%\.venv\Scripts\pythonw.exe" "%CD%\main.py"
if errorlevel 1 goto failed
popd
exit /b 0

:missing_python
echo Python 3.12 64-bit was not found. Install it, then open this file again.
echo See README.md for installation instructions.
goto failed

:invalid_environment
echo The existing .venv requires Python 3.12 64-bit.
echo Rename the .venv folder and open this file again.

:failed
echo Setup or launch failed. See the messages above and README.md.
pause
popd
exit /b 1
