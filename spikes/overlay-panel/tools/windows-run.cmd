@echo off
rem Runs the overlay spike in the interactive session and writes C:\juvin-spike\overlay\results.txt.
C:\juvin-spike\overlay\juvin-overlay-spike.exe --cover --seconds 30 > C:\juvin-spike\overlay\results.txt 2>&1
