@echo off
setlocal enabledelayedexpansion

set YTDLP=yt-dlp.exe

if not exist "downloads" mkdir downloads

echo Starting bulk download...

for /f "usebackq delims=" %%A in ("songs.txt") do (
    echo --------------------------------------------------------
    echo Downloading: %%A
    "%YTDLP%" "ytsearch1:%%A" ^
        --extract-audio ^
        --audio-format mp3 ^
        --audio-quality 0 ^
        --prefer-ffmpeg ^
        -o "downloads/%%(title)s.%%(ext)s"
)

echo ----------------------------------------------------------
echo DONE! MP3 files saved in /downloads
pause
