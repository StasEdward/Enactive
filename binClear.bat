@echo off
set "root_dir=%~dp0"

echo Scanning and deleting all 'bin' and 'obj' directories in %root_dir%
for /d /r "%root_dir%" %%d in (bin obj) do (
    if exist "%%d" (
        echo Deleting "%%d"
        rd /s /q "%%d"
    )
)

echo Done.
pause
