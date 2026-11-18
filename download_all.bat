@echo off
setlocal

set "YTDLP=%~dp0yt-dlp.exe"

if not exist "downloads" mkdir downloads

rem URL from argument or prompt; empty input = bulk search from songs.txt
set "URL=%~1"
if "%URL%"=="" set /p "URL=Paste YouTube / YouTube Music link (playlist or track), or press Enter to use songs.txt: "
if not "%URL%"=="" goto :link

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
goto :done

:link
echo Downloading: %URL%
"%YTDLP%" "%URL%" ^
    --yes-playlist ^
    --ignore-errors ^
    --download-archive "downloads/archive.txt" ^
    --extract-audio ^
    --audio-format mp3 ^
    --audio-quality 0 ^
    --prefer-ffmpeg ^
    -o "downloads/%%(playlist_title|Singles)s/%%(playlist_index&{} |)s%%(title)s.%%(ext)s" ^
    %2 %3 %4 %5 %6 %7 %8 %9

:done
echo ----------------------------------------------------------
echo DONE! MP3 files saved in /downloads
if "%~1"=="" pause
