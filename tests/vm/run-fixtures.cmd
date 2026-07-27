@echo off
REM Runs every fixture through --ocr-file and prints actual vs expected.
REM Globs *.png so new fixtures are picked up without editing this file.
cd /d C:\sar-test
for %%f in (*.png) do (
  if exist %%~nf.expected.txt (
    echo ===== %%f
    echo --- actual:
    SelectAndRead.exe --ocr-file %%f
    echo --- expected:
    type %%~nf.expected.txt
    echo.
  )
)
