@echo off
title Cloudflare Tunnel - linenlady
echo Starting Cloudflare Tunnel: linenlady...
echo.
cloudflared tunnel run linenlady
echo.
echo Tunnel stopped. Press any key to close.
pause >nul
