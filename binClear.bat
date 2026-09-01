@echo off
set "root_dir=%~dp0"
set "exclude_dir1=%root_dir%Src\Gate.Spa"
set "exclude_dir2=%root_dir%Src\Tools"

echo Scanning and deleting all 'bin' directories in %root_dir%
for /d /r "%root_dir%" %%d in (bin) do (
    if exist "%%d" (
        echo "%%d" | findstr /I /C:"%exclude_dir1%" /C:"%exclude_dir2%" >nul
        if errorlevel 1 (
            echo Deleting "%%d"
            rd /s /q "%%d"
        ) else (
            echo Skipping "%%d"
        )
    )
)

echo Scanning and deleting all 'obj' directories in %root_dir%
for /d /r "%root_dir%" %%d in (obj) do (
    if exist "%%d" (
        echo "%%d" | findstr /I /C:"%exclude_dir1%" /C:"%exclude_dir2%" >nul
        if errorlevel 1 (
            echo Deleting "%%d"
            rd /s /q "%%d"
        ) else (
            echo Skipping "%%d"
        )
    )
)

echo Done.
pause